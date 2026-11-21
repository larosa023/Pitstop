using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pitstop.Models;
using Pitstop.Services;

namespace Pitstop.ViewModels;

public partial class DriverRowViewModel : ViewModelBase
{
    private readonly DriverViewModel _owner;

    public DriverRowViewModel(DriverViewModel owner, DriverBranch branch)
    {
        _owner = owner;
        Branch = branch;
    }

    public DriverBranch Branch { get; }
    public string BranchText => BranchNames.Display(Branch);
    public string BranchHelp => Branch == DriverBranch.Studio
        ? "Tested longer with creative apps. Updated about monthly."
        : "Tuned for the newest games, on launch day.";

    [ObservableProperty] private DriverRelease? _release;
    [ObservableProperty] private string _versionText = "—";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isNewer;
    [ObservableProperty] private bool _isOlder;
    [ObservableProperty] private bool _isSaved;

    public bool HasRelease => Release is not null;
    public bool CanInstall => Release is not null && _owner.CanInstall;
    public void NotifyCanInstall() => OnPropertyChanged(nameof(CanInstall));
    public bool HasNotes => Release is { DetailsUrl.Length: > 0 };
    public string InstallText => IsInstalled ? "Reinstall" : IsOlder ? "Install older" : "Install";

    public void Update(DriverRelease? r, string? installedVersion, DriverBranch? installedBranch)
    {
        Release = r;
        VersionText = r?.Version ?? "—";
        var parts = new List<string>();
        if (r?.DateText is { Length: > 0 } d) parts.Add("Released " + d);
        if (r?.SizeText is { Length: > 0 } s) parts.Add(s);
        DetailText = r is null ? "Not available for this card right now." : string.Join(" · ", parts);
        int cmp = Versions.CompareDriver(r?.Version, installedVersion);
        IsInstalled = r is not null && cmp == 0 && (installedBranch is null || installedBranch == Branch);
        IsNewer = r is not null && installedVersion is not null && cmp > 0;
        IsOlder = r is not null && installedVersion is not null && cmp < 0;
        IsSaved = r is not null && PackageStore.Find(r.Version, r.Branch) is not null;
        OnPropertyChanged(nameof(HasRelease));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(InstallText));
    }

    [RelayCommand]
    private void Install()
    {
        if (Release is not null) _owner.StartInstall(Release, null);
    }

    [RelayCommand]
    private void Notes()
    {
        if (Release is { DetailsUrl.Length: > 0 } r) Shell.Open(r.DetailsUrl);
    }
}

public partial class StepViewModel : ObservableObject
{
    public StepViewModel(InstallStage stage, string label)
    {
        Stage = stage;
        Label = label;
    }

    public InstallStage Stage { get; }
    public string Label { get; }
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isFailed;
}

public partial class DriverViewModel : ViewModelBase
{
    private readonly AppServices _app;
    private CancellationTokenSource? _installCts;
    private List<DriverRelease> _gameReady = new(), _studio = new();
    private DateTime? _checkedAt;
    private (int psid, int pfid)? _product;

    public DriverViewModel(AppServices app)
    {
        _app = app;
        var s = app.Settings.Current;
        _physX = s.InstallPhysX;
        _hdAudio = s.InstallHdAudio;
        _usbC = s.InstallUsbC;
        _clean = s.CleanInstall;
        _restorePoint = s.CreateRestorePoint;
        _disableTelemetry = s.DisableTelemetry;
        GameReady = new DriverRowViewModel(this, DriverBranch.GameReady);
        Studio = new DriverRowViewModel(this, DriverBranch.Studio);
    }

    /// <summary>Raised when an install starts, so the window can show this page.</summary>
    public event Action? InstallStarted;
    public event Action? InstallFinished;

    // ---------------------------------------------------------------- GPU

    [ObservableProperty] private GpuInfo? _gpu;
    [ObservableProperty] private bool _isDetecting = true;
    [ObservableProperty] private string _gpuName = "Looking for your graphics card…";
    [ObservableProperty] private string _gpuDetail = "";
    [ObservableProperty] private string _installedVersion = "—";
    [ObservableProperty] private string _installedDetail = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _statusKind = "";   // good | accent | warn | bad
    public bool HasGpu => Gpu is not null;
    public bool NoGpu => !IsDetecting && Gpu is null;
    public bool StatusGood => StatusKind == "good";
    public bool StatusAccent => StatusKind == "accent";
    public bool StatusWarn => StatusKind == "warn";
    public bool HasStatus => StatusText.Length > 0;
    public DriverBranch? InstalledBranch { get; private set; }

