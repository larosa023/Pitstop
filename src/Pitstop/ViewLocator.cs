using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Pitstop.ViewModels;
using Pitstop.Views;

namespace Pitstop;

/// <summary>Maps each page view-model to its view. No reflection, so it stays fast and trim-safe.</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? data) => data switch
    {
        DriverViewModel => new DriverView(),
        DriverRowViewModel => new DriverRowView(),
        SavedViewModel => new SavedView(),
        ProfilesViewModel => new ProfilesView(),
        HistoryViewModel => new HistoryView(),
        SettingsViewModel => new SettingsView(),
        _ => new TextBlock { Text = "Page not found" },
    };

    public bool Match(object? data) => data is DriverViewModel or DriverRowViewModel or SavedViewModel
        or ProfilesViewModel or HistoryViewModel or SettingsViewModel;
}
