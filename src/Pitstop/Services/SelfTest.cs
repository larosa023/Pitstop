using System.Diagnostics;
using Pitstop.Models;
using Pitstop.Native;

namespace Pitstop.Services;

/// <summary>
/// "Pitstop.exe --selftest &lt;folder&gt;": run by CI on every build. Exercises everything that doesn't need
/// an NVIDIA card: NVIDIA's real driver lookup for an RTX 4090, the real download from NVIDIA's servers,
/// the signature check, unpacking only the chosen parts and preparing setup.cfg, plus the theme, version
/// and updater logic. Writes selftest.log and exits with the number of failures.
/// </summary>
public static class SelfTest
{
    private static string _log = "";
    private static int _failures;

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        _log = Path.Combine(outDir, "selftest.log");
        File.WriteAllText(_log, $"Pitstop {AppInfo.Version} self-test {DateTime.Now:u}\n");
        Log.OverridePath = Path.Combine(outDir, "pitstop.log");
        try
        {
            RunAsync(outDir, Environment.GetCommandLineArgs().Contains("--no-download")).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Fail("unexpected: " + ex);
        }
        Line(_failures == 0 ? "RESULT: all checks passed" : $"RESULT: {_failures} check(s) failed");
        return _failures;
    }

    private static void Line(string s)
    {
        File.AppendAllText(_log, s + "\n");
        Console.WriteLine(s);
    }

    private static void Check(bool ok, string what)
    {
        Line((ok ? "PASS " : "FAIL ") + what);
        if (!ok) _failures++;
    }

    private static void Fail(string what) => Check(false, what);

    private static async Task RunAsync(string outDir, bool noDownload)
    {
        // ---- versions
        Check(Versions.FromWindowsDriverVersion("32.0.15.6636") == "566.36", "Windows 32.0.15.6636 -> 566.36");
        Check(Versions.FromWindowsDriverVersion("32.0.16.1074") == "610.74", "Windows 32.0.16.1074 -> 610.74");
        Check(Versions.FromWindowsDriverVersion("31.0.15.5161") == "551.61", "Windows 31.0.15.5161 -> 551.61");
        Check(Versions.FromNvApi(56636) == "566.36", "NvAPI 56636 -> 566.36");
        Check(Versions.CompareDriver("581.80", "566.36") > 0 && Versions.CompareDriver("566.36", "566.36") == 0, "driver version compare");
        Check(Versions.CompareSemVer("0.2.0-beta.1", "0.2.0") < 0 && Versions.CompareSemVer("0.10.0", "0.9.1") > 0 &&
              Versions.CompareSemVer("0.2.0-beta.2", "0.2.0-beta.10") < 0, "SemVer compare (pre-releases)");

        // ---- themes (Hangar format)
        Check(ThemeService.BuiltIn.Count == 14, "14 built-in themes");
        var names = string.Join(",", ThemeService.BuiltIn.Select(t => t.Name));
        Check(names == "Dark,Light,Windows,Midnight,Glass,Frost,Twilight,Forest,Sunset,Ocean,Rose,Graphite,Paper,Mist", "theme order: " + names);
        const string sample = """
        {
          "schema": 1,
          "name": "My Theme",
          "description": "Shown under the name",
          "mode": "dark",              // dark | light
          "backdrop": "mica",
          "transparency": 0.15,
          "surfaceOpacity": 1.0,
          "colors": {
            "background": "#2E3440",
            "surface": "#3B4252",
            "text": "#ECEFF4",
            "accent": "#88C0D0",
          }
        }
        """;
        Check(ThemeService.TryParse(sample, out var parsed, out var err) && parsed!.Bg == 0x2E3440 && parsed.Accent == 0x88C0D0 &&
              parsed.Sidebar == ThemeService.BuiltIn[0].Sidebar, "Hangar theme JSON with comments " + err);
        Check(!ThemeService.TryParse("{\"name\":\"x\",\"colors\":{\"text\":\"#ZZZ\"}}", out _, out var err2) && err2.Contains("text"), "bad theme color is rejected");

        // ---- game setting matching
        var fps = GameSettings.All.First(d => d.Key == "fps");
        var map = new Dictionary<uint, NvApi.SettingRead> { [GameSettings.FrameRateLimit] = new() { Found = true, Value = 141 } };
        Check(fps.Match(map)?.Label == "141 FPS", "frame-rate option matching");
        var latency = GameSettings.All.First(d => d.Key == "latency");
        var ultra = new Dictionary<uint, NvApi.SettingRead>
        {
            [GameSettings.PreRenderLimit] = new() { Found = true, Value = 1 },
            [GameSettings.UllCplState] = new() { Found = true, Value = 2 },
            [GameSettings.UllEnabled] = new() { Found = true, Value = 1 },
        };
        Check(latency.Match(ultra)?.Label == "Ultra", "low latency Ultra matching");

        // ---- updater release picking
        const string releases = """
        [
          {"tag_name":"v0.3.0-beta.1","draft":false,"prerelease":true,"html_url":"x","assets":[{"name":"Pitstop.exe","browser_download_url":"https://e/b","digest":"sha256:ab"}]},
          {"tag_name":"v0.2.0","draft":false,"prerelease":false,"html_url":"x","assets":[{"name":"Pitstop.exe","browser_download_url":"https://e/s","digest":"sha256:cd"},{"name":"SHA256SUMS.txt","browser_download_url":"https://e/sums"}]},
          {"tag_name":"v0.9.0","draft":true,"prerelease":false,"html_url":"x","assets":[{"name":"Pitstop.exe","browser_download_url":"https://e/d"}]}
        ]
        """;
        Check(UpdateService.PickRelease(releases, beta: false)?.Version == "0.2.0", "stable channel picks 0.2.0");
        Check(UpdateService.PickRelease(releases, beta: true)?.Version == "0.3.0-beta.1", "beta channel picks 0.3.0-beta.1");

        // ---- NVIDIA lookup
        Check(DriverCatalog.IsOfficialDownload("https://us.download.nvidia.com/Windows/566.36/566.36-desktop-win10-win11-64bit-international-dch-whql.exe"), "official URL accepted");
        Check(!DriverCatalog.IsOfficialDownload("https://evil.example.com/nvidia.download.nvidia.com.exe") &&
              !DriverCatalog.IsOfficialDownload("http://us.download.nvidia.com/x.exe"), "other hosts / plain http refused");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        var product = await DriverCatalog.ResolveProductAsync("GeForce RTX 4090", cts.Token);
        Check(product is not null, $"NVIDIA product ids for GeForce RTX 4090: {product}");
        if (product is not { } p) return;

        var gr = await DriverCatalog.GetDriversAsync(p.psid, p.pfid, DriverBranch.GameReady, 10, cts.Token);
        var st = await DriverCatalog.GetDriversAsync(p.psid, p.pfid, DriverBranch.Studio, 10, cts.Token);
        foreach (var r in gr.Take(3)) Line($"  Game Ready {r.Version}  {r.DateText}  {r.SizeText}  {r.DownloadUrl}  notes: {r.DetailsUrl}");
        foreach (var r in st.Take(3)) Line($"  Studio     {r.Version}  {r.DateText}  {r.SizeText}  {r.DownloadUrl}  notes: {r.DetailsUrl}");
        Check(gr.Count > 0, $"Game Ready drivers found ({gr.Count})");
        Check(st.Count > 0, $"Studio drivers found ({st.Count})");
        Check(gr.All(r => DriverCatalog.IsOfficialDownload(r.DownloadUrl)) && st.All(r => DriverCatalog.IsOfficialDownload(r.DownloadUrl)), "all download links are on NVIDIA's servers");
        Check(gr.FirstOrDefault()?.ReleaseDate is not null, "release date parsed");
        Check(!gr.Any(r => st.Any(s => s.Version == r.Version && s.Name == r.Name)), "branches kept apart");

        if (noDownload || gr.Count == 0) return;

        // ---- download + verify + unpack + prepare (everything but running NVIDIA's setup)
        var latest = gr[0];
        var sw = Stopwatch.StartNew();
        var package = await DriverInstaller.DownloadAsync(latest, _ => { }, cts.Token);
        Line($"  downloaded {latest.FileName} ({Fmt.Bytes(package.Meta.Size)}) in {sw.Elapsed.TotalSeconds:0}s, sha256 {package.Meta.Sha256}");
        Check(package.Meta.Size > 100_000_000, "package size looks right");
        Check(WinTrust.CheckSignedBy(package.ExePath, "NVIDIA Corporation") is null, "package is signed by NVIDIA Corporation");
        Check(WinTrust.CheckSignedBy(Environment.ProcessPath!, "NVIDIA Corporation") is not null, "a non-NVIDIA file is rejected");
        DriverInstaller.VerifySaved(package);
        Check(true, "saved package re-verifies (SHA-256 + signature)");

        var req = new InstallRequest { PhysX = true, HdAudio = true, UsbC = true };
        var folders = DriverInstaller.FoldersFor(req);
        var work = Path.Combine(outDir, "work");
        Files.TryDeleteDirectory(work);
        sw.Restart();
        double last = 0;
        Extractor.Extract(package.ExePath, work, DriverInstaller.IncludeFilter(folders), v => last = v, cts.Token);
        var tops = Directory.GetDirectories(work).Select(Path.GetFileName).OrderBy(x => x).ToList();
        Line($"  unpacked in {sw.Elapsed.TotalSeconds:0}s ({Fmt.Bytes(Files.DirectorySize(work))}): {string.Join(", ", tops)}");
        Line($"  root files: {string.Join(", ", Directory.GetFiles(work).Select(Path.GetFileName))}");
        Check(last > 0.99, "unpack progress reached 100%");
        Check(tops.Contains("Display.Driver") && tops.Contains("NVI2"), "display driver and installer core unpacked");
        Check(!tops.Any(t => t!.StartsWith("NvApp", StringComparison.OrdinalIgnoreCase) || t!.StartsWith("GFExperience", StringComparison.OrdinalIgnoreCase) ||
                             t!.Contains("Telemetry", StringComparison.OrdinalIgnoreCase) || t == "FrameViewSDK" || t == "NvContainer"),
              "no NVIDIA App / GFE / telemetry / container unpacked");
        var installed = new List<string>();
        var notes = DriverInstaller.Prepare(work, folders, installed);
        Line($"  components: {string.Join(", ", installed)}; notes: {string.Join(" ", notes)}");
        var cfg = File.ReadAllText(Path.Combine(work, "setup.cfg"));
        Check(!cfg.Contains("EulaHtmlFile") && !cfg.Contains("FunctionalConsentFile") && !cfg.Contains("PrivacyPolicyFile"), "setup.cfg no longer needs the left-out packages");
        Check(WinTrust.CheckSignedBy(Path.Combine(work, "setup.exe"), "NVIDIA Corporation") is null, "unpacked setup.exe is signed by NVIDIA");

        // The rollback store keeps 3.
        PackageStore.MarkInstalled(package);
        PackageStore.Prune(package.Id);
        Check(PackageStore.List().Count <= PackageStore.Keep && PackageStore.Find(latest.Version, latest.Branch) is not null, "package store keeps the installed package");
        Files.TryDeleteDirectory(work);
    }
}
