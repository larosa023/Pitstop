using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pitstop.Services;

namespace Pitstop.ViewModels;

public sealed class ThemeChoice
{
    public required ThemeDef Def { get; init; }
    public string Name => Def.Name;
    public string Description => Def.Custom ? (Def.Description.Length > 0 ? Def.Description : "Your theme") : Def.Description;
    public IBrush Preview1 => new SolidColorBrush(Color.FromUInt32(0xFF000000 | Def.Bg));
    public IBrush Preview2 => new SolidColorBrush(Color.FromUInt32(0xFF000000 | Def.Surface));
    public IBrush Preview3 => new SolidColorBrush(Color.FromUInt32(0xFF000000 | (Def.Accent != 0 ? Def.Accent : ThemeService.WindowsAccent())));
    public override string ToString() => Name;
}

public partial class SwatchChoice : ObservableObject
{
    public required string Key { get; init; }
    public required IBrush Brush { get; init; }
    public required string Tip { get; init; }
    public required System.Windows.Input.ICommand Select { get; init; }
    [ObservableProperty] private bool _isSelected;
}

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _app;
    private readonly DriverViewModel _driver;
    private bool _loading = true;

    public SettingsViewModel(AppServices app, DriverViewModel driver)
    {
        _app = app;
        _driver = driver;
        var s = app.Settings.Current;
        foreach (var c in ThemeService.AccentSwatches)
            Swatches.Add(new SwatchChoice { Key = ThemeService.Hex(c), Brush = new SolidColorBrush(Color.FromUInt32(0xFF000000 | c)), Tip = ThemeService.Hex(c), Select = SetAccentCommand });
        LoadThemes();
        _customAccent = s.Accent.StartsWith('#') ? s.Accent : "";
        _preferStudio = s.PreferredBranch == "studio";
        _checkOnLaunch = s.CheckDriversOnLaunch;
        _trayWatch = s.TrayWatch;
        _startWithWindows = s.StartWithWindows && AutoStart.IsEnabled();
        _selectedInterval = Intervals.FirstOrDefault(i => i.Hours == s.CheckHours) ?? Intervals[2];
        _betaChannel = s.UpdateChannel == "beta";
        _autoUpdate = s.AutoUpdate;
        UpdateAccentSelection();
        app.Updates.Changed += () => Ui(RefreshUpdate);
        RefreshUpdate();
        _loading = false;
    }

    public string VersionText => $"Pitstop {AppInfo.Version}" + (Paths.Portable ? " · portable" : "");
    public string DataFolderText => Paths.Data;

    // ---------------------------------------------------------------- appearance

    public ObservableCollection<ThemeChoice> Themes { get; } = new();
    [ObservableProperty] private ThemeChoice? _selectedTheme;
    [ObservableProperty] private string _themeMessage = "";
    public bool HasThemeMessage => ThemeMessage.Length > 0;
    partial void OnThemeMessageChanged(string value) => OnPropertyChanged(nameof(HasThemeMessage));

    private void LoadThemes()
    {
        Themes.Clear();
        foreach (var t in _app.Themes.All) Themes.Add(new ThemeChoice { Def = t });
        _selectedTheme = Themes.FirstOrDefault(t => t.Def.Id == _app.Settings.Current.Theme) ?? Themes[0];
        OnPropertyChanged(nameof(SelectedTheme));
    }

    partial void OnSelectedThemeChanged(ThemeChoice? value)
    {
        if (_loading || value is null) return;
        _app.Settings.Current.Theme = value.Def.Id;
        _app.Settings.Save();
        ApplyTheme();
    }

    public ObservableCollection<SwatchChoice> Swatches { get; } = new();
    [ObservableProperty] private bool _accentIsTheme;
    [ObservableProperty] private bool _accentIsWindows;
    [ObservableProperty] private bool _accentIsCustom;
    [ObservableProperty] private string _customAccent = "";

    private void UpdateAccentSelection()
    {
        var a = _app.Settings.Current.Accent;
        AccentIsTheme = a == "theme";
        AccentIsWindows = a == "windows";
        bool swatch = false;
        foreach (var s in Swatches)
        {
            s.IsSelected = string.Equals(s.Key, a, StringComparison.OrdinalIgnoreCase);
            swatch |= s.IsSelected;
        }
        AccentIsCustom = a.StartsWith('#') && !swatch;
    }

    [RelayCommand]
    private void SetAccent(string key)
    {
        _app.Settings.Current.Accent = key;
        _app.Settings.Save();
        UpdateAccentSelection();
        ApplyTheme();
    }

    [RelayCommand]
    private void ApplyCustomAccent()
    {
        if (ThemeService.TryHex(CustomAccent, out var c))
        {
            CustomAccent = ThemeService.Hex(c);
            SetAccent(CustomAccent);
        }
        else
        {
            ThemeMessage = "Type a color like #4F8CFF.";
        }
    }

    private void ApplyTheme()
    {
        ThemeMessage = "";
        _app.Themes.Apply(_app.Settings.Current.Theme, _app.Settings.Current.Accent);
    }

    /// <summary>Re-applies when Windows switches light/dark or its accent color.</summary>
    public void OnSystemColorsChanged() => ApplyTheme();

    [RelayCommand]
    private void OpenThemesFolder()
    {
        Directory.CreateDirectory(Paths.Themes);
        Shell.Open(Paths.Themes);
    }

    [RelayCommand]
    private void ReloadThemes()
    {
        _app.Themes.ReloadCustom();
        _loading = true;
        LoadThemes();
        _loading = false;
        ApplyTheme();
        ThemeMessage = _app.Themes.LoadErrors.Count > 0
            ? "Skipped: " + string.Join("; ", _app.Themes.LoadErrors)
            : $"{_app.Themes.Custom.Count} custom theme{(_app.Themes.Custom.Count == 1 ? "" : "s")} loaded.";
    }

    [RelayCommand]
    private void SaveThemeAsFile()
    {
        try
        {
            var path = _app.Themes.SaveCurrentAsFile(_app.Settings.Current.Accent);
            ThemeMessage = $"Saved {Path.GetFileName(path)} in the themes folder. Edit it, then Reload.";
        }
        catch (Exception ex)
        {
            ThemeMessage = "Couldn't save: " + ex.Message;
        }
    }

    // ---------------------------------------------------------------- drivers

    [ObservableProperty] private bool _preferStudio;
    [ObservableProperty] private bool _checkOnLaunch;

    partial void OnPreferStudioChanged(bool value)
    {
        if (_loading) return;
        _app.Settings.Current.PreferredBranch = value ? "studio" : "gameready";
        _app.Settings.Current.NotifiedVersion = "";
        _app.Settings.Save();
        _driver.ReloadOptions();
    }

    partial void OnCheckOnLaunchChanged(bool value)
    {
        if (_loading) return;
        _app.Settings.Current.CheckDriversOnLaunch = value;
        _app.Settings.Save();
    }

    public DriverViewModel Driver => _driver;

    [ObservableProperty] private string _storageText = "";

    public void RefreshStorage()
    {
        var list = PackageStore.List();
        StorageText = list.Count == 0 ? "No saved drivers yet." : $"{list.Count} saved · {Fmt.Bytes(list.Sum(p => p.SizeOnDisk))}";
    }

    [RelayCommand]
    private void OpenDataFolder() => Shell.Open(Paths.Data);

    // ---------------------------------------------------------------- tray

    public sealed record Interval(int Hours, string Label)
    {
        public override string ToString() => Label;
    }

    public List<Interval> Intervals { get; } = new()
    {
        new(1, "Every hour"), new(3, "Every 3 hours"), new(6, "Every 6 hours"), new(12, "Every 12 hours"), new(24, "Once a day"),
    };

    [ObservableProperty] private bool _trayWatch;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private Interval? _selectedInterval;

    partial void OnTrayWatchChanged(bool value)
    {
        if (_loading) return;
        _app.Settings.Current.TrayWatch = value;
        if (!value && StartWithWindows) StartWithWindows = false;
        _app.Settings.Save();
        _app.TrayOptionsChanged();
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        _app.Settings.Current.StartWithWindows = value;
        if (value && !TrayWatch) TrayWatch = true;
        _app.Settings.Save();
        AutoStart.Set(value);
    }

    partial void OnSelectedIntervalChanged(Interval? value)
    {
        if (_loading || value is null) return;
        _app.Settings.Current.CheckHours = value.Hours;
        _app.Settings.Save();
        _app.TrayOptionsChanged();
    }

    // ---------------------------------------------------------------- updates

    [ObservableProperty] private bool _betaChannel;
    [ObservableProperty] private bool _autoUpdate;
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _updateReady;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private bool _isCheckingUpdate;
    public bool HasUpdateStatus => UpdateStatus.Length > 0;
    partial void OnUpdateStatusChanged(string value) => OnPropertyChanged(nameof(HasUpdateStatus));

    partial void OnBetaChannelChanged(bool value)
    {
        if (_loading) return;
        _app.Settings.Current.UpdateChannel = value ? "beta" : "stable";
        _app.Settings.Save();
        _ = _app.Updates.CheckAsync(manual: false);
    }

    partial void OnAutoUpdateChanged(bool value)
    {
        if (_loading) return;
        _app.Settings.Current.AutoUpdate = value;
        _app.Settings.Save();
    }

    private void RefreshUpdate()
    {
        var u = _app.Updates;
        UpdateStatus = u.Status;
        UpdateReady = u.State == UpdateState.Ready;
        UpdateAvailable = u.State == UpdateState.Available;
        IsDownloading = u.State == UpdateState.Downloading;
        IsCheckingUpdate = u.State is UpdateState.Checking or UpdateState.Downloading;
        UpdateProgress = u.Progress;
    }

    [RelayCommand]
    private Task CheckForUpdates() => _app.Updates.CheckAsync(manual: true);

    [RelayCommand]
    private void RestartToUpdate() => _app.RestartToUpdate();

    [RelayCommand]
    private void OpenGitHub() => Shell.Open(AppInfo.RepoUrl);
}
