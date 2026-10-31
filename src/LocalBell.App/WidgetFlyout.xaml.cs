using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LocalBell.App.Services;
using LocalBell.App.ViewModels;
using static LocalBell.App.Services.NativeMethods;
using Forms = System.Windows.Forms;

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
    /// pixels): above the widget. A click on the widget that closed it a moment ago (it lost the focus to that click) does
    /// not open it again.
    /// </summary>
    public void ShowAbove(int left, int barTop) => ShowAt(barTop, _ => left);

    /// <summary>Shows it centred on <paramref name="x"/> above <paramref name="barTop"/>: above the bell near the clock.</summary>
    public void ShowNear(int x, int barTop) => ShowAt(barTop, width => x - width / 2);

    // Its place, from its width once shown; kept within the work area of its screen (near the clock, it would go past
    // the right edge).
    private void ShowAt(int barTop, Func<int, int> leftFor)
    {
        if ((DateTime.Now - hiddenAt).TotalMilliseconds < 300) return;
        ShowUpdate();
        Left = -10000;
        Top = -10000;
        Show();
        UpdateLayout();
        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = VisualTreeHelper.GetDpi(this);
        int shadow = (int)Math.Round(12 * dpi.DpiScaleX), gap = (int)Math.Round(4 * dpi.DpiScaleY);
        int width = (int)Math.Round(ActualWidth * dpi.DpiScaleX) - 2 * shadow;
        int height = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        int left = leftFor(width);
        // The screen of the click (on the widget or the bell near the clock).
        var screen = Forms.Screen.FromPoint(Forms.Control.MousePosition);
        var area = screen.WorkingArea;
        left = Math.Clamp(left, area.Left + gap, Math.Max(area.Left + gap, area.Right - width - gap));
        // Above the taskbar, or below it when it is on top (Windows 11 26H2 lets it be; the audit, October 31).
        int top = area.Top > screen.Bounds.Top ? area.Top + gap - shadow : barTop - height + shadow - gap;
        SetWindowPos(hwnd, HWND_TOPMOST, left - shadow, top, 0, 0, SWP_NOSIZE);
        SetForegroundWindow(hwnd);
        Activate();
    }

    // A new version, unless its banner was closed in the window or it is installing.
    private void ShowUpdate()
    {
        var release = UpdateBar.Offered;
        UpdateBanner.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
        if (release is not null) UpdateTitle.Text = Strings.Format("UpdateFlyoutTitle", release.Version);
    }

    private void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        ((App)Application.Current).InstallUpdate();
    }

    private void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (Updater.Available is { } release) Updater.OpenReleasePage(release);
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
