namespace Pitstop.Services;

/// <summary>
/// Where Pitstop keeps its files. Normally %LOCALAPPDATA%\Pitstop. In portable mode (an empty file
/// named "portable" next to Pitstop.exe) everything goes in a "data" folder beside the exe instead.
/// </summary>
public static class Paths
{
    public static string ExePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Pitstop.exe");
    public static string ExeDir { get; } = Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;

    public static bool Portable { get; } = File.Exists(Path.Combine(ExeDir, "portable"));

    public static string Data { get; } = Portable
        ? Path.Combine(ExeDir, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pitstop");

    public static string Settings => Path.Combine(Data, "settings.json");
    public static string History => Path.Combine(Data, "history.json");
    public static string LogFile => Path.Combine(Data, "pitstop.log");
    public static string Themes => Path.Combine(Data, "themes");
    public static string Packages => Path.Combine(Data, "packages");
    public static string Downloads => Path.Combine(Data, "downloads");
    public static string Work => Path.Combine(Data, "work");
    public static string Updates => Path.Combine(Data, "updates");
    public static string Jobs => Path.Combine(Data, "jobs");
    public static string Cache => Path.Combine(Data, "cache");

    /// <summary>Hangar's shared theme folder: themes saved there show up in every suite app.</summary>
    public static string HangarThemes => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hangar", "themes");

    public static void Ensure()
    {
        foreach (var dir in new[] { Data, Themes, Packages, Downloads, Work, Updates, Jobs, Cache })
        {
            try { Directory.CreateDirectory(dir); } catch { /* read-only location: features degrade gracefully */ }
        }
    }

    /// <summary>True when <paramref name="path"/> is inside Pitstop's data folder (used to sanity-check elevated jobs).</summary>
    public static bool IsInsideData(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\') + "\\";
            var root = Path.GetFullPath(Data).TrimEnd('\\') + "\\";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
