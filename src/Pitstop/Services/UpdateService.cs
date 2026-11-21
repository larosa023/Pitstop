using System.Diagnostics;
using System.Text.Json;

namespace Pitstop.Services;

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Error }

/// <summary>
/// Pitstop's own updater (Hangar's model): checks larosa023/Pitstop releases on the chosen channel,
/// downloads Pitstop.exe in the background, verifies its SHA-256 against GitHub's digest and the
/// release's SHA256SUMS.txt, and swaps it in on restart. If the new copy doesn't start, the old one
/// puts itself back.
/// </summary>
public sealed class UpdateService
{
    public const string AssetName = "Pitstop.exe";
    private const string SumsName = "SHA256SUMS.txt";
    private readonly SettingsService _settings;
    private string? _etag;
    private string? _cachedJson;
    private bool _busy;

    public UpdateService(SettingsService settings) => _settings = settings;

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public string Status { get; private set; } = "";
    public double Progress { get; private set; }
    public string? ReadyVersion { get; private set; }
    public string? ReadyPath { get; private set; }
    public string? AvailableVersion { get; private set; }
    public DateTime? LastChecked { get; private set; }

    public event Action? Changed;

    internal sealed record Release(string Version, string Tag, bool Prerelease, string AssetUrl, string? AssetDigest, string? SumsUrl, string Page);

    private void Set(UpdateState state, string status)
    {
        State = state;
        Status = status;
        Changed?.Invoke();
    }

