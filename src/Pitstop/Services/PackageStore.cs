using System.Text.Json;
using Pitstop.Models;

namespace Pitstop.Services;

public sealed class SavedPackage
{
    public required string Dir { get; init; }
    public required PackageMeta Meta { get; init; }
    public string ExePath => Path.Combine(Dir, Meta.FileName);
    public long SizeOnDisk { get; init; }
    public DriverBranch Branch => BranchNames.Parse(Meta.Branch);
    public string Id => Path.GetFileName(Dir);
}

/// <summary>
/// The driver packages Pitstop keeps for one-click rollback: packages\&lt;version&gt;-&lt;branch&gt;\ with the
/// original NVIDIA .exe and a meta.json (SHA-256, size, dates). Only the last few installed are kept.
/// </summary>
public static class PackageStore
{
    public const int Keep = 3;

    public static List<SavedPackage> List()
    {
        var list = new List<SavedPackage>();
        try
        {
            if (!Directory.Exists(Paths.Packages)) return list;
            foreach (var dir in Directory.GetDirectories(Paths.Packages))
            {
                try
                {
                    var metaPath = Path.Combine(dir, "meta.json");
                    if (!File.Exists(metaPath)) continue;
                    var meta = JsonSerializer.Deserialize(File.ReadAllText(metaPath), PitstopJson.Default.PackageMeta);
                    if (meta is null || meta.FileName.Length == 0 || !File.Exists(Path.Combine(dir, meta.FileName))) continue;
                    list.Add(new SavedPackage { Dir = dir, Meta = meta, SizeOnDisk = Files.DirectorySize(dir) });
                }
                catch (Exception ex)
                {
                    Log.Warn($"package {dir}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("package list", ex);
        }
        return list.OrderByDescending(p => p.Meta.LastInstalledAt ?? p.Meta.DownloadedAt).ToList();
    }

    public static SavedPackage? Find(string version, DriverBranch branch) =>
        List().FirstOrDefault(p => p.Meta.Version == version && p.Branch == branch);

    /// <summary>Moves a verified download into the store.</summary>
    public static SavedPackage Add(string downloadedFile, DriverRelease release, string sha256)
    {
        var dir = Path.Combine(Paths.Packages, $"{release.Version}-{BranchNames.Key(release.Branch)}");
        Files.TryDeleteDirectory(dir);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, release.FileName);
        File.Move(downloadedFile, target, overwrite: true);
        var meta = new PackageMeta
        {
            Version = release.Version,
            Branch = BranchNames.Key(release.Branch),
            FileName = release.FileName,
            Sha256 = sha256,
            Size = new FileInfo(target).Length,
            SourceUrl = release.DownloadUrl,
            ReleaseDate = release.ReleaseDate?.ToString("yyyy-MM-dd") ?? "",
            DownloadedAt = DateTime.Now,
        };
        SaveMeta(dir, meta);
        return new SavedPackage { Dir = dir, Meta = meta, SizeOnDisk = meta.Size };
    }

    public static void MarkInstalled(SavedPackage p)
    {
        p.Meta.LastInstalledAt = DateTime.Now;
        SaveMeta(p.Dir, p.Meta);
    }

    public static void Delete(SavedPackage p) => Files.TryDeleteDirectory(p.Dir);

    /// <summary>Keeps the <see cref="Keep"/> most recently installed packages (never the one just installed).</summary>
    public static void Prune(string? protectId = null)
    {
        var all = List();
        var keep = all.Where(p => p.Id == protectId)
            .Concat(all.Where(p => p.Id != protectId).OrderByDescending(p => p.Meta.LastInstalledAt ?? DateTime.MinValue)
                .ThenByDescending(p => p.Meta.DownloadedAt))
            .Take(Keep).Select(p => p.Id).ToHashSet();
        foreach (var p in all.Where(p => !keep.Contains(p.Id)))
        {
            Log.Info($"pruning saved driver {p.Id}");
            Delete(p);
        }
    }

    private static void SaveMeta(string dir, PackageMeta meta) =>
        Files.WriteAtomic(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(meta, PitstopJson.Default.PackageMeta));
}
