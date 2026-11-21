using System.Reflection;

namespace Pitstop;

public static class AppInfo
{
    public const string Name = "Pitstop";

    /// <summary>The built-in updater checks this repo's GitHub Releases.</summary>
    public const string Repo = "larosa023/Pitstop";
    public const string RepoUrl = "https://github.com/larosa023/Pitstop";

    /// <summary>"0.1.0", or "0.2.0-beta.1" for a Beta build.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var info = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        var v = typeof(AppInfo).Assembly.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
    }

    public static string UserAgent => $"Pitstop/{Version} (+{RepoUrl})";
}
