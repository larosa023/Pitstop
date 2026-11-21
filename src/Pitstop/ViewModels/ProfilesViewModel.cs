using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pitstop.Models;
using Pitstop.Native;
using Pitstop.Services;

namespace Pitstop.ViewModels;

public sealed class OptionItem
{
    public required string Label { get; init; }
    public SettingOption? Option { get; init; }
    public bool IsDefault { get; init; }
    public bool IsCustom { get; init; }
    /// <summary>A value Pitstop has no name for: shown, kept as-is.</summary>
    public uint? RawValue { get; init; }

    public override string ToString() => Label;
}

public partial class SettingRowViewModel : ViewModelBase
{
    private readonly ProfilesViewModel _owner;
    private OptionItem? _original;
    private uint _originalCustom;

    public SettingRowViewModel(ProfilesViewModel owner, SettingDef def, bool isGlobal, IReadOnlyDictionary<uint, NvApi.SettingRead> values)
    {
        _owner = owner;
        Def = def;
        Options = new ObservableCollection<OptionItem>();

        var match = def.Match(values);
        values.TryGetValue(def.PrimaryId, out var primary);
        bool here = primary.Found && primary.Location == 0;

        string defaultLabel;
        if (isGlobal) defaultLabel = "Driver default";
        else if (primary.Found && !here) defaultLabel = $"Use global setting ({match?.Label ?? Describe(def, primary.Value)})";
        else defaultLabel = "Use global setting";
        var defItem = new OptionItem { Label = defaultLabel, IsDefault = true };
        Options.Add(defItem);
        foreach (var o in def.Options) Options.Add(new OptionItem { Label = o.Label, Option = o });
        OptionItem? custom = null;
        if (def.AllowCustom)
        {
            custom = new OptionItem { Label = "Custom…", IsCustom = true };
            Options.Add(custom);
        }

        OptionItem selected = defItem;
        if (here)
        {
            if (match is not null) selected = Options.First(o => o.Option == match);
            else if (custom is not null)
            {
                selected = custom;
                _customValue = primary.Value;
            }
            else
            {
                selected = new OptionItem { Label = $"Other ({primary.Value})", RawValue = primary.Value };
                Options.Add(selected);
            }
            Source = primary.IsPredefined ? "Set by NVIDIA for this game" : "Changed by you";
            IsUserSet = !primary.IsPredefined;
        }
        else
        {
            Source = "";
        }
        _selected = selected;
        _original = selected;
        _originalCustom = (uint)_customValue;
    }

    private static string Describe(SettingDef def, uint value) =>
        def.Options.FirstOrDefault(o => o.Writes.Length > 0 && o.Writes[0].Value == value)?.Label ?? value.ToString();

    public SettingDef Def { get; }
    public string Label => Def.Label;
    public string Help => Def.Help;
    public string Source { get; }
    public bool HasSource => Source.Length > 0;
    public bool IsUserSet { get; }
    public ObservableCollection<OptionItem> Options { get; }

    [ObservableProperty] private OptionItem? _selected;
    [ObservableProperty] private decimal _customValue = 60;

    public bool ShowCustom => Selected?.IsCustom == true;
    public decimal CustomMin => Def.CustomMin;
    public decimal CustomMax => Def.CustomMax;

    public bool IsChanged => Selected != _original || (Selected?.IsCustom == true && (uint)CustomValue != _originalCustom);

    partial void OnSelectedChanged(OptionItem? value)
    {
        OnPropertyChanged(nameof(ShowCustom));
        OnPropertyChanged(nameof(IsChanged));
        _owner.RowChanged();
    }

    partial void OnCustomValueChanged(decimal value)
    {
        OnPropertyChanged(nameof(IsChanged));
        _owner.RowChanged();
    }

    public void SetDefault() => Selected = Options.FirstOrDefault(o => o.IsDefault);

