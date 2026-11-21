using Microsoft.Win32;

namespace Pitstop.Services;

/// <summary>Windows restore points (admin only).</summary>
public static class RestorePoint
{
    private const string Key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string Frequency = "SystemRestorePointCreationFrequency";

    /// <summary>
    /// Windows normally allows one restore point per 24 hours, so the limit is lifted for this one call
    /// and put back right after.
    /// </summary>
    public static (bool ok, string message) Create(string description)
    {
        object? previous = null;
        bool changed = false;
        try
        {
            using (var k = Registry.LocalMachine.CreateSubKey(Key, true))
            {
                previous = k.GetValue(Frequency);
                k.SetValue(Frequency, 0, RegistryValueKind.DWord);
                changed = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("restore point frequency: " + ex.Message);
        }

        try
        {
            var safe = description.Replace("'", "''");
            var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var (code, output) = Shell.Run(ps,
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description '{safe}' -RestorePointType DEVICE_DRIVER_INSTALL -ErrorAction Stop\"",
                TimeSpan.FromMinutes(4));
            if (code == 0) return (true, "");
            Log.Warn($"restore point failed ({code}): {output}");
            if (output.Contains("disabled", StringComparison.OrdinalIgnoreCase) || output.Contains("0x80042306", StringComparison.OrdinalIgnoreCase))
                return (false, "System Restore is turned off on this PC (Windows Settings → System → About → System protection).");
            if (code == -1) return (false, "Windows took too long to create it.");
            var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? $"code {code}";
            return (false, line);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            if (changed)
            {
                try
                {
                    using var k = Registry.LocalMachine.CreateSubKey(Key, true);
                    if (previous is null) k.DeleteValue(Frequency, false);
                    else k.SetValue(Frequency, previous, RegistryValueKind.DWord);
                }
                catch { }
            }
        }
    }
}

/// <summary>Turns off NVIDIA's telemetry/update scheduled tasks and the old telemetry service (admin only).</summary>
public static class Telemetry
{
    private static readonly string[] TaskPrefixes =
    {
        "NvTm",                                   // NvTmRep_CrashReport*, NvTmMon_*, NvTmRepOnLogon_*
        "NvDriverUpdateCheck",                    // NvDriverUpdateCheckDaily_*
        "NvNodeLauncher",
        "NVIDIA GeForce Experience SelfUpdate",
        "NvAppSelfUpdate",
    };

    public static List<string> FindTasks()
    {
        var list = new List<string>();
        var (code, output) = Shell.Run("schtasks.exe", "/Query /FO CSV /NH", TimeSpan.FromSeconds(60));
        if (code != 0) return list;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('"')) continue;
            int end = line.IndexOf('"', 1);
            if (end < 0) continue;
            var path = line[1..end];
            var name = path[(path.LastIndexOf('\\') + 1)..];
            if (TaskPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) && !list.Contains(path))
                list.Add(path);
        }
        return list;
    }

    public static int DisableAll()
    {
        int n = 0;
        foreach (var task in FindTasks())
        {
            var (code, output) = Shell.Run("schtasks.exe", $"/Change /TN \"{task}\" /Disable", TimeSpan.FromSeconds(30));
            if (code == 0) n++;
            else Log.Warn($"disable task {task}: {output}");
        }

        // The telemetry service from older drivers, if it's still around.
        var (q, _) = Shell.Run("sc.exe", "query NvTelemetryContainer", TimeSpan.FromSeconds(15));
        if (q == 0)
        {
            Shell.Run("sc.exe", "stop NvTelemetryContainer", TimeSpan.FromSeconds(30));
            if (Shell.Run("sc.exe", "config NvTelemetryContainer start= disabled", TimeSpan.FromSeconds(15)).code == 0) n++;
        }
        Log.Info($"telemetry: disabled {n}");
        return n;
    }
}

/// <summary>Start with Windows (HKCU Run key), in the tray.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("Pitstop") is string;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (on) k.SetValue("Pitstop", $"\"{Paths.ExePath}\" --minimized");
            else k.DeleteValue("Pitstop", false);
        }
        catch (Exception ex)
        {
            Log.Error("autostart", ex);
        }
    }

    /// <summary>Keeps the entry pointing at this exe if Pitstop was moved.</summary>
    public static void RefreshPath()
    {
        if (IsEnabled()) Set(true);
    }
}
