using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Pitstop.Models;
using Pitstop.Native;

namespace Pitstop.Services;

public sealed class ElevatedResult
{
    public bool Declined { get; init; }
    public ElevatedStatus Status { get; init; } = new();
}

/// <summary>
/// UI side: Pitstop never runs as admin. For installs and game-setting writes it starts a second copy of
/// itself with "runas" (one UAC prompt), hands it a job file and follows its status file.
/// </summary>
public static class ElevatedRunner
{
    public static async Task<ElevatedResult> RunAsync(ElevatedJob job, Action<ElevatedStatus>? onStatus, CancellationToken ct)
    {
        Directory.CreateDirectory(Paths.Jobs);
        var id = Guid.NewGuid().ToString("N");
        var jobPath = Path.Combine(Paths.Jobs, id + ".json");
        job.StatusFile = Path.Combine(Paths.Jobs, id + ".status.json");
        File.WriteAllText(jobPath, JsonSerializer.Serialize(job, PitstopJson.Default.ElevatedJob));

        try
        {
            Process proc;
            try
            {
                proc = Process.Start(new ProcessStartInfo(Paths.ExePath, $"--elevated \"{jobPath}\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Paths.ExeDir,
                })!;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED: UAC declined
            {
                return new ElevatedResult { Declined = true };
            }

            using (proc)
            {
                string last = "";
                ElevatedStatus? status = null;
                while (!proc.HasExited)
                {
                    await Task.Delay(300, CancellationToken.None);
                    if (TryRead(job.StatusFile, ref last) is { } s)
                    {
                        status = s;
                        onStatus?.Invoke(s);
                    }
                }
                await Task.Delay(100, CancellationToken.None);
                if (TryRead(job.StatusFile, ref last) is { } fin) status = fin;
                status ??= new ElevatedStatus { Finished = true, Ok = false, Error = $"The admin helper stopped unexpectedly (exit code {proc.ExitCode})." };
                if (!status.Finished)
                {
                    status.Ok = false;
                    if (status.Error.Length == 0) status.Error = $"The admin helper stopped before finishing (exit code {proc.ExitCode}).";
                }
                return new ElevatedResult { Status = status };
            }
        }
        finally
        {
            try { File.Delete(jobPath); } catch { }
            try { File.Delete(job.StatusFile); } catch { }
        }
    }

    private static ElevatedStatus? TryRead(string path, ref string last)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            if (text == last || text.Length == 0) return null;
            last = text;
            return JsonSerializer.Deserialize(text, PitstopJson.Default.ElevatedStatus);
        }
        catch
        {
            return null;   // mid-write; next poll gets it
        }
    }
}

/// <summary>Admin side: "Pitstop.exe --elevated job.json". No window; reports through the status file.</summary>
public static class ElevatedHelper
{
    public static int Run(string jobPath)
    {
        ElevatedJob? job;
        try
        {
            if (!Paths.IsInsideData(jobPath)) return 2;
            job = JsonSerializer.Deserialize(File.ReadAllText(jobPath), PitstopJson.Default.ElevatedJob);
            if (job is null || !Paths.IsInsideData(job.StatusFile)) return 2;
        }
        catch (Exception ex)
        {
            Log.Error("elevated: read job", ex);
            return 2;
        }

        var status = new ElevatedStatus();
        void Report(string step, string message)
        {
            status.Step = step;
            status.Message = message;
            Write(job.StatusFile, status);
        }

        try
        {
            Log.Info($"elevated job: {job.Kind}");
            switch (job.Kind)
            {
                case "install":
                    Install(job, status, Report);
                    break;
                case "profiles":
                    Report("profiles", "Saving your game settings to the driver…");
                    DrsService.Apply(job.Profiles, status.Warnings);
                    status.Ok = true;
                    break;
                default:
                    throw new InvalidOperationException("Unknown job " + job.Kind);
            }
        }
        catch (Exception ex)
        {
            Log.Error("elevated job", ex);
            status.Ok = false;
            status.Error = ex.Message;
        }
        status.Finished = true;
        Report("done", status.Ok ? "Done." : status.Error);
        return status.Ok ? 0 : 1;
    }

    private static void Install(ElevatedJob job, ElevatedStatus status, Action<string, string> report)
    {
        if (!Paths.IsInsideData(job.WorkDir)) throw new InvalidOperationException("The install folder isn't Pitstop's.");
        var setup = Path.Combine(job.WorkDir, "setup.exe");
        if (!File.Exists(setup)) throw new FileNotFoundException("NVIDIA's setup.exe is missing from the unpacked driver.");

        // The job file lives in a user-writable folder, so check setup.exe really is NVIDIA's before running it as admin.
        var problem = WinTrust.CheckSignedBy(setup, "NVIDIA Corporation");
        if (problem is not null) throw new InvalidDataException($"NVIDIA's installer failed its safety check: {problem}.");

        if (job.RestorePoint)
        {
            report("restore", "Creating a Windows restore point so you can undo this if you ever need to…");
            var (ok, message) = RestorePoint.Create(job.RestorePointName);
            status.RestorePointMade = ok;
            if (!ok) status.Warnings.Add("No restore point was made: " + message);
        }

        report("install", "Installing the driver. Your screen may flicker or go black for a few seconds. That's normal.");
        var args = "-s -noreboot -noeula -nofinish -nosplash" + (job.Clean ? " -clean" : "");
        Log.Info($"running setup.exe {args}");
        var psi = new ProcessStartInfo(setup, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = job.WorkDir,
        };
        using (var p = Process.Start(psi)!)
        {
            if (!p.WaitForExit((int)TimeSpan.FromMinutes(40).TotalMilliseconds))
                throw new TimeoutException("NVIDIA's installer is still running after 40 minutes. Check Device Manager, then restart Windows.");
            status.ExitCode = p.ExitCode;
            Log.Info($"setup.exe exit code {p.ExitCode}");
        }

        // 0 = installed. 1 = installed, restart needed. Anything else is NVIDIA's own failure code.
        if (status.ExitCode is not (0 or 1))
            throw new InvalidOperationException($"NVIDIA's installer reported a problem (code {status.ExitCode}). Your previous driver should still be in place.");
        status.RestartNeeded = status.ExitCode == 1;

        if (job.DisableTelemetry)
        {
            report("telemetry", "Turning off NVIDIA's telemetry tasks…");
            try
            {
                status.TasksDisabled = Telemetry.DisableAll();
            }
            catch (Exception ex)
            {
                status.Warnings.Add("Couldn't turn off the telemetry tasks: " + ex.Message);
            }
        }
        status.Ok = true;
    }

    private static void Write(string path, ElevatedStatus s)
    {
        try
        {
            Files.WriteAtomic(path, JsonSerializer.Serialize(s, PitstopJson.Default.ElevatedStatus));
        }
        catch (Exception ex)
        {
            Log.Warn("status write: " + ex.Message);
        }
    }
}
