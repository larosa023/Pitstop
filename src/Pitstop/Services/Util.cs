using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Pitstop.Services;

public static class Fmt
{
    public static string Bytes(long b)
    {
        if (b < 0) return "?";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : v >= 100 ? $"{v:0} {u[i]}" : $"{v:0.#} {u[i]}";
    }

    public static string Date(DateTime d) => d.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    public static string DateAndTime(DateTime d) => d.ToString("MMM d, yyyy · h:mm tt", CultureInfo.InvariantCulture);

    public static string Ago(DateTime when)
    {
        var span = System.DateTime.Now - when;
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        return Date(when);
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond <= 0 ? "" : $"{Bytes((long)bytesPerSecond)}/s";
}

/// <summary>NVIDIA driver versions ("566.36") and Pitstop's own SemVer versions ("0.2.0-beta.1").</summary>
public static class Versions
{
    /// <summary>Compares "566.36"-style versions numerically. Unparseable sorts first.</summary>
    public static int CompareDriver(string? a, string? b)
    {
        static (int, int) P(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return (-1, -1);
            var parts = s.Trim().Split('.');
            int major = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : -1;
            int minor = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            return (major, minor);
        }
        var (a1, a2) = P(a);
        var (b1, b2) = P(b);
        return a1 != b1 ? a1.CompareTo(b1) : a2.CompareTo(b2);
    }

    /// <summary>Windows' "32.0.15.6636" to NVIDIA's "566.36": the last five digits of the last two parts.</summary>
    public static string? FromWindowsDriverVersion(string? windowsVersion)
    {
        if (string.IsNullOrWhiteSpace(windowsVersion)) return null;
        var p = windowsVersion.Trim().Split('.');
        if (p.Length < 4) return null;
        var digits = p[2] + p[3].PadLeft(4, '0');
        if (digits.Length < 5 || !digits.All(char.IsAsciiDigit)) return null;
        var last5 = digits[^5..];
        return $"{int.Parse(last5[..3], CultureInfo.InvariantCulture)}.{last5[3..]}";
    }

    /// <summary>NvAPI's 56636 to "566.36".</summary>
    public static string FromNvApi(uint v) => $"{v / 100}.{v % 100:00}";

    public static bool IsPrerelease(string v) => v.Contains('-');

    /// <summary>SemVer comparison including pre-release tags (1.0.0-beta.2 &lt; 1.0.0).</summary>
    public static int CompareSemVer(string a, string b)
    {
        static (int[] core, string[] pre) P(string s)
        {
            s = s.Trim().TrimStart('v', 'V');
            int plus = s.IndexOf('+');
            if (plus >= 0) s = s[..plus];
            int dash = s.IndexOf('-');
            var core = (dash >= 0 ? s[..dash] : s).Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToList();
            while (core.Count < 3) core.Add(0);
            var pre = dash >= 0 ? s[(dash + 1)..].Split('.') : Array.Empty<string>();
            return (core.ToArray(), pre);
        }
        var (ac, ap) = P(a);
        var (bc, bp) = P(b);
        for (int i = 0; i < 3; i++)
            if (ac[i] != bc[i]) return ac[i].CompareTo(bc[i]);
        if (ap.Length == 0 && bp.Length == 0) return 0;
        if (ap.Length == 0) return 1;
        if (bp.Length == 0) return -1;
        for (int i = 0; i < Math.Min(ap.Length, bp.Length); i++)
        {
            bool an = int.TryParse(ap[i], out var ai), bn = int.TryParse(bp[i], out var bi);
            int c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(ap[i], bp[i]);
            if (c != 0) return c;
        }
        return ap.Length.CompareTo(bp.Length);
    }
}

public static class Http
{
    public static HttpClient Client { get; } = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        return c;
    }

    public static async Task<string> GetStringAsync(string url, TimeSpan timeout, CancellationToken ct = default, string? accept = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (accept != null) req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        using var resp = await Client.SendAsync(req, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams <paramref name="url"/> to <paramref name="path"/>, hashing as it goes.
    /// Progress reports (bytes done, total or -1, bytes/second). Returns the lower-case SHA-256.
    /// </summary>
    public static async Task<string> DownloadAsync(string url, string path, Action<long, long, double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buf = new byte[1 << 20];
            long done = 0;
            var sw = Stopwatch.StartNew();
            long lastBytes = 0;
            double lastTime = 0, speed = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                hash.AppendData(buf, 0, n);
                done += n;
                double t = sw.Elapsed.TotalSeconds;
                if (t - lastTime >= 0.25)
                {
                    var inst = (done - lastBytes) / (t - lastTime);
                    speed = speed <= 0 ? inst : speed * 0.7 + inst * 0.3;
                    lastBytes = done;
                    lastTime = t;
                    progress?.Invoke(done, total, speed);
                }
            }
            progress?.Invoke(done, total, speed);
            if (total > 0 && done != total)
                throw new IOException($"The download stopped early ({Fmt.Bytes(done)} of {Fmt.Bytes(total)}).");
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string Sha256File(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}

public static class Shell
{
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("open " + target, ex);
        }
    }

    /// <summary>Runs a console tool hidden and returns (exit code, stdout + stderr).</summary>
    public static (int code, string output) Run(string file, string args, TimeSpan timeout, string? workDir = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir ?? Environment.SystemDirectory,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(true); } catch { }
            return (-1, "timed out");
        }
        p.WaitForExit();
        return (p.ExitCode, (stdout.Result + "\n" + stderr.Result).Trim());
    }
}
