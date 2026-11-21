using System.Text.Json.Serialization;

namespace Pitstop.Models;

public enum DriverBranch
{
    GameReady,
    Studio,
}

public static class BranchNames
{
    public static string Display(DriverBranch b) => b == DriverBranch.Studio ? "Studio" : "Game Ready";
    public static string Key(DriverBranch b) => b == DriverBranch.Studio ? "studio" : "gameready";
    public static DriverBranch Parse(string? s) =>
        string.Equals(s, "studio", StringComparison.OrdinalIgnoreCase) ? DriverBranch.Studio : DriverBranch.GameReady;
}

/// <summary>Everything in settings.json. New fields need a default so older files still load.</summary>
public sealed class AppSettings
{
    // Appearance
    public string Theme { get; set; } = "dark";
    /// <summary>"theme" (the theme's own accent), "windows", or "#RRGGBB".</summary>
    public string Accent { get; set; } = "theme";

    // Drivers
    public string PreferredBranch { get; set; } = "gameready";
    public bool InstallPhysX { get; set; }
    public bool InstallHdAudio { get; set; }
    public bool InstallUsbC { get; set; }
    public bool CleanInstall { get; set; }
    public bool CreateRestorePoint { get; set; } = true;
    public bool DisableTelemetry { get; set; } = true;
    public bool CheckDriversOnLaunch { get; set; } = true;

    // Tray
    public bool TrayWatch { get; set; }
    public int CheckHours { get; set; } = 6;
    public bool StartWithWindows { get; set; }
    public string NotifiedVersion { get; set; } = "";

    // Pitstop updates
    public string UpdateChannel { get; set; } = "stable";
    public bool AutoUpdate { get; set; } = true;
    public string SkipVersion { get; set; } = "";
    public string LastRunVersion { get; set; } = "";

    // Window
    public string LastPage { get; set; } = "driver";
    public bool SidebarCollapsed { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
}

public sealed class HistoryEntry
{
    public DateTime Date { get; set; } = DateTime.Now;
    /// <summary>install | clean-install | rollback</summary>
    public string Kind { get; set; } = "install";
    public string Version { get; set; } = "";
    public string Branch { get; set; } = "";
    public string FromVersion { get; set; } = "";
    /// <summary>success | failed | cancelled</summary>
    public string Result { get; set; } = "success";
    public string Details { get; set; } = "";
    public List<string> Components { get; set; } = new();
}

/// <summary>meta.json next to each saved driver package.</summary>
public sealed class PackageMeta
{
    public string Version { get; set; } = "";
    public string Branch { get; set; } = "gameready";
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string SourceUrl { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public DateTime DownloadedAt { get; set; } = DateTime.Now;
    public DateTime? LastInstalledAt { get; set; }
}

// ---------------------------------------------------------------- elevated helper

/// <summary>Written by the UI, read by "Pitstop.exe --elevated job.json" (the only part that runs as admin).</summary>
public sealed class ElevatedJob
{
    /// <summary>install | profiles</summary>
    public string Kind { get; set; } = "install";
    public string StatusFile { get; set; } = "";

    // install
    public string WorkDir { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Clean { get; set; }
    public bool RestorePoint { get; set; }
    public bool DisableTelemetry { get; set; }
    public string RestorePointName { get; set; } = "";

    // profiles
    public List<ProfileChange> Profiles { get; set; } = new();
}

public sealed class ProfileChange
{
    /// <summary>Profile name; empty means the global (base) profile.</summary>
    public string Name { get; set; } = "";
    public bool IsGlobal { get; set; }
    public bool CreateIfMissing { get; set; }
    public List<string> Apps { get; set; } = new();
    public List<SettingChange> Settings { get; set; } = new();
}

public sealed class SettingChange
{
    public uint Id { get; set; }
    public uint Value { get; set; }
    /// <summary>True: go back to the driver's default instead of writing Value.</summary>
    public bool Restore { get; set; }
    /// <summary>True: failure to write this one is not an error (settings some drivers don't know).</summary>
    public bool Optional { get; set; }
}

/// <summary>The helper rewrites this file as it goes; the UI polls it to show progress.</summary>
public sealed class ElevatedStatus
{
    public string Step { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Finished { get; set; }
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public List<string> Warnings { get; set; } = new();
    public int ExitCode { get; set; }
    public bool RestartNeeded { get; set; }
    public int TasksDisabled { get; set; }
    public bool RestorePointMade { get; set; }
}

// ---------------------------------------------------------------- profile export

public sealed class ProfileExportFile
{
    public int Pitstop { get; set; } = 1;
    public DateTime Exported { get; set; } = DateTime.Now;
    public string Driver { get; set; } = "";
    public List<ExportedProfile> Profiles { get; set; } = new();
}

public sealed class ExportedProfile
{
    public string Name { get; set; } = "";
    public bool Global { get; set; }
    public List<string> Apps { get; set; } = new();
    /// <summary>Setting id ("0x10835002") to value.</summary>
    public Dictionary<string, uint> Settings { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never, AllowTrailingCommas = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<HistoryEntry>))]
[JsonSerializable(typeof(PackageMeta))]
[JsonSerializable(typeof(ElevatedJob))]
[JsonSerializable(typeof(ElevatedStatus))]
[JsonSerializable(typeof(ProfileExportFile))]
public partial class PitstopJson : JsonSerializerContext
{
}