    /// <param name="manual">"Check for updates now": also downloads when auto-update is off, and reports "up to date".</param>
    public async Task CheckAsync(bool manual)
    {
        if (_busy || State == UpdateState.Ready && !manual) return;
        _busy = true;
        try
        {
            Set(UpdateState.Checking, "Checking for updates…");
            var release = await FindLatestAsync();
            LastChecked = DateTime.Now;
            if (release is null || Versions.CompareSemVer(release.Version, AppInfo.Version) <= 0)
            {
                AvailableVersion = null;
                Set(UpdateState.UpToDate, $"Pitstop {AppInfo.Version} is up to date.");
                return;
            }
            if (!manual && release.Version == _settings.Current.SkipVersion)
            {
                Set(UpdateState.UpToDate, $"Pitstop {release.Version} didn't start last time, so it's being skipped.");
                return;
            }
            AvailableVersion = release.Version;
            if (ReadyVersion == release.Version && ReadyPath is not null && File.Exists(ReadyPath))
            {
                Set(UpdateState.Ready, $"Pitstop {release.Version} is ready. Restart to update.");
                return;
            }
            if (!manual && !_settings.Current.AutoUpdate)
            {
                Set(UpdateState.Available, $"Pitstop {release.Version} is available.");
                return;
            }
            await DownloadAsync(release);
        }
        catch (Exception ex)
        {
            Log.Error("update check", ex);
            Set(UpdateState.Error, manual ? "Couldn't check for updates: " + ex.Message : "");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<Release?> FindLatestAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{AppInfo.Repo}/releases?per_page=20");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (_etag is not null) req.Headers.TryAddWithoutValidation("If-None-Match", _etag);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var resp = await Http.Client.SendAsync(req, cts.Token);
        string json;
        if (resp.StatusCode == System.Net.HttpStatusCode.NotModified && _cachedJson is not null) json = _cachedJson;
        else
        {
            resp.EnsureSuccessStatusCode();
            json = await resp.Content.ReadAsStringAsync(cts.Token);
            _etag = resp.Headers.ETag?.Tag;
            _cachedJson = json;
        }
        return PickRelease(json, beta: _settings.Current.UpdateChannel == "beta");
    }

    /// <summary>Stable sees normal releases; Beta also sees pre-releases and "-" versions. Whichever is newest wins.</summary>
    internal static Release? PickRelease(string json, bool beta)
    {
        using var doc = JsonDocument.Parse(json);
        Release? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            var version = tag.TrimStart('v', 'V');
            bool pre = (r.TryGetProperty("prerelease", out var p) && p.GetBoolean()) || Versions.IsPrerelease(version);
            if (pre && !beta) continue;

            string? asset = null, digest = null, sums = null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var url = a.GetProperty("browser_download_url").GetString();
                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    asset = url;
                    if (a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String) digest = dg.GetString();
                }
                else if (string.Equals(name, SumsName, StringComparison.OrdinalIgnoreCase)) sums = url;
            }
            if (asset is null) continue;
            var rel = new Release(version, tag, pre, asset, digest, sums, r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "");
            if (best is null || Versions.CompareSemVer(rel.Version, best.Version) > 0) best = rel;
        }
        return best;
    }

    private async Task DownloadAsync(Release r)
    {
        var dir = Path.Combine(Paths.Updates, r.Version);
        Directory.CreateDirectory(dir);
        var partial = Path.Combine(dir, AssetName + ".partial");
        var final = Path.Combine(dir, AssetName);

        Progress = 0;
        Set(UpdateState.Downloading, $"Downloading Pitstop {r.Version}…");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var sha = await Http.DownloadAsync(r.AssetUrl, partial, (done, total, _) =>
        {
            if (total > 0)
            {
                Progress = done * 100.0 / total;
                Changed?.Invoke();
            }
        }, cts.Token);

        // Every source of truth we have must agree, and there must be at least one.
        var expected = new List<string>();
        if (r.AssetDigest is { } dg && dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) expected.Add(dg[7..].ToLowerInvariant());
        if (r.SumsUrl is not null)
        {
            var sums = await Http.GetStringAsync(r.SumsUrl, TimeSpan.FromSeconds(20), cts.Token);
            var line = sums.Split('\n').Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(parts => parts.Length >= 2 && parts[^1].TrimStart('*').Equals(AssetName, StringComparison.OrdinalIgnoreCase));
            if (line is not null) expected.Add(line[0].ToLowerInvariant());
        }
        if (expected.Count == 0 || expected.Any(e => e != sha))
        {
            try { File.Delete(partial); } catch { }
            throw new InvalidDataException(expected.Count == 0
                ? "The release has no checksum, so it wasn't installed."
                : "The download didn't match its checksum, so it wasn't installed.");
        }
        File.Move(partial, final, overwrite: true);
        ReadyVersion = r.Version;
        ReadyPath = final;
        Set(UpdateState.Ready, $"Pitstop {r.Version} is ready. Restart to update.");

        // old staged versions
        foreach (var old in Directory.GetDirectories(Paths.Updates).Where(d => !d.Equals(dir, StringComparison.OrdinalIgnoreCase)))
            Files.TryDeleteDirectory(old);
    }

    /// <summary>
    /// Swaps in the new exe and starts it. Returns null on success (the caller should hide and wait for
    /// <see cref="WaitForHealthy"/>), or a reason it couldn't.
    /// </summary>
    public string? BeginRestart(out Process? started, out string backup)
    {
        started = null;
        backup = Paths.ExePath + ".old";
        if (DriverInstaller.IsBusy) return "Finish the driver install first.";
        if (ReadyPath is null || !File.Exists(ReadyPath)) return "The update isn't downloaded yet.";
        var exe = Paths.ExePath;
        try
        {
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(exe, backup);            // Windows lets a running exe be renamed
            try
            {
                File.Copy(ReadyPath, exe, overwrite: true);
            }
            catch
            {
                File.Move(backup, exe);
                throw;
            }
            started = Process.Start(new ProcessStartInfo(exe, $"--after-update {Environment.ProcessId}")
            {
                UseShellExecute = false,
                WorkingDirectory = Paths.ExeDir,
            });
            return started is null ? "The new version didn't start." : null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Pitstop can't write to its own folder. Move Pitstop.exe somewhere you own (like a folder in your user profile).";
        }
        catch (Exception ex)
        {
            Log.Error("update restart", ex);
            return ex.Message;
        }
    }

    public static string HealthyEventName(int oldPid) => $"Local\\Pitstop.Healthy.{oldPid}";

    /// <summary>True when the new copy signalled it's up within 20 seconds.</summary>
    public static bool WaitForHealthy(EventWaitHandle healthy, Process started)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (healthy.WaitOne(250)) return true;
            if (started.HasExited) return false;
        }
        return false;
    }

    /// <summary>The new copy didn't come up: put the old exe back and remember not to try that version again.</summary>
    public void RollBack(Process? started, string backup)
    {
        try
        {
            if (started is { HasExited: false }) started.Kill(true);
            started?.WaitForExit(5000);
        }
        catch { }
        try
        {
            var exe = Paths.ExePath;
            if (File.Exists(backup))
            {
                if (File.Exists(exe)) File.Delete(exe);
                File.Move(backup, exe);
            }
        }
        catch (Exception ex)
        {
            Log.Error("update rollback", ex);
        }
        if (ReadyVersion is not null)
        {
            _settings.Current.SkipVersion = ReadyVersion;
            _settings.Save();
        }
        ReadyVersion = null;
        ReadyPath = null;
        Set(UpdateState.Error, "The update didn't start, so Pitstop stayed on this version.");
    }

    /// <summary>Called by the new copy: removes the previous exe once the old process has exited.</summary>
    public static void CleanupAfterUpdate(int oldPid)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var old = Process.GetProcessById(oldPid);
                await old.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
            }
            catch { /* already gone */ }
            var backup = Paths.ExePath + ".old";
            for (int i = 0; i < 20 && File.Exists(backup); i++)
            {
                try { File.Delete(backup); }
                catch { await Task.Delay(1000); }
            }
            Files.TryDeleteDirectory(Paths.Updates);
            try { Directory.CreateDirectory(Paths.Updates); } catch { }
        });
    }
}
