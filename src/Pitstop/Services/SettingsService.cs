using System.Text.Json;
using Pitstop.Models;

namespace Pitstop.Services;

public sealed class SettingsService
{
    public AppSettings Current { get; private set; } = new();

    public static SettingsService Load()
    {
        var s = new SettingsService();
        try
        {
            if (File.Exists(Paths.Settings))
                s.Current = JsonSerializer.Deserialize(File.ReadAllText(Paths.Settings), PitstopJson.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("settings load", ex);
            s.Current = new AppSettings();
        }
        if (s.Current.CheckHours is < 1 or > 48) s.Current.CheckHours = 6;
        return s;
    }

    public void Save()
    {
        try
        {
            Files.WriteAtomic(Paths.Settings, JsonSerializer.Serialize(Current, PitstopJson.Default.AppSettings));
        }
        catch (Exception ex)
        {
            Log.Error("settings save", ex);
        }
    }
}

public static class Files
{
    /// <summary>Write to a temp file, then swap it in, so a crash never leaves half a file behind.</summary>
    public static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    public static long DirectorySize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    public static void TryDeleteDirectory(string dir)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(dir, true);
                return;
            }
            catch
            {
                Thread.Sleep(300);
            }
        }
    }

    public static long FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return long.MaxValue;
        }
    }
}