    public IEnumerable<SettingChange> ToChanges()
    {
        var sel = Selected;
        if (sel is null || sel.RawValue is not null) yield break;
        if (sel.IsDefault)
        {
            foreach (var id in Def.AllIds) yield return new SettingChange { Id = id, Restore = true, Optional = GameSettings.IsOptional(id) };
        }
        else if (sel.IsCustom)
        {
            yield return new SettingChange { Id = Def.PrimaryId, Value = (uint)Math.Clamp(CustomValue, Def.CustomMin, Def.CustomMax) };
        }
        else if (sel.Option is { } o)
        {
            foreach (var w in o.Writes) yield return new SettingChange { Id = w.Id, Value = w.Value, Optional = w.Optional };
        }
    }
}

public partial class ProfilesViewModel : ViewModelBase
{
    private readonly AppServices _app;
    private List<ProfileSummary> _all = new();
    private bool _loaded;

    public ProfilesViewModel(AppServices app) => _app = app;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isAvailable = true;
    [ObservableProperty] private string _unavailableText = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private ProfileSummary? _selectedProfile;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _isLoadingSettings;

    [ObservableProperty] private List<ProfileSummary> _profiles = new();
    public ObservableCollection<SettingRowViewModel> Rows { get; } = new();
    public bool HasSelection => SelectedProfile is not null;
    public bool HasChanges => Rows.Any(r => r.IsChanged);
    public bool HasStatus => Status.Length > 0;
    public string SelectedTitle => SelectedProfile?.DisplayName ?? "";
    public string SelectedApps => SelectedProfile?.AppsText ?? "";

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnSearchChanged(string value) => ApplyFilter();

