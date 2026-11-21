using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Pitstop.Views;

/// <summary>A small pop-up in the bottom-right corner (no Windows toast plumbing needed). Closes itself after 10 seconds.</summary>
public partial class ToastWindow : Window
{
    private readonly Action? _onClick;
    private bool _closing;

    public ToastWindow() => InitializeComponent();

    public ToastWindow(string title, string message, Action? onClick) : this()
    {
        _onClick = onClick;
        TitleText.Text = title;
        MessageText.Text = message;

        CloseButton.Click += (_, e) =>
        {
            e.Handled = true;
            FadeOutAndClose();
        };
        Card.PointerPressed += (_, e) =>
        {
            if (e.Handled) return;
            _onClick?.Invoke();
            FadeOutAndClose();
        };
        Opened += (_, _) =>
        {
            PlaceBottomRight();
            Opacity = 1;
            DispatcherTimer.RunOnce(FadeOutAndClose, TimeSpan.FromSeconds(10));
        };
    }

    private void PlaceBottomRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var margin = (int)(16 * scale);
        Position = new PixelPoint(
            area.Right - (int)(Bounds.Width * scale) - margin,
            area.Bottom - (int)(Bounds.Height * scale) - margin);
    }

    private void FadeOutAndClose()
    {
        if (_closing) return;
        _closing = true;
        Opacity = 0;
        DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(220));
    }
}
