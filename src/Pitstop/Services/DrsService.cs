using System.Globalization;
using Pitstop.Models;
using Pitstop.Native;

namespace Pitstop.Services;

public sealed class ProfileSummary
{
    public string Name { get; init; } = "";
    public bool IsGlobal { get; init; }
    public bool Predefined { get; init; }
    public List<string> Apps { get; init; } = new();
    public string AppsText => Apps.Count == 0 ? (IsGlobal ? "Applies to every game unless a game says otherwise" : "No programs")
        : Apps.Count <= 3 ? string.Join(", ", Apps) : $"{string.Join(", ", Apps.Take(3))} +{Apps.Count - 3} more";
    public string DisplayName => IsGlobal ? "Global (all games)" : Name;
    internal string Search { get; init; } = "";

    public bool Matches(string q) => q.Length == 0 || Search.Contains(q, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads and writes NVIDIA's driver profiles (the same database the NVIDIA Control Panel uses).</summary>
public static class DrsService
{
    public static bool Available => NvApi.Initialize();
    public static string UnavailableReason => NvApi.InitError ?? "NVIDIA's driver settings aren't available.";

    private sealed class Session : IDisposable
    {
        public IntPtr H { get; }

        public Session()
        {
            if (!NvApi.Initialize()) throw new InvalidOperationException(UnavailableReason);
            H = NvApi.DrsCreateSession();
            try
            {
                NvApi.DrsLoadSettings(H);
            }
            catch
            {
                NvApi.DrsDestroySession(H);
                throw;
            }
        }

        public void Dispose() => NvApi.DrsDestroySession(H);
    }

    private static IntPtr Resolve(Session s, string name, bool isGlobal) =>
        isGlobal ? NvApi.DrsGetCurrentGlobalProfile(s.H)
                 : NvApi.DrsFindProfileByName(s.H, name) ?? throw new InvalidOperationException($"The profile \"{name}\" no longer exists.");

    /// <summary>Every profile, the global one first. About 6,000 on a current driver; takes well under a second.</summary>
    public static List<ProfileSummary> LoadProfiles()
    {
        lock (NvApi.Lock)
        {
            using var s = new Session();
            var global = NvApi.DrsGetCurrentGlobalProfile(s.H);
            var list = new List<ProfileSummary>();
            ProfileSummary? globalSummary = null;
            for (uint i = 0; NvApi.DrsEnumProfiles(s.H, i, out var p); i++)
            {
                var (name, predefined, apps, _) = NvApi.DrsGetProfileInfo(s.H, p);
                var appList = apps > 0 ? NvApi.DrsEnumApplications(s.H, p, apps) : new List<string>();
                var summary = new ProfileSummary
                {
                    Name = name,
                    IsGlobal = p == global,
                    Predefined = predefined,
                    Apps = appList,
                    Search = name + " " + string.Join(" ", appList),
                };
                if (summary.IsGlobal) globalSummary = summary;
                else if (appList.Count > 0 || !predefined) list.Add(summary);   // skip internal profiles with no programs
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            if (globalSummary is null)
            {
                var (gname, _, _, _) = NvApi.DrsGetProfileInfo(s.H, global);
                globalSummary = new ProfileSummary { Name = gname, IsGlobal = true, Search = gname + " global base" };
            }
            list.Insert(0, globalSummary);
            return list;
        }
    }

    public static Dictionary<uint, NvApi.SettingRead> ReadSettings(string profile, bool isGlobal, IEnumerable<uint> ids)
    {
        lock (NvApi.Lock)
        {
            using var s = new Session();
            var p = Resolve(s, profile, isGlobal);
            var map = new Dictionary<uint, NvApi.SettingRead>();
            foreach (var id in ids.Distinct())
            {
                try { map[id] = NvApi.DrsGetSetting(s.H, p, id); }
                catch (NvApiException ex) { Log.Warn($"read 0x{id:X8}: {ex.Message}"); }
            }
            return map;
        }
    }

    /// <summary>Writes changes and saves (admin only: runs in the elevated helper).</summary>
    public static void Apply(List<ProfileChange> changes, List<string> warnings)
    {
        lock (NvApi.Lock)
        {
            using var s = new Session();
            foreach (var c in changes)
            {
                IntPtr p;
                if (c.IsGlobal) p = NvApi.DrsGetCurrentGlobalProfile(s.H);
                else if (NvApi.DrsFindProfileByName(s.H, c.Name) is { } found) p = found;
                else if (c.CreateIfMissing)
                {
                    p = NvApi.DrsCreateProfile(s.H, c.Name);
                    foreach (var app in c.Apps) NvApi.DrsCreateApplication(s.H, p, app);
                }
                else
                {
                    warnings.Add($"Skipped \"{c.Name}\": that profile isn't in this driver.");
                    continue;
                }

                foreach (var set in c.Settings)
                {
                    int r = set.Restore ? NvApi.DrsRestoreProfileDefaultSetting(s.H, p, set.Id) : NvApi.DrsSetDword(s.H, p, set.Id, set.Value);
                    if (r == NvApi.Ok) continue;
                    if (set.Optional) Log.Warn($"optional setting 0x{set.Id:X8} on {c.Name}: {NvApi.Describe(r)}");
                    else NvApi.Check(r, $"Changing {GameSettings.NameOf(set.Id)} for {(c.IsGlobal ? "Global" : c.Name)}");
                }
            }
            NvApi.DrsSaveSettings(s.H);
        }
    }

    /// <summary>Your own values (not NVIDIA's predefined ones) for Pitstop's settings, for one profile or all.</summary>
    public static List<ExportedProfile> Export(IReadOnlyList<ProfileSummary> profiles)
    {
        var ids = GameSettings.AllIds;
        var result = new List<ExportedProfile>();
        lock (NvApi.Lock)
        {
            using var s = new Session();
            var global = NvApi.DrsGetCurrentGlobalProfile(s.H);
            foreach (var prof in profiles)
            {
                IntPtr p;
                if (prof.IsGlobal) p = global;
                else if (NvApi.DrsFindProfileByName(s.H, prof.Name) is { } found) p = found;
                else continue;
                var e = new ExportedProfile { Name = prof.Name, Global = prof.IsGlobal, Apps = prof.Apps.ToList() };
                foreach (var id in ids)
                {
                    try
                    {
                        var r = NvApi.DrsGetSetting(s.H, p, id);
                        if (r.Found && r.Location == 0 && !r.IsPredefined) e.Settings[$"0x{id:X8}"] = r.Value;
                    }
                    catch (NvApiException) { }
                }
                if (e.Settings.Count > 0) result.Add(e);
            }
        }
        return result;
    }

    public static List<ProfileChange> ToChanges(ProfileExportFile file)
    {
        var list = new List<ProfileChange>();
        foreach (var p in file.Profiles)
        {
            var c = new ProfileChange { Name = p.Name, IsGlobal = p.Global, CreateIfMissing = !p.Global && p.Apps.Count > 0, Apps = p.Apps };
            foreach (var (key, value) in p.Settings)
            {
                var k = key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? key[2..] : key;
                if (uint.TryParse(k, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) && GameSettings.AllIds.Contains(id))
                    c.Settings.Add(new SettingChange { Id = id, Value = value, Optional = GameSettings.IsOptional(id) });
            }
            if (c.Settings.Count > 0) list.Add(c);
        }
        return list;
    }
}