    partial void OnSelectedProfileChanged(ProfileSummary? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedApps));
        _ = LoadSettingsAsync();
    }

    public void RowChanged()
    {
        OnPropertyChanged(nameof(HasChanges));
        ApplyCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Loads the profile list the first time the page opens.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded || IsLoading) return;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsLoading = true;
        try
        {
            if (!await Task.Run(() => DrsService.Available))
            {
                IsAvailable = false;
                UnavailableText = DrsService.UnavailableReason + " Game settings need an NVIDIA driver to be installed.";
                return;
            }
            var keep = SelectedProfile?.Name;
            _all = await Task.Run(DrsService.LoadProfiles);
            _loaded = true;
            IsAvailable = true;
            ApplyFilter();
            SelectedProfile = _all.FirstOrDefault(p => p.Name == keep) ?? _all.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Error("profiles load", ex);
            IsAvailable = false;
            UnavailableText = "Couldn't read the driver's game profiles: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        var q = Search.Trim();
        var keep = SelectedProfile;
        Profiles = _all.Where(p => p.Matches(q)).ToList();
        CountText = q.Length == 0 ? $"{_all.Count:N0} profiles" : $"{Profiles.Count:N0} of {_all.Count:N0}";
        if (keep is not null && Profiles.Contains(keep)) SelectedProfile = keep;
    }

    private async Task LoadSettingsAsync()
    {
        var p = SelectedProfile;
        Rows.Clear();
        if (p is null) return;
        IsLoadingSettings = true;
        try
        {
            var defs = GameSettings.All.Where(d => p.IsGlobal ? !d.GameOnly : !d.GlobalOnly).ToList();
            var values = await Task.Run(() => DrsService.ReadSettings(p.Name, p.IsGlobal, defs.SelectMany(d => d.AllIds)));
            if (SelectedProfile != p) return;
            foreach (var d in defs) Rows.Add(new SettingRowViewModel(this, d, p.IsGlobal, values));
        }
        catch (Exception ex)
        {
            Log.Error("profile settings", ex);
            SetStatus("Couldn't read this profile: " + ex.Message, true);
        }
        finally
        {
            IsLoadingSettings = false;
            RowChanged();
        }
    }

    private void SetStatus(string text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }

    // ---------------------------------------------------------------- apply

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private async Task ApplyAsync()
    {
        var p = SelectedProfile;
        if (p is null) return;
        var change = new ProfileChange { Name = p.Name, IsGlobal = p.IsGlobal };
        foreach (var r in Rows.Where(r => r.IsChanged)) change.Settings.AddRange(r.ToChanges());
        if (change.Settings.Count == 0) return;
        await WriteAsync(new List<ProfileChange> { change }, $"Saved {p.DisplayName}.");
        await LoadSettingsAsync();
    }

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private Task DiscardAsync() => LoadSettingsAsync();

    [RelayCommand]
    private void ResetAll()
    {
        foreach (var r in Rows) r.SetDefault();
    }

    private async Task<bool> WriteAsync(List<ProfileChange> changes, string done)
    {
        if (DriverInstaller.IsBusy)
        {
            SetStatus("Wait for the driver install to finish first.", true);
            return false;
        }
        IsBusy = true;
        SetStatus("Waiting for you to approve the admin prompt…");
        try
        {
            var job = new ElevatedJob { Kind = "profiles", Profiles = changes };
            var result = await ElevatedRunner.RunAsync(job, null, CancellationToken.None);
            if (result.Declined)
            {
                SetStatus("You declined the admin prompt, so nothing was saved.", true);
                return false;
            }
            if (!result.Status.Ok)
            {
                SetStatus("Couldn't save: " + result.Status.Error, true);
                return false;
            }
            SetStatus(done + (result.Status.Warnings.Count > 0 ? " " + string.Join(" ", result.Status.Warnings) : "") + " Restart a running game to pick it up.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("profiles write", ex);
            SetStatus("Couldn't save: " + ex.Message, true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------------------------------------------------------- export / import

    [RelayCommand]
    private Task ExportThisAsync() => SelectedProfile is { } p ? ExportAsync(new[] { p }, SafeName(p.DisplayName)) : Task.CompletedTask;

    [RelayCommand]
    private Task ExportAllAsync() => ExportAsync(_all, "All changed profiles");

    private async Task ExportAsync(IReadOnlyList<ProfileSummary> which, string suggested)
    {
        if (_app.SaveFile is null) return;
        try
        {
            IsBusy = true;
            SetStatus("Collecting your settings…");
            var list = await Task.Run(() => DrsService.Export(which));
            if (list.Count == 0)
            {
                SetStatus(which.Count == 1 ? "This profile has no settings you changed, so there's nothing to export." : "You haven't changed any of these settings in any profile yet.", true);
                return;
            }
            var path = await _app.SaveFile($"{suggested}.pitstop.json", "Pitstop profiles");
            if (path is null)
            {
                SetStatus("");
                return;
            }
            var file = new ProfileExportFile { Driver = InstalledDriver(), Profiles = list };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(file, PitstopJson.Default.ProfileExportFile));
            SetStatus($"Exported {list.Count} profile{(list.Count == 1 ? "" : "s")} to {Path.GetFileName(path)}.");
        }
        catch (Exception ex)
        {
            Log.Error("export", ex);
            SetStatus("Export failed: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (_app.OpenFile is null) return;
        try
        {
            var path = await _app.OpenFile("Pitstop profiles");
            if (path is null) return;
            var file = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), PitstopJson.Default.ProfileExportFile);
            var changes = file is null ? new List<ProfileChange>() : DrsService.ToChanges(file);
            if (changes.Count == 0)
            {
                SetStatus("That file has no settings Pitstop can import.", true);
                return;
            }
            if (await WriteAsync(changes, $"Imported {changes.Count} profile{(changes.Count == 1 ? "" : "s")}."))
                await ReloadAsync();
        }
        catch (JsonException)
        {
            SetStatus("That isn't a Pitstop profiles file.", true);
        }
        catch (Exception ex)
        {
            Log.Error("import", ex);
            SetStatus("Import failed: " + ex.Message, true);
        }
    }

    private static string InstalledDriver()
    {
        try { return GpuService.InstalledVersionFromRegistry() ?? ""; }
        catch { return ""; }
    }

    private static string SafeName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
}
