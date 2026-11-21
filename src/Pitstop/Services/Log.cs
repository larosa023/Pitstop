namespace Pitstop.Services;

/// <summary>A tiny append-only log (pitstop.log in the data folder), trimmed when it passes 1 MB.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string? OverridePath { get; set; }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string context, Exception? ex) => Write("ERROR", $"{context}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            var path = OverridePath ?? Paths.LogFile;
            lock (Gate)
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 1_000_000)
                {
                    var keep = File.ReadAllLines(path);
                    File.WriteAllLines(path, keep.Skip(keep.Length / 2));
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never break anything
        }
    }
}
