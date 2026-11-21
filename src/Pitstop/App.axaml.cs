using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Pitstop.Services;
using Pitstop.ViewModels;
using Pitstop.Views;

namespace Pitstop;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private SettingsService _settings = null!;
    private AppServices _services = null!;
    private MainWindowViewModel _vm = null!;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private DispatcherTimer? _driverTimer;
    private bool _quitting;
    private bool _healthySignalled;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (!Program.SmokeTest)
            {
                Dispatcher.UIThread.UnhandledException += (_, e) =>
                {
                    Log.Error("ui", e.Exception);
                    e.Handled = true;
                };
            }

            _settings = SettingsService.Load();
            var themes = new ThemeService();
            themes.Apply(_settings.Current.Theme, _settings.Current.Accent);
            var history = new HistoryService();
            var updates = new UpdateService(_settings);
            _services = new AppServices { Settings = _settings, Themes = themes, History = history, Updates = updates };
            _services.Toast = ShowToast;
            _services.TrayOptionsChanged = ApplyTrayOptions;
            _services.RestartToUpdate = RestartToUpdate;

            _vm = new MainWindowViewModel(_services);
            if (!Program.SmokeTest) _vm.Navigate(_settings.Current.LastPage);

            themes.Applied += () =>
            {
                if (_window is not null) themes.ApplyTo(_window);
            };
            if (PlatformSettings is { } ps)
            {
                ps.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    var s = _settings.Current;
                    if (s.Theme == "system" || s.Accent == "windows" || themes.Find(s.Theme).Accent == 0 && s.Accent == "theme")
                        _vm.Settings.OnSystemColorsChanged();
                });
            }

            ApplyTrayOptions();
            if (!Program.StartMinimized || !_settings.Current.TrayWatch)
                ShowWindow();

            desktop.Exit += (_, _) =>
            {
                SavePlacement();
                _settings.Save();
                _tray?.Dispose();
            };

            if (!Program.SmokeTest)
            {
                SingleInstance.Listen(() => Dispatcher.UIThread.Post(ShowWindow), () => Dispatcher.UIThread.Post(Quit));
                AutoStart.RefreshPath();
            }
            if (Program.AfterUpdatePid is int oldPid) UpdateService.CleanupAfterUpdate(oldPid);
            if (!Program.SmokeTest && _settings.Current.LastRunVersion != AppInfo.Version)
            {
                _settings.Current.LastRunVersion = AppInfo.Version;
                _settings.Save();
            }

            _ = StartupAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartupAsync()
    {
        await _vm.Driver.DetectAsync();
        if (Program.SmokeTest)
        {
            await RunSmokeTestAsync();
            return;
        }
        if (_settings.Current.CheckDriversOnLaunch || _settings.Current.TrayWatch)
        {
            var newer = await _vm.Driver.CheckAsync(manual: false);
            NotifyIfNew(newer, onLaunch: true);
        }

        // Pitstop's own updates: shortly after launch, then every 4 hours.
        await Task.Delay(TimeSpan.FromSeconds(8));
        _ = _services.Updates.CheckAsync(manual: false);
        var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(4) };
        updateTimer.Tick += (_, _) => _ = _services.Updates.CheckAsync(manual: false);
        updateTimer.Start();
        _services.Updates.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (_services.Updates.State == UpdateState.Ready && !_updateToastShown)
            {
                _updateToastShown = true;
                ShowToast("Pitstop update ready", $"Version {_services.Updates.ReadyVersion} is downloaded and verified. Click to restart and update.", () =>
                {
                    ShowWindow();
                    RestartToUpdate();
                });
            }
        });
    }

    private bool _updateToastShown;

    // ---------------------------------------------------------------- window

    private void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Attach(_vm);
            var s = _settings.Current;
            if (s.WindowWidth >= 820 && s.WindowHeight >= 560)
            {
                _window.Width = s.WindowWidth;
                _window.Height = s.WindowHeight;
            }
            if (s.WindowMaximized) _window.WindowState = WindowState.Maximized;
            _services.Themes.ApplyTo(_window);
            _window.Closing += OnWindowClosing;
            _window.Opened += (_, _) =>
            {
                _services.Themes.ApplyTo(_window!);
                SignalHealthy();
            };
            _desktop!.MainWindow = _window;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Tells the previous copy (after a self-update) that this one started fine.</summary>
    private void SignalHealthy()
    {
        if (_healthySignalled || Program.AfterUpdatePid is not int oldPid) return;
        _healthySignalled = true;
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(UpdateService.HealthyEventName(oldPid), out var ev))
                {
                    ev.Set();
                    ev.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Error("healthy signal", ex);
            }
            SingleInstance.AcquireLater();
        }, TimeSpan.FromMilliseconds(600));
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        SavePlacement();
        if (_quitting) return;
        if (_settings.Current.TrayWatch && !Program.SmokeTest)
        {
            e.Cancel = true;
            _window?.Hide();
            return;
        }
        if (DriverInstaller.IsBusy)
        {
            // Never leave an install half-watched: keep running in the tray until it's done.
            e.Cancel = true;
            _window?.Hide();
            EnsureTray();
            ShowToast("Still installing", "Pitstop will finish the driver install in the background. It's in the tray.", ShowWindow);
            return;
        }
        Quit();
    }

    private void SavePlacement()
    {
        if (_window is null) return;
        var s = _settings.Current;
        s.WindowMaximized = _window.WindowState == WindowState.Maximized;
        if (_window.WindowState == WindowState.Normal)
        {
            s.WindowWidth = _window.Width;
            s.WindowHeight = _window.Height;
        }
    }

    private void Quit()
    {
        if (DriverInstaller.IsBusy && !Program.SmokeTest)
        {
            ShowToast("A driver install is running", "Pitstop will close itself after it finishes.", ShowWindow);
            _vm.Driver.InstallFinished += () => Dispatcher.UIThread.Post(Quit);
            return;
        }
        _quitting = true;
        _window?.Close();
        _desktop?.Shutdown();
    }

    // ---------------------------------------------------------------- tray

    private void ApplyTrayOptions()
    {
        if (_settings.Current.TrayWatch) EnsureTray();
        else if (_tray is not null && !DriverInstaller.IsBusy)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
            _tray = null;
            TrayIcon.SetIcons(this, new TrayIcons());
        }

        _driverTimer?.Stop();
        if (_settings.Current.TrayWatch)
        {
            _driverTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(Math.Clamp(_settings.Current.CheckHours, 1, 48)) };
            _driverTimer.Tick += async (_, _) =>
            {
                if (DriverInstaller.IsBusy) return;
                var newer = await _vm.Driver.CheckAsync(manual: false);
                NotifyIfNew(newer, onLaunch: false);
            };
            _driverTimer.Start();
        }
    }

    private void EnsureTray()
    {
        if (_tray is not null) return;
        WindowIcon? icon = null;
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://Pitstop/Assets/pitstop.ico"));
            icon = new WindowIcon(s);
        }
        catch (Exception ex)
        {
            Log.Error("tray icon", ex);
        }

        var menu = new NativeMenu();
        var open = new NativeMenuItem("Open Pitstop");
        open.Click += (_, _) => ShowWindow();
        var check = new NativeMenuItem("Check for new drivers");
        check.Click += async (_, _) =>
        {
            var newer = await _vm.Driver.CheckAsync(manual: true);
            if (newer is null)
                ShowToast("No new driver", _vm.Driver.HasCatalogError ? _vm.Driver.CatalogError : $"You're on the newest {(_vm.Driver.PreferStudio ? "Studio" : "Game Ready")} driver.", ShowWindow);
            else NotifyIfNew(newer, onLaunch: false, force: true);
        };
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();
        menu.Items.Add(open);
        menu.Items.Add(check);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

        _tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "Pitstop",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    private void NotifyIfNew(DriverRelease? newer, bool onLaunch, bool force = false)
    {
        if (newer is null) return;
        if (!force && _settings.Current.NotifiedVersion == newer.Version) return;
        // Don't pop up over the window you're already looking at.
        if (!force && onLaunch && _window is { IsVisible: true }) return;
        _settings.Current.NotifiedVersion = newer.Version;
        _settings.Save();
        ShowToast($"New {(newer.Branch == Models.DriverBranch.Studio ? "Studio" : "Game Ready")} driver: {newer.Version}",
            $"Released {newer.DateText}. You have {_vm.Driver.InstalledVersion}. Click to open Pitstop.", () =>
            {
                ShowWindow();
                _vm.Navigate("driver");
            });
    }

    private void ShowToast(string title, string message, Action? onClick)
    {
        if (Program.SmokeTest) return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                new ToastWindow(title, message, onClick ?? ShowWindow).Show();
            }
            catch (Exception ex)
            {
                Log.Error("toast", ex);
            }
        });
    }

    // ---------------------------------------------------------------- self-update

    private void RestartToUpdate()
    {
        if (DriverInstaller.IsBusy)
        {
            ShowToast("Update waits for the driver install", "Pitstop will offer to restart once the install is done.", ShowWindow);
            return;
        }
        var updates = _services.Updates;
        var healthy = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateService.HealthyEventName(Environment.ProcessId));
        SavePlacement();
        _settings.Save();
        var error = updates.BeginRestart(out var started, out var backup);
        if (error is not null)
        {
            healthy.Dispose();
            ShowToast("Couldn't update", error, ShowWindow);
            return;
        }
        _window?.Hide();
        _tray?.Dispose();
        _tray = null;
        Task.Run(() =>
        {
            bool ok = UpdateService.WaitForHealthy(healthy, started!);
            healthy.Dispose();
            Dispatcher.UIThread.Post(() =>
            {
                if (ok)
                {
                    Log.Info("update handed off");
                    _quitting = true;
                    _desktop?.Shutdown();
                }
                else
                {
                    Log.Warn("new version didn't start; rolling back");
                    updates.RollBack(started, backup);
                    ApplyTrayOptions();
                    ShowWindow();
                    ShowToast("Update rolled back", "The new version didn't start, so Pitstop stayed on " + AppInfo.Version + ".", ShowWindow);
                }
            });
        });
    }

    // ---------------------------------------------------------------- smoke test

    /// <summary>
    /// Release gate: open every page, switch through every theme, then exit 0. Any unhandled
    /// exception fails the run (Program exits non-zero).
    /// </summary>
    private async Task RunSmokeTestAsync()
    {
        try
        {
            await Task.Delay(800);
            foreach (var page in new[] { "driver", "saved", "games", "history", "settings", "driver" })
            {
                _vm.Navigate(page);
                await Task.Delay(600);
            }
            foreach (var t in ThemeService.BuiltIn)
            {
                _services.Themes.Apply(t.Id, "theme");
                await Task.Delay(120);
            }
            foreach (var accent in new[] { "windows", "#FF5D5D", "theme" })
            {
                _services.Themes.Apply("light", accent);
                await Task.Delay(120);
            }
            _services.Themes.Apply("dark", "theme");
            _vm.IsSidebarCollapsed = true;
            await Task.Delay(300);
            _vm.IsSidebarCollapsed = false;
            await _vm.Driver.CheckAsync(manual: true);
            await Task.Delay(500);
            Log.Info("smoke test passed");
            Program.SmokeExitCode = 0;
        }
        catch (Exception ex)
        {
            Log.Error("smoke test", ex);
            Program.SmokeExitCode = 4;
        }
        _quitting = true;
        _desktop?.Shutdown(Program.SmokeExitCode);
    }
}