    partial void OnStatusKindChanged(string value)
    {
        OnPropertyChanged(nameof(StatusGood));
        OnPropertyChanged(nameof(StatusAccent));
        OnPropertyChanged(nameof(StatusWarn));
    }

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnGpuChanged(GpuInfo? value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(HasGpu));
        OnPropertyChanged(nameof(NoGpu));
    }

    partial void OnIsDetectingChanged(bool value) => OnPropertyChanged(nameof(NoGpu));

    public async Task DetectAsync()
    {
        IsDetecting = true;
        var gpu = await Task.Run(GpuService.Detect);
        Gpu = gpu;
        IsDetecting = false;
        if (gpu is null)
        {
            GpuName = "No NVIDIA graphics card found";
            GpuDetail = "Pitstop manages drivers for NVIDIA GeForce and RTX cards.";
            InstalledVersion = "—";
            InstalledDetail = "";
            return;
        }
        GpuName = gpu.Name;
        var bits = new List<string>();
        if (gpu.VramBytes > 0) bits.Add($"{Math.Round(gpu.VramBytes / 1073741824.0)} GB");
        if (gpu.IsNotebook) bits.Add("Laptop");
        GpuDetail = string.Join(" · ", bits);
        InstalledVersion = gpu.DriverVersion ?? "None";
        RefreshInstalledDetail();
    }

    private void RefreshInstalledDetail()
    {
        var gpu = Gpu;
        if (gpu?.DriverVersion is not { } v)
        {
            InstalledDetail = "No NVIDIA driver is installed.";
            return;
        }
        var fromHistory = _app.History.BranchFor(v);
        if (fromHistory is not null) InstalledBranch = BranchNames.Parse(fromHistory);
        else
        {
            bool inGr = _gameReady.Any(r => r.Version == v), inStudio = _studio.Any(r => r.Version == v);
            InstalledBranch = inStudio && !inGr ? DriverBranch.Studio : inGr ? DriverBranch.GameReady : null;
        }
        var parts = new List<string> { InstalledBranch is { } b ? BranchNames.Display(b) : "Game Ready or Studio" };
        var date = _gameReady.Concat(_studio).FirstOrDefault(r => r.Version == v)?.ReleaseDate ?? gpu.DriverDate;
        if (date is { } d) parts.Add("released " + Fmt.Date(d));
        InstalledDetail = string.Join(" · ", parts);
    }

    // ---------------------------------------------------------------- latest drivers

    public DriverRowViewModel GameReady { get; }
    public DriverRowViewModel Studio { get; }
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string _checkedText = "";
    [ObservableProperty] private string _catalogError = "";
    public bool HasCatalogError => CatalogError.Length > 0;
    partial void OnCatalogErrorChanged(string value) => OnPropertyChanged(nameof(HasCatalogError));

    public bool PreferStudio => _app.Settings.Current.PreferredBranch == "studio";
    public DriverRowViewModel Preferred => PreferStudio ? Studio : GameReady;

    [RelayCommand]
    private Task Refresh() => CheckAsync(manual: true);

    /// <summary>Asks NVIDIA for the newest drivers. Returns the newer driver on the preferred branch, if any.</summary>
    public async Task<DriverRelease?> CheckAsync(bool manual)
    {
        if (IsChecking) return null;
        if (Gpu is null) await DetectAsync();
        var gpu = Gpu;
        if (gpu is null) return null;
        IsChecking = true;
        CatalogError = "";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _product ??= await DriverCatalog.ResolveProductAsync(gpu.ProductName, cts.Token);
            if (_product is not { } prod)
            {
                CatalogError = $"NVIDIA's driver lookup doesn't list \"{gpu.ProductName}\".";
                return null;
            }
            var gr = DriverCatalog.GetDriversAsync(prod.psid, prod.pfid, DriverBranch.GameReady, 25, cts.Token);
            var st = DriverCatalog.GetDriversAsync(prod.psid, prod.pfid, DriverBranch.Studio, 25, cts.Token);
            _gameReady = await gr;
            _studio = await st;
            _checkedAt = DateTime.Now;
            UpdateRows();
            return Preferred.IsNewer ? Preferred.Release : null;
        }
        catch (Exception ex)
        {
            Log.Warn("driver check: " + ex.Message);
            CatalogError = "Couldn't reach NVIDIA's driver lookup. Check your connection and try again.";
            if (manual) StatusText = "";
            return null;
        }
        finally
        {
            IsChecking = false;
            UpdateCheckedText();
        }
    }

    public void UpdateCheckedText() => CheckedText = _checkedAt is { } t ? "Checked " + Fmt.Ago(t) : "";

    public void UpdateRows()
    {
        RefreshInstalledDetail();
        var installed = Gpu?.DriverVersion;
        GameReady.Update(_gameReady.FirstOrDefault(), installed, InstalledBranch);
        Studio.Update(_studio.FirstOrDefault(), installed, InstalledBranch);
        OnPropertyChanged(nameof(Preferred));
        OnPropertyChanged(nameof(PreferStudio));
        if (installed is null) { StatusText = "No driver installed"; StatusKind = "warn"; }
        else if (Preferred.IsNewer) { StatusText = $"{BranchNames.Display(Preferred.Branch)} {Preferred.VersionText} is available"; StatusKind = "accent"; }
        else if (Preferred.HasRelease) { StatusText = "Up to date"; StatusKind = "good"; }
        else { StatusText = ""; StatusKind = ""; }
    }

    // ---------------------------------------------------------------- install options

    [ObservableProperty] private bool _physX;
    [ObservableProperty] private bool _hdAudio;
    [ObservableProperty] private bool _usbC;
    [ObservableProperty] private bool _clean;
    [ObservableProperty] private bool _restorePoint;
    [ObservableProperty] private bool _disableTelemetry;

    partial void OnPhysXChanged(bool value) { _app.Settings.Current.InstallPhysX = value; _app.Settings.Save(); }
    partial void OnHdAudioChanged(bool value) { _app.Settings.Current.InstallHdAudio = value; _app.Settings.Save(); }
    partial void OnUsbCChanged(bool value) { _app.Settings.Current.InstallUsbC = value; _app.Settings.Save(); }
    partial void OnCleanChanged(bool value) { _app.Settings.Current.CleanInstall = value; _app.Settings.Save(); }
    partial void OnRestorePointChanged(bool value) { _app.Settings.Current.CreateRestorePoint = value; _app.Settings.Save(); }
    partial void OnDisableTelemetryChanged(bool value) { _app.Settings.Current.DisableTelemetry = value; _app.Settings.Save(); }

    /// <summary>Settings page changes the same defaults; keep this page in step.</summary>
    public void ReloadOptions()
    {
        var s = _app.Settings.Current;
        PhysX = s.InstallPhysX;
        HdAudio = s.InstallHdAudio;
        UsbC = s.InstallUsbC;
        Clean = s.CleanInstall;
        RestorePoint = s.CreateRestorePoint;
        DisableTelemetry = s.DisableTelemetry;
        UpdateRows();
    }

    // ---------------------------------------------------------------- install progress

    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private string _progressTitle = "";
    [ObservableProperty] private string _progressMessage = "";
    [ObservableProperty] private string _progressDetail = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private bool _resultOk;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _warningsText = "";
    [ObservableProperty] private bool _restartNeeded;
    [ObservableProperty] private string _elapsedText = "";
    public bool HasWarnings => WarningsText.Length > 0;
    public bool ResultFailed => HasResult && !ResultOk;
    public bool CanInstall => !IsInstalling && Gpu is not null;
    public ObservableCollection<StepViewModel> Steps { get; } = new();

    partial void OnWarningsTextChanged(string value) => OnPropertyChanged(nameof(HasWarnings));
    partial void OnResultOkChanged(bool value) => OnPropertyChanged(nameof(ResultFailed));
    partial void OnHasResultChanged(bool value) => OnPropertyChanged(nameof(ResultFailed));
    partial void OnIsInstallingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstall));
        GameReady.NotifyCanInstall();
        Studio.NotifyCanInstall();
    }

    public void StartInstall(DriverRelease? release, SavedPackage? saved)
    {
        if (IsInstalling || Gpu is null) return;
        var from = Gpu.DriverVersion ?? "";
        var version = release?.Version ?? saved?.Meta.Version ?? "";
        var branch = release?.Branch ?? saved?.Branch ?? DriverBranch.GameReady;
        bool rollback = saved is not null && Versions.CompareDriver(version, from) < 0;
        var req = new InstallRequest
        {
            Release = release,
            Saved = saved,
            PhysX = PhysX,
            HdAudio = HdAudio,
            UsbC = UsbC,
            Clean = Clean,
            RestorePoint = RestorePoint,
            DisableTelemetry = DisableTelemetry,
            IsRollback = rollback,
            FromVersion = from,
            IsNotebook = Gpu.IsNotebook,
        };
        _ = RunInstallAsync(req, branch);
    }

    private async Task RunInstallAsync(InstallRequest req, DriverBranch branch)
    {
        IsInstalling = true;
        ShowProgress = true;
        HasResult = false;
        ResultText = "";
        WarningsText = "";
        RestartNeeded = false;
        CanCancel = true;
        ProgressTitle = (req.IsRollback ? "Rolling back to " : Clean ? "Clean installing " : "Installing ") +
                        $"{BranchNames.Display(branch)} {req.Version}";
        ProgressMessage = "Getting ready…";
        ProgressDetail = "";
        ProgressIndeterminate = true;

        Steps.Clear();
        if (req.Saved is null) Steps.Add(new StepViewModel(InstallStage.Download, "Download from NVIDIA"));
        Steps.Add(new StepViewModel(InstallStage.Verify, "Check NVIDIA's signature"));
        Steps.Add(new StepViewModel(InstallStage.Unpack, "Unpack the parts you picked"));
        if (req.RestorePoint) Steps.Add(new StepViewModel(InstallStage.RestorePoint, "Create a restore point"));
        Steps.Add(new StepViewModel(InstallStage.Install, "Install the driver"));
        if (req.DisableTelemetry) Steps.Add(new StepViewModel(InstallStage.Telemetry, "Turn off telemetry tasks"));
        InstallStarted?.Invoke();

        var sw = Stopwatch.StartNew();
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => ElapsedText = $"{(int)sw.Elapsed.TotalMinutes}:{sw.Elapsed.Seconds:00} elapsed";
        timer.Start();

        _installCts = new CancellationTokenSource();
        var outcome = await DriverInstaller.RunAsync(req, u => Ui(() => OnUpdate(u)), _installCts.Token);
        timer.Stop();

        // mark steps
        foreach (var s in Steps)
        {
            if (s.IsActive)
            {
                s.IsActive = false;
                if (outcome.Ok) s.IsDone = true;
                else if (!outcome.Cancelled) s.IsFailed = true;
            }
            else if (outcome.Ok) s.IsDone = true;
        }

        IsInstalling = false;
        CanCancel = false;
        HasResult = true;
        ResultOk = outcome.Ok;
        RestartNeeded = outcome.RestartNeeded;
        ResultText = outcome.Message;
        WarningsText = string.Join("\n", outcome.Warnings);
        ProgressIndeterminate = false;
        ProgressValue = outcome.Ok ? 100 : 0;
        ProgressMessage = "";
        ProgressDetail = "";

        if (!outcome.Cancelled)
        {
            _app.History.Add(new HistoryEntry
            {
                Kind = req.IsRollback ? "rollback" : req.Clean ? "clean-install" : "install",
                Version = req.Version,
                Branch = BranchNames.Key(branch),
                FromVersion = req.FromVersion,
                Result = outcome.Ok ? "success" : outcome.Cancelled ? "cancelled" : "failed",
                Details = outcome.Message + (outcome.Warnings.Count > 0 ? " " + string.Join(" ", outcome.Warnings) : ""),
                Components = outcome.Components.ToList(),
            });
        }

        await DetectAsync();
        UpdateRows();
        InstallFinished?.Invoke();
        if (outcome.Ok)
            _app.Toast(outcome.RestartNeeded ? "Restart to finish the driver install" : "Driver installed",
                outcome.Message, null);
    }

    private void OnUpdate(InstallUpdate u)
    {
        if (u.Stage is InstallStage.RestorePoint or InstallStage.Install or InstallStage.Telemetry or InstallStage.Finish)
            CanCancel = false;
        ProgressMessage = u.Message;
        ProgressDetail = u.Detail;
        ProgressIndeterminate = u.Progress < 0;
        if (u.Progress >= 0) ProgressValue = u.Progress * 100;

        bool reached = false;
        var target = u.Stage == InstallStage.Prepare ? InstallStage.Unpack : u.Stage;
        foreach (var s in Steps)
        {
            if (s.Stage == target)
            {
                reached = true;
                s.IsActive = u.Stage != InstallStage.Finish;
                s.IsDone = false;
            }
            else if (!reached)
            {
                s.IsActive = false;
                s.IsDone = true;
            }
            else
            {
                s.IsActive = false;
            }
        }
        if (u.Stage == InstallStage.Finish)
            foreach (var s in Steps) { s.IsActive = false; s.IsDone = true; }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (CanCancel) _installCts?.Cancel();
    }

    [RelayCommand]
    private void Dismiss()
    {
        if (IsInstalling) return;
        ShowProgress = false;
        HasResult = false;
    }

    [RelayCommand]
    private void RestartWindows()
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5 /c \"Restarting to finish the NVIDIA driver install (Pitstop)\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("restart windows", ex);
        }
    }

    [RelayCommand]
    private void InstallPreferred() => Preferred.InstallCommand.Execute(null);
}
