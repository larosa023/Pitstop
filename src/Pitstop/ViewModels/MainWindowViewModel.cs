using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pitstop.Services;

namespace Pitstop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppServices _app;

    public MainWindowViewModel(AppServices app)
    {
        _app = app;
        Driver = new DriverViewModel(app);
        Saved = new SavedViewModel(Driver);
        Games = new ProfilesViewModel(app);
        History = new HistoryViewModel(app.History);
        Settings = new SettingsViewModel(app, Driver);
        _isSidebarCollapsed = app.Settings.Current.SidebarCollapsed;
        _currentPage = Driver;
        Driver.InstallStarted += () => CurrentPage = Driver;
        Driver.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DriverViewModel.IsInstalling)) OnPropertyChanged(nameof(IsInstalling));
        };
        app.Updates.Changed += () => Ui(() => OnPropertyChanged(nameof(UpdateReady)));
    }

    public AppServices App => _app;
    public DriverViewModel Driver { get; }
    public SavedViewModel Saved { get; }
    public ProfilesViewModel Games { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDriver), nameof(IsSaved), nameof(IsGames), nameof(IsHistory), nameof(IsSettings))]
    private ViewModelBase _currentPage;

    public bool IsDriver => CurrentPage == Driver;
    public bool IsSaved => CurrentPage == Saved;
    public bool IsGames => CurrentPage == Games;
    public bool IsHistory => CurrentPage == History;
    public bool IsSettings => CurrentPage == Settings;
    public bool IsInstalling => Driver.IsInstalling;
    public bool UpdateReady => _app.Updates.State == UpdateState.Ready;

    public string PageKey => CurrentPage switch
    {
        SavedViewModel => "saved",
        ProfilesViewModel => "games",
        HistoryViewModel => "history",
        SettingsViewModel => "settings",
        _ => "driver",
    };

    partial void OnCurrentPageChanged(ViewModelBase value)
    {
        switch (value)
        {
            case SavedViewModel s: s.Refresh(); break;
            case ProfilesViewModel g: _ = g.EnsureLoadedAsync(); break;
            case SettingsViewModel st: st.RefreshStorage(); break;
            case DriverViewModel d: d.UpdateCheckedText(); break;
        }
        _app.Settings.Current.LastPage = PageKey;
    }

    [RelayCommand]
    public void Navigate(string page) => CurrentPage = page switch
    {
        "saved" => Saved,
        "games" => Games,
        "history" => History,
        "settings" => Settings,
        _ => Driver,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarWidth))]
    private bool _isSidebarCollapsed;

    public double SidebarWidth => IsSidebarCollapsed ? 68 : 224;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        _app.Settings.Current.SidebarCollapsed = value;
        _app.Settings.Save();
    }

    [RelayCommand]
    private void RestartToUpdate() => _app.RestartToUpdate();

    public string VersionText => $"v{AppInfo.Version}";
}
