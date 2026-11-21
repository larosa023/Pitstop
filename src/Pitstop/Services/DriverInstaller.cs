using Pitstop.Models;
using Pitstop.Native;

namespace Pitstop.Services;

public sealed class InstallRequest
{
    /// <summary>A driver from NVIDIA's lookup (downloaded unless it's already saved)...</summary>
    public DriverRelease? Release { get; init; }
    /// <summary>...or a saved package (rollback / reinstall).</summary>
    public SavedPackage? Saved { get; init; }
    public bool PhysX { get; init; }
    public bool HdAudio { get; init; }
    public bool UsbC { get; init; }
    public bool Clean { get; init; }
    public bool RestorePoint { get; init; }
    public bool DisableTelemetry { get; init; }
    public bool IsRollback { get; init; }
    public string FromVersion { get; init; } = "";
    public bool IsNotebook { get; init; }

    public string Version => Release?.Version ?? Saved?.Meta.Version ?? "";
    public DriverBranch Branch => Release?.Branch ?? Saved?.Branch ?? DriverBranch.GameReady;
}

public enum InstallStage { Download, Verify, Unpack, Prepare, RestorePoint, Install, Telemetry, Finish }

public sealed class InstallUpdate
{
    public InstallStage Stage { get; init; }
    public string Message { get; init; } = "";
    /// <summary>0..1, or -1 when there's no way to know (NVIDIA's installer reports nothing).</summary>
    public double Progress { get; init; } = -1;
    public string Detail { get; init; } = "";
}

public sealed class InstallOutcome
{
    public bool Ok { get; set; }
    public bool Cancelled { get; set; }
    public string Message { get; set; } = "";
    public List<string> Warnings { get; } = new();
    public bool RestartNeeded { get; set; }
    public string InstalledVersion { get; set; } = "";
    public List<string> Components { get; } = new();
}

/// <summary>
/// Download → verify → unpack only the chosen parts → (one admin prompt) restore point, NVIDIA's setup
/// in silent mode, telemetry tasks off. Everything before the admin prompt can be cancelled.
/// </summary>
public static class DriverInstaller
{
    public static readonly (string folder, string label)[] Required = { ("Display.Driver", "Display driver"), ("NVI2", "Installer core") };
    public const string PhysXFolder = "PhysX", HdAudioFolder = "HDAudio", UsbCFolder = "PPC";
    private static readonly string[] NotebookFolders = { "Display.Optimus", "NVPCF" };

    /// <summary>Set while an install runs; Pitstop's own updater waits for it.</summary>
    public static bool IsBusy { get; private set; }

