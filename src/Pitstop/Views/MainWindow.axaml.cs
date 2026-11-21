using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Pitstop.ViewModels;

namespace Pitstop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>Wires the file pickers (export/import) to this window.</summary>
    public void Attach(MainWindowViewModel vm)
    {
        DataContext = vm;
        vm.App.SaveFile = async (suggested, kind) =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export game settings",
                SuggestedFileName = suggested,
                DefaultExtension = "json",
                FileTypeChoices = new[] { new FilePickerFileType(kind) { Patterns = new[] { "*.json" } } },
            });
            return file?.TryGetLocalPath();
        };
        vm.App.OpenFile = async kind =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import game settings",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(kind) { Patterns = new[] { "*.json" } } },
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        };
    }
}
