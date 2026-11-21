using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Pitstop.Models;

namespace Pitstop.Services;

public sealed class DriverRelease
{
    public string Version { get; init; } = "";
    public DriverBranch Branch { get; init; }
    public string Name { get; init; } = "";
    public DateTime? ReleaseDate { get; init; }
    public string DownloadUrl { get; init; } = "";
    public string DetailsUrl { get; init; } = "";
    public string SizeText { get; init; } = "";
    public bool IsWhql { get; init; }

    public string FileName => Path.GetFileName(new Uri(DownloadUrl).AbsolutePath);
    public string DateText => ReleaseDate is { } d ? Fmt.Date(d) : "";
}

/// <summary>
/// Asks NVIDIA's own driver lookup (the one behind nvidia.com/drivers) which drivers fit this card.
/// Downloads are only ever accepted from NVIDIA's download servers over HTTPS.
/// </summary>
public static class DriverCatalog
{
    private const string ProductListUrl = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";
    private const string LookupUrl = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php";

    // Known ids, used if NVIDIA's product list can't be reached (psid = series, pfid = product).
    private static readonly Dictionary<string, (int psid, int pfid)> Fallback = new(StringComparer.OrdinalIgnoreCase)
    {
        ["geforce rtx 4090"] = (127, 995),
    };

    /// <summary>True for https URLs on NVIDIA's download servers (us.download.nvidia.com, international.download.nvidia.com, ...).</summary>
    public static bool IsOfficialDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("download.nvidia.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.EndsWith(".download.nvidia.com", StringComparison.OrdinalIgnoreCase)) &&
        u.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    public static bool IsNvidiaPage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("nvidia.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".nvidia.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>NVIDIA's product ids for a card name like "GeForce RTX 4090".</summary>
    public static async Task<(int psid, int pfid)?> ResolveProductAsync(string productName, CancellationToken ct)
    {
        var key = Normalize(productName);
        var cacheFile = Path.Combine(Paths.Cache, "nvidia-products.xml");

        string? cached = null;
        bool fresh = false;
        try
        {
            if (File.Exists(cacheFile))
            {
                cached = await File.ReadAllTextAsync(cacheFile, ct);
                fresh = DateTime.Now - File.GetLastWriteTime(cacheFile) < TimeSpan.FromDays(7);
            }
        }
        catch { /* re-download */ }

        if (cached is not null && fresh && FindInProductList(cached, key) is { } hit) return hit;

        try
        {
            var xml = await Http.GetStringAsync(ProductListUrl, TimeSpan.FromSeconds(20), ct);
            try { Files.WriteAtomic(cacheFile, xml); } catch { }
            if (FindInProductList(xml, key) is { } fromWeb) return fromWeb;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warn("product list: " + ex.Message);
        }

        if (cached is not null && FindInProductList(cached, key) is { } stale) return stale;

        return Fallback.TryGetValue(key, out var fb) ? fb : null;
    }

    internal static (int psid, int pfid)? FindInProductList(string xml, string key)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            (int, int)? contains = null;
            int containsLen = 0;
            foreach (var lv in doc.Descendants().Where(e => e.Name.LocalName == "LookupValue"))
            {
                var name = lv.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value ?? "";
                var value = lv.Elements().FirstOrDefault(e => e.Name.LocalName == "Value")?.Value ?? "";
                var parent = lv.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("ParentID", StringComparison.OrdinalIgnoreCase))?.Value ?? "0";
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pfid)) continue;
                int.TryParse(parent, NumberStyles.Integer, CultureInfo.InvariantCulture, out var psid);
                var n = Normalize(name);
                if (n == key) return (psid, pfid);
                // "geforce rtx 4090" inside "geforce rtx 4090 founders" etc.: keep the longest match
                if (n.Length > containsLen && key.StartsWith(n + " ", StringComparison.Ordinal))
                {
                    contains = (psid, pfid);
                    containsLen = n.Length;
                }
            }
            return contains;
        }
        catch (Exception ex)
        {
            Log.Warn("product list parse: " + ex.Message);
            return null;
        }
    }

    private static string Normalize(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (s.StartsWith("nvidia ")) s = s[7..];
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Newest drivers first for one branch.</summary>
    public static async Task<List<DriverRelease>> GetDriversAsync(int psid, int pfid, DriverBranch branch, int count, CancellationToken ct)
    {
        var url = $"{LookupUrl}?func=DriverManualLookup&psid={psid}&pfid={pfid}&osID=57&languageCode=1033&beta=0&isWHQL=0" +
                  $"&dltype=-1&dch=1&upCRD={(branch == DriverBranch.Studio ? 1 : 0)}&qnf=0&sort1=0&numberOfResults={count}";
        var json = await Http.GetStringAsync(url, TimeSpan.FromSeconds(25), ct);
        return Parse(json, branch);
    }

    internal static List<DriverRelease> Parse(string json, DriverBranch branch)
    {
        var list = new List<DriverRelease>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in ids.EnumerateArray())
        {
            if (!item.TryGetProperty("downloadInfo", out var d)) continue;
            string S(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? Uri.UnescapeDataString(v.GetString() ?? "") : "";

            var download = S("DownloadURL");
            if (!IsOfficialDownload(download)) continue;
            var name = S("Name");
            // The two lists occasionally overlap; keep each driver on its own side.
            bool isStudio = name.Contains("Studio", StringComparison.OrdinalIgnoreCase);
            if (name.Length > 0 && isStudio != (branch == DriverBranch.Studio)) continue;
            if (S("IsBeta") == "1") continue;

            DateTime? date = null;
            var ds = S("ReleaseDateTime");
            if (DateTime.TryParseExact(ds, new[] { "ddd MMM dd, yyyy", "ddd MMM d, yyyy" }, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var parsed))
                date = parsed;

            var details = S("DetailsURL");
            list.Add(new DriverRelease
            {
                Version = S("Version"),
                Branch = branch,
                Name = name.Length > 0 ? name : branch == DriverBranch.Studio ? "NVIDIA Studio Driver" : "GeForce Game Ready Driver",
                ReleaseDate = date,
                DownloadUrl = download,
                DetailsUrl = IsNvidiaPage(details) ? details : "",
                SizeText = S("DownloadURLFileSize"),
                IsWhql = S("IsWHQL") == "1",
            });
        }
        return list.Where(r => r.Version.Length > 0)
            .OrderByDescending(r => r.Version, Comparer<string>.Create(Versions.CompareDriver)).ToList();
    }
}
