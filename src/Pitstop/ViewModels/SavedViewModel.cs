using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pitstop.Models;
using Pitstop.Services;

namespace Pitstop.ViewModels;

public partial class SavedRowViewModel : ViewModelBase
{
    private readonly SavedViewModel _owner;

    public SavedRowViewModel(SavedViewModel owner, SavedPackage p, string? installed)
    {
        _owner = owner;
        Package = p;
        IsCurrent = p.Meta.Version == installed;
        int cmp = Versions.CompareDriver(p.Meta.Version, installed);
        ActionText = IsCurrent ? "Reinstall" : cmp < 0 ? "Roll back" : "Install";
    }

    public SavedPackage Package { get; }
    public string Version => Package.Meta.Version;
    public string BranchText => BranchNames.Display(Package.Branch);
    public string SizeText => Fmt.Bytes(Package.SizeOnDisk);
    public string DateText => Package.Meta.LastInstalledAt is { } t ? "Last installed " + Fmt.Date(t) : "Downloaded " + Fmt.Date(Package.Meta.DownloadedAt);
    public bool IsCurrent { get; }
    public string ActionText { get; }
    public bool CanInstall => _owner.CanInstall;
    [ObservableProperty] private bool _confirmDelete;

    [RelayCommand]
    private void Install() => _owner.Install(this);

    [RelayCommand]
    private void Delete()
    {
        if (!ConfirmDelete)
        {
            ConfirmDelete = true;
            return;
        }
        _owner.Delete(this);
    }

    [RelayCommand]
    private void CancelDelete() => ConfirmDelete = false;
}

public partial class SavedViewModel : ViewModelBase
{
    private readonly DriverViewModel _driver;

    public SavedViewModel(DriverViewModel driver)
    {
        _driver = driver;
        driver.InstallFinished += Refresh;
    }

    public ObservableCollection<SavedRowViewModel> Items { get; } = new();
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private bool _isEmpty = true;
    public bool HasItems => !IsEmpty;
    partial void OnIsEmptyChanged(bool value) => OnPropertyChanged(nameof(HasItems));
    public bool CanInstall => _driver.CanInstall;

    public void Refresh()
    {
        Items.Clear();
        var list = PackageStore.List();
        foreach (var p in list) Items.Add(new SavedRowViewModel(this, p, _driver.Gpu?.DriverVersion));
        IsEmpty = Items.Count == 0;
        TotalText = IsEmpty ? "" : $"{Items.Count} saved · {Fmt.Bytes(list.Sum(p => p.SizeOnDisk))} on disk";
        OnPropertyChanged(nameof(CanInstall));
    }

    public void Install(SavedRowViewModel row) => _driver.StartInstall(null, row.Package);

    public void Delete(SavedRowViewModel row)
    {
        PackageStore.Delete(row.Package);
        Refresh();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(Paths.Packages);
        Shell.Open(Paths.Packages);
    }
}

public sealed class HistoryRowViewModel
{
    public HistoryRowViewModel(HistoryEntry e)
    {
        var branch = e.Branch.Length > 0 ? BranchNames.Display(BranchNames.Parse(e.Branch)) + " " : "";
        Title = e.Kind switch
        {
            "rollback" => $"Rolled back to {branch}{e.Version}",
            "clean-install" => $"Clean install of {branch}{e.Version}",
            _ => $"Installed {branch}{e.Version}",
        };
        if (e.Result == "failed") Title = Title.Replace("Installed", "Install of").Replace("Rolled back to", "Rollback to").Replace("Clean install of", "Clean install of") + " failed";
        if (e.Result == "cancelled") Title += " (cancelled)";
        var sub = new List<string> { Fmt.DateAndTime(e.Date) };
        if (e.FromVersion.Length > 0 && e.FromVersion != e.Version) sub.Add("from " + e.FromVersion);
        if (e.Components.Count > 0) sub.Add(string.Join(", ", e.Components));
        Subtitle = string.Join(" · ", sub);
        Details = e.Details;
        IsGood = e.Result == "success";
        IsBad = e.Result == "failed";
        IsWarn = e.Result == "cancelled";
        ResultText = e.Result switch { "success" => "Done", "failed" => "Failed", _ => "Cancelled" };
        Icon = e.Kind == "rollback" ? "" : "";
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string Details { get; }
    public bool HasDetails => Details.Length > 0;
    public bool IsGood { get; }
    public bool IsBad { get; }
    public bool IsWarn { get; }
    public string ResultText { get; }
    public string Icon { get; }
}

public partial class HistoryViewModel : ViewModelBase
{
    private readonly HistoryService _history;

    public HistoryViewModel(HistoryService history)
    {
        _history = history;
        history.Changed += () => Ui(Refresh);
        Refresh();
    }

    public ObservableCollection<HistoryRowViewModel> Items { get; } = new();
    [ObservableProperty] private bool _isEmpty = true;
    public bool HasItems => !IsEmpty;
    partial void OnIsEmptyChanged(bool value) => OnPropertyChanged(nameof(HasItems));

    public void Refresh()
    {
        Items.Clear();
        foreach (var e in _history.Entries) Items.Add(new HistoryRowViewModel(e));
        IsEmpty = Items.Count == 0;
    }
}