    public static HashSet<string> FoldersFor(InstallRequest r)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Display.Driver", "NVI2" };
        if (r.PhysX) set.Add(PhysXFolder);
        if (r.HdAudio) set.Add(HdAudioFolder);
        if (r.UsbC) set.Add(UsbCFolder);
        if (r.IsNotebook) foreach (var f in NotebookFolders) set.Add(f);
        return set;
    }

    /// <summary>Root files (setup.exe, setup.cfg, license text) plus the chosen component folders.</summary>
    public static Func<string, bool> IncludeFilter(HashSet<string> folders) => key =>
    {
        int slash = key.IndexOf('/');
        return slash < 0 || folders.Contains(key[..slash]);
    };

    public static async Task<InstallOutcome> RunAsync(InstallRequest req, Action<InstallUpdate> report, CancellationToken ct)
    {
        var outcome = new InstallOutcome();
        if (IsBusy)
        {
            outcome.Message = "Another install is already running.";
            return outcome;
        }
        IsBusy = true;
        string? workDir = null;
        try
        {
            // ---- 1. the package
            SavedPackage package;
            if (req.Saved is { } saved)
            {
                report(new() { Stage = InstallStage.Verify, Message = $"Checking the saved {req.Version} package…" });
                await Task.Run(() => VerifySaved(saved), ct);
                package = saved;
            }
            else if (req.Release is { } release && PackageStore.Find(release.Version, release.Branch) is { } existing && TryVerifySaved(existing))
            {
                report(new() { Stage = InstallStage.Verify, Message = $"Using the {release.Version} package you already downloaded." });
                package = existing;
            }
            else if (req.Release is { } rel)
            {
                package = await DownloadAsync(rel, report, ct);
            }
            else
            {
                throw new InvalidOperationException("Nothing to install.");
            }

            // ---- 2. unpack only what's needed
            ct.ThrowIfCancellationRequested();
            var folders = FoldersFor(req);
            long need = package.Meta.Size * 3;
            if (Files.FreeSpace(Paths.Work) < need)
                throw new IOException($"Not enough free disk space to unpack the driver (needs about {Fmt.Bytes(need)}).");
            workDir = Path.Combine(Paths.Work, $"{package.Id}-{DateTime.Now:yyyyMMddHHmmss}");
            report(new() { Stage = InstallStage.Unpack, Message = "Unpacking the parts you picked…", Progress = 0 });
            var exe = package.ExePath;
            var dir = workDir;
            await Task.Run(() => Extractor.Extract(exe, dir, IncludeFilter(folders),
                p => report(new() { Stage = InstallStage.Unpack, Message = "Unpacking the parts you picked…", Progress = p }), ct), ct);

            // ---- 3. strip the package down to those parts
            report(new() { Stage = InstallStage.Prepare, Message = "Preparing a driver-only install…" });
            var missing = Prepare(workDir, folders, outcome.Components);
            outcome.Warnings.AddRange(missing);
            ct.ThrowIfCancellationRequested();

            // ---- 4. the admin part
            report(new() { Stage = req.RestorePoint ? InstallStage.RestorePoint : InstallStage.Install, Message = "Waiting for you to approve the admin prompt…" });
            var job = new ElevatedJob
            {
                Kind = "install",
                WorkDir = workDir,
                Version = req.Version,
                Clean = req.Clean,
                RestorePoint = req.RestorePoint,
                DisableTelemetry = req.DisableTelemetry,
                RestorePointName = $"Pitstop: before NVIDIA driver {req.Version}",
            };
            var status = await ElevatedRunner.RunAsync(job, s => report(MapStatus(s)), ct);
            if (status.Declined)
            {
                outcome.Cancelled = true;
                outcome.Message = "You declined the admin prompt, so nothing was changed.";
                return outcome;
            }
            outcome.Warnings.AddRange(status.Status.Warnings);
            if (!status.Status.Ok)
            {
                outcome.Message = status.Status.Error.Length > 0 ? status.Status.Error : "NVIDIA's installer didn't finish.";
                return outcome;
            }

            // ---- 5. did it take?
            report(new() { Stage = InstallStage.Finish, Message = "Checking the result…" });
            await Task.Delay(1500, CancellationToken.None);
            var now = GpuService.InstalledVersionFromRegistry() ?? "";
            outcome.InstalledVersion = now;
            outcome.Ok = true;
            outcome.RestartNeeded = status.Status.RestartNeeded || (now.Length > 0 && now != req.Version);
            outcome.Message = outcome.RestartNeeded
                ? $"Driver {req.Version} is installed. Restart Windows to finish."
                : $"Driver {req.Version} is installed and running.";
            if (status.Status.TasksDisabled > 0) outcome.Warnings.Add($"Turned off {status.Status.TasksDisabled} NVIDIA telemetry task(s).");

            PackageStore.MarkInstalled(package);
            PackageStore.Prune(package.Id);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            outcome.Cancelled = true;
            outcome.Message = "Cancelled. Nothing was changed.";
            return outcome;
        }
        catch (Exception ex)
        {
            Log.Error("install", ex);
            outcome.Message = ex.Message;
            return outcome;
        }
        finally
        {
            if (workDir is not null) _ = Task.Run(() => Files.TryDeleteDirectory(workDir));
            IsBusy = false;
        }
    }

    private static InstallUpdate MapStatus(ElevatedStatus s) => new()
    {
        Stage = s.Step switch
        {
            "restore" => InstallStage.RestorePoint,
            "telemetry" => InstallStage.Telemetry,
            "done" => InstallStage.Finish,
            _ => InstallStage.Install,
        },
        Message = s.Message,
    };

    public static async Task<SavedPackage> DownloadAsync(DriverRelease rel, Action<InstallUpdate> report, CancellationToken ct)
    {
        if (!DriverCatalog.IsOfficialDownload(rel.DownloadUrl))
            throw new InvalidOperationException("That download isn't on NVIDIA's servers, so Pitstop won't use it.");
        if (Files.FreeSpace(Paths.Downloads) < 3L << 30)
            throw new IOException("Not enough free disk space to download the driver (needs about 3 GB).");

        var partial = Path.Combine(Paths.Downloads, rel.FileName + ".partial");
        string sha = "";
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                report(new() { Stage = InstallStage.Download, Message = $"Downloading {rel.Version} from NVIDIA…", Progress = 0 });
                sha = await Http.DownloadAsync(rel.DownloadUrl, partial, (done, total, speed) => report(new()
                {
                    Stage = InstallStage.Download,
                    Message = $"Downloading {rel.Version} from NVIDIA…",
                    Progress = total > 0 ? (double)done / total : -1,
                    Detail = total > 0 ? $"{Fmt.Bytes(done)} of {Fmt.Bytes(total)} · {Fmt.Speed(speed)}" : Fmt.Bytes(done),
                }), ct);
                break;
            }
            catch (Exception ex) when (attempt < 3 && ex is HttpRequestException or IOException && !ct.IsCancellationRequested)
            {
                Log.Warn($"download attempt {attempt}: {ex.Message}");
                await Task.Delay(2000 * attempt, ct);
            }
            catch
            {
                try { File.Delete(partial); } catch { }
                throw;
            }
        }

        report(new() { Stage = InstallStage.Verify, Message = "Checking NVIDIA's digital signature…" });
        var problem = await Task.Run(() => WinTrust.CheckSignedBy(partial, "NVIDIA Corporation"), ct);
        if (problem is not null)
        {
            try { File.Delete(partial); } catch { }
            throw new InvalidDataException($"The download failed its safety check: {problem}. It was deleted.");
        }
        return PackageStore.Add(partial, rel, sha);
    }

    private static bool TryVerifySaved(SavedPackage p)
    {
        try
        {
            VerifySaved(p);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"saved package {p.Id} failed verification: {ex.Message}");
            return false;
        }
    }

    /// <summary>Same file we downloaded (SHA-256) and still signed by NVIDIA.</summary>
    public static void VerifySaved(SavedPackage p)
    {
        var sha = Http.Sha256File(p.ExePath);
        if (p.Meta.Sha256.Length > 0 && !sha.Equals(p.Meta.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The saved {p.Meta.Version} package has changed since it was downloaded, so Pitstop won't use it. Delete it and download again.");
        var problem = WinTrust.CheckSignedBy(p.ExePath, "NVIDIA Corporation");
        if (problem is not null) throw new InvalidDataException($"The saved {p.Meta.Version} package failed its safety check: {problem}.");
    }

    /// <summary>
    /// Makes the unpacked folder install only what's there: setup.cfg references NVIDIA's EULA, consent and
    /// privacy pages from packages Pitstop leaves out, so those lines go. Returns notes about missing parts.
    /// </summary>
    public static List<string> Prepare(string workDir, HashSet<string> folders, List<string> installed)
    {
        var notes = new List<string>();
        if (!File.Exists(Path.Combine(workDir, "setup.exe")) || !File.Exists(Path.Combine(workDir, "setup.cfg")))
            throw new InvalidDataException("This package doesn't look like an NVIDIA driver (no setup.exe/setup.cfg).");
        if (!Directory.Exists(Path.Combine(workDir, "Display.Driver")))
            throw new InvalidDataException("This package has no display driver in it.");

        var cfg = Path.Combine(workDir, "setup.cfg");
        var lines = File.ReadAllLines(cfg);
        var kept = lines.Where(l => !(l.Contains("EulaHtmlFile", StringComparison.OrdinalIgnoreCase) ||
                                      l.Contains("FunctionalConsentFile", StringComparison.OrdinalIgnoreCase) ||
                                      l.Contains("PrivacyPolicyFile", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (kept.Length != lines.Length) File.WriteAllLines(cfg, kept);

        foreach (var (folder, label) in new[] { ("Display.Driver", "Display driver"), (PhysXFolder, "PhysX"), (HdAudioFolder, "HD Audio"), (UsbCFolder, "USB-C driver"), ("Display.Optimus", "Optimus"), ("NVPCF", "Platform controller") })
        {
            if (!folders.Contains(folder)) continue;
            if (Directory.Exists(Path.Combine(workDir, folder))) installed.Add(label);
            else if (folder is PhysXFolder or HdAudioFolder or UsbCFolder) notes.Add($"{label} isn't in this driver package, so it was skipped.");
        }
        return notes;
    }
}
