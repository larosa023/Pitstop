using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Pitstop.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>Runs on the UI thread (directly if already there).</summary>
    protected static void Ui(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }
}
