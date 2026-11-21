using Pitstop.Services;

namespace Pitstop.ViewModels;

/// <summary>The services every page shares, plus a few app-level hooks the App class fills in.</summary>
public sealed class AppServices
{
    public required SettingsService Settings { get; init; }
    public required ThemeService Themes { get; init; }
    public required HistoryService History { get; init; }
    public required UpdateService Updates { get; init; }

    /// <summary>Shows a small notification in the corner of the screen (title, message, click action).</summary>
    public Action<string, string, Action?> Toast { get; set; } = (_, _, _) => { };

    /// <summary>Settings changed something the app shell cares about (tray, startup).</summary>
    public Action TrayOptionsChanged { get; set; } = () => { };

    /// <summary>Asks the App to swap in the downloaded update and restart.</summary>
    public Action RestartToUpdate { get; set; } = () => { };

    /// <summary>Opens a file picker; set by the main window.</summary>
    public Func<string, string, Task<string?>>? SaveFile { get; set; }
    public Func<string, Task<string?>>? OpenFile { get; set; }
}
