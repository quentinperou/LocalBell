using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LocalBell.App.ViewModels;
using static LocalBell.App.Services.NativeMethods;

namespace LocalBell.App;

/// <summary>
/// The flyout of the taskbar widget, above it: the missed rings ("Rappeler"), the PCs, the message, everyone. Active
/// while shown (the message can be typed), it hides when the user clicks elsewhere; the missed rings are then seen.
/// </summary>
public partial class WidgetFlyout : Window
{
    private readonly MainViewModel viewModel;
    private DateTime hiddenAt;

    public WidgetFlyout(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = this.viewModel = viewModel;
    }

    /// <summary>
    /// Shows it with its left edge on <paramref name="left"/> and its bottom just above <paramref name="barTop"/> (screen
    /// pixels). A click on the widget that closed it a moment ago (it lost the focus to that click) does not open it again.
    /// </summary>
    public void ShowAbove(int left, int barTop)
    {
        if ((DateTime.Now - hiddenAt).TotalMilliseconds < 300) return;
        Left = -10000;
        Top = -10000;
        Show();
        UpdateLayout();
        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = VisualTreeHelper.GetDpi(this);
        int shadow = (int)Math.Round(12 * dpi.DpiScaleX), gap = (int)Math.Round(4 * dpi.DpiScaleY);
        int height = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        SetWindowPos(hwnd, HWND_TOPMOST, left - shadow, barTop - height + shadow - gap, 0, 0, SWP_NOSIZE);
        SetForegroundWindow(hwnd);
        Activate();
    }

    private void Window_Deactivated(object? sender, EventArgs e) => HideFlyout();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) HideFlyout();
    }

    private void HideFlyout()
    {
        if (!IsVisible) return;
        hiddenAt = DateTime.Now;
        Hide();
        viewModel.ClearMissed();
    }
}
