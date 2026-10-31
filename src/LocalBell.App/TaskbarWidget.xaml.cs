using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LocalBell.App.Services;
using Microsoft.Win32;
using static LocalBell.App.Services.NativeMethods;

namespace LocalBell.App;

/// <summary>
/// The widget in the taskbar (the approved mock-ups): the bell alone (a click opens the flyout), or, with a favourite,
/// the bell and its name (a click rings it; what its row says shows in place of the name for a while) and an arrow (the
/// flyout); a moon in place of the bell in "do not disturb"; a red badge counting the missed rings; a right click opens
/// the menu of the bell near the clock.
/// Windows lets no app add to its taskbar: this is a window of its own laid over it, as NowPlaying's widget: after the
/// weather of Windows 11, or near the clock (Windows 10, icons aligned left, no room), hidden over full-screen apps and
/// while an auto-hidden taskbar is away. Its owner is the taskbar: an owned window always stays above its owner, even
/// when Windows lifts the taskbar over every window while Start or search is open (a window of the app's own went under
/// it then, October 31), and whatever raises the taskbar (NowPlaying's widget did, every 15 s). That tie lets the two
/// threads share their input: the window runs in the widget process only (<see cref="TaskbarClient"/>), which the app
/// ends if it ever stops answering (<see cref="TaskbarHost"/>). It shows what the app sends (<see cref="Apply"/>) and
/// gives its clicks back; the flyout and the menu are the app's.
/// </summary>
public partial class TaskbarWidget : Window
{
    private const int AppBarMessage = WM_APP + 2;

    private readonly int appProcessId;
    private readonly DispatcherTimer autoHideTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer othersTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer buttonsAgainTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private IntPtr hwnd;
    private IntPtr foregroundHook;
    private WinEventProc? foregroundProc; // kept alive while hooked
    private bool appBar;
    private bool fullScreen;
    private TaskbarState? state;
    private Brush idle = Brushes.Transparent;
    private Brush hover = Brushes.Transparent;

    /// <param name="appProcessId">The app's process: its windows are not widgets of other apps.</param>
    public TaskbarWidget(int appProcessId)
    {
        InitializeComponent();
        this.appProcessId = appProcessId;
        autoHideTimer.Tick += (_, _) => Place();
        // The widgets of other apps come and go (NowPlaying's shows only while something plays): looked for again. The
        // buttons of the taskbar too: at once when it changes (alignment of its icons, the notification area wider or
        // narrower: with the icons on the left the weather moves with it; the author's try, October 31), else every
        // 10 s (the weather's text, so its width, changes now and then).
        othersTimer.Tick += (_, _) =>
        {
            string shape = TaskbarShape();
            if (shape != taskbarShape)
            {
                taskbarShape = shape;
                FindTaskbarButtons();
                buttonsAgainTimer.Stop();
                buttonsAgainTimer.Start(); // once they have slid to their new places
            }
            else if (DateTime.UtcNow - buttonsFoundAt > TimeSpan.FromSeconds(10)) FindTaskbarButtons();
            Place();
        };
        othersTimer.Start();
        buttonsAgainTimer.Tick += (_, _) =>
        {
            buttonsAgainTimer.Stop();
            FindTaskbarButtons();
        };
        SizeChanged += (_, _) => Place();
        MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            MenuRequested?.Invoke(BarTop());
        };
        foreach (var part in new[] { BellPart, RingPart, ArrowPart })
        {
            part.MouseEnter += (_, _) => part.Background = hover;
            part.MouseLeave += (_, _) => part.Background = idle;
        }
        ApplyColors();
    }

    /// <summary>The bell or the arrow: the app opens or closes its flyout above the widget (its left edge, the taskbar's top).</summary>
    public event Action<int, int>? FlyoutRequested;

    /// <summary>The favourite: the app rings it.</summary>
    public event EventHandler? RingRequested;

    /// <summary>A right click: the app shows the menu of the bell near the clock above the taskbar (its top).</summary>
    public event Action<int>? MenuRequested;

    /// <summary>A line for the developer log of the app (the place of the widget when it changes).</summary>
    public event Action<string>? Log;

    // ---- What it shows ----

    /// <summary>What the app sends: the favourite or the bell, the moon, the badge, the place, the tooltips.</summary>
    public void Apply(TaskbarState state)
    {
        this.state = state;
        bool dnd = state.DoNotDisturb;
        BellIcon.Visibility = FavoriteBell.Visibility = dnd ? Visibility.Collapsed : Visibility.Visible;
        MoonIcon.Visibility = FavoriteMoon.Visibility = dnd ? Visibility.Visible : Visibility.Collapsed;

        bool favorite = state.Favorite is not null;
        BellPart.Visibility = favorite ? Visibility.Collapsed : Visibility.Visible;
        FavoritePart.Visibility = favorite ? Visibility.Visible : Visibility.Collapsed;
        FavoriteName.Text = state.Favorite ?? "";
        FavoriteName.Opacity = state.FavoriteOnline ? 1 : 0.55;
        RingPart.ToolTip = state.RingTooltip;
        BellPart.ToolTip = state.BellTooltip;

        Badge.Visibility = state.Missed > 0 ? Visibility.Visible : Visibility.Collapsed;
        BadgeText.Text = state.Missed > 9 ? "9+" : state.Missed.ToString();
        foreach (var part in new[] { BellPart, RingPart, ArrowPart })
            if (!part.IsMouseOver) part.Background = idle;
        Place();
    }

    /// <summary>White on a dark taskbar, near black on a light one; the hover as the taskbar's own buttons.</summary>
    private void ApplyColors()
    {
        bool dark = WindowsTheme.TaskbarIsDark;
        var ink = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
        idle = new SolidColorBrush(dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0C, 0x00, 0x00, 0x00));
        hover = new SolidColorBrush(dark ? Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        BellIcon.Fill = FavoriteBell.Fill = ink;
        MoonIcon.Foreground = FavoriteMoon.Foreground = FavoriteName.Foreground = Arrow.Foreground = ink;
        Divider.Fill = new SolidColorBrush(dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x00, 0x00, 0x00));
        foreach (var part in new[] { BellPart, RingPart, ArrowPart })
            if (!part.IsMouseOver) part.Background = idle;
    }

    // ---- Clicks ----

    private void BellPart_Click(object sender, MouseButtonEventArgs e) => AskFlyout();

    private void ArrowPart_Click(object sender, MouseButtonEventArgs e) => AskFlyout();

    private void RingPart_Click(object sender, MouseButtonEventArgs e) => RingRequested?.Invoke(this, EventArgs.Empty);

    private void AskFlyout()
    {
        GetWindowRect(hwnd, out var widget);
        FlyoutRequested?.Invoke(widget.Left, BarTop());
    }

    // ---- Its place on the taskbar (as NowPlaying's) ----

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        hwnd = new WindowInteropHelper(this).Handle;
        // Not in Alt+Tab, never activated by a click (the app in front keeps the keyboard).
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        KeepTransparent();
        SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, Tray);

        // An app bar that takes no room: Windows tells it when an app goes full screen on its screen.
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd, uCallbackMessage = AppBarMessage };
        appBar = SHAppBarMessage(ABM_NEW, ref data) != IntPtr.Zero;

        // Another topmost window may come over it when the window in front changes: back on top then.
        foregroundProc = (_, _, _, _, _, _, _) => KeepOnTop();
        foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, foregroundProc, 0, 0,
            WINEVENT_OUTOFCONTEXT);
        FindTaskbarButtons();
    }

    /// <summary>
    /// See-through where nothing is drawn, composed by DWM: no backdrop, no rounded corners nor border from Windows 11
    /// (NowPlaying's SeeThrough).
    /// </summary>
    private void KeepTransparent()
    {
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        int backdrop = DWMSBT_NONE, corners = DWMWCP_DONOTROUND, border = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    private static IntPtr Tray => FindWindow("Shell_TrayWnd", null);

    /// <summary>The top of the taskbar, in screen pixels: the flyout and the menu open above it.</summary>
    private static int BarTop() => GetWindowRect(Tray, out var bar) ? bar.Top : (int)SystemParameters.WorkArea.Bottom;

    /// <summary>Puts the widget at its place on the taskbar, or hides it when it has none now (or nothing to show yet).</summary>
    public void Place()
    {
        if (state is null) return;
        if (hwnd == IntPtr.Zero)
        {
            // Made at its first showing, out of sight, with what it shows: its size is its content's. (Made beforehand,
            // empty, Windows held it to its smallest window width, 136 px for a 40 px bell, October 31.) Owned by the
            // taskbar before it appears (OnSourceInitialized).
            Left = Top = -10000;
            Show();
        }
        bool placed = TryGetPlace(out int x, out int y, out bool autoHide);
        if (fullScreen || !placed)
        {
            autoHideTimer.Stop();
            if (IsVisible) Hide();
            if (!fullScreen && autoHide) autoHideTimer.Start(); // an auto-hidden taskbar away: look again in a moment
            LogPlace(fullScreen ? "hidden (full screen)" : autoHide ? "hidden (taskbar away)" : "hidden (no horizontal taskbar)");
            return;
        }
        if (!IsVisible) Show();
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        if (autoHide) autoHideTimer.Start();
        else autoHideTimer.Stop();
        LogPlace($"at {x},{y}; {placeDetail}");
    }

    // The developer log gets the place of the widget when it changes (it is looked at every 2 s).
    private string lastPlace = "";
    private string placeDetail = "";

    private void LogPlace(string place)
    {
        if (place == lastPlace) return;
        lastPlace = place;
        Log?.Invoke("Widget " + place);
    }

    private bool TryGetPlace(out int x, out int y, out bool autoHide)
    {
        x = y = 0;
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        autoHide = (SHAppBarMessage(ABM_GETSTATE, ref abd).ToInt64() & ABS_AUTOHIDE) != 0;

        IntPtr tray = Tray;
        if (tray == IntPtr.Zero || !IsWindow(tray) || !GetWindowRect(tray, out var bar) || bar.Width <= bar.Height)
            return false; // no taskbar, or a vertical one (Windows 10): not handled

        var monitor = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(tray, MONITOR_DEFAULTTOPRIMARY), ref monitor);
        // An auto-hidden taskbar keeps a line or two of itself on screen.
        int visible = Math.Min(bar.Bottom, monitor.rcMonitor.Bottom) - Math.Max(bar.Top, monitor.rcMonitor.Top);
        if (visible < bar.Height - 4) return false;

        double scale = GetDpiForWindow(tray) / 96.0;
        int width = (int)Math.Round(ActualWidth * scale), height = (int)Math.Round(ActualHeight * scale);
        int margin = (int)Math.Round(8 * scale);
        y = bar.Top + (bar.Height - height) / 2;
        var others = OtherWidgets(tray, bar, appProcessId);

        // After the weather of Windows 11 (its Widgets button) when it is there, and after the widgets of other apps laid
        // over the taskbar there (NowPlaying's: it lay over this one, October 31); Windows 10 and icons aligned left
        // (Start first) leave no room before them: near the clock. Or near the clock when Settings ask for it.
        bool nearClock = !WindowsTheme.IsWindows11 || state?.Position == WidgetPosition.NearClock;
        int half = bar.Left + bar.Width / 2;
        int at = bar.Left;
        RECT? Over(int left) => others.Where(o => o.Left < left + width && o.Right > left).Select(o => (RECT?)o).FirstOrDefault();
        if (!nearClock)
        {
            at = (weather is { } w && w.Right > bar.Left && w.Right < half ? w.Right : bar.Left) + margin;
            for (int i = 0; i < 10 && Over(at) is { } other; i++) at = other.Right + margin;
            if (startLeft is int start && at + width > start - (int)Math.Round(4 * scale)) nearClock = true;
        }
        if (nearClock)
        {
            // Left of the notification area (still a real window with its rectangle on Windows 11), and left of the widgets
            // of other apps there.
            IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            int right = notify != IntPtr.Zero && GetWindowRect(notify, out var area) && area.Width > 0 && area.Left > bar.Left
                ? area.Left
                : bar.Right - (int)Math.Round(220 * scale);
            // Icons aligned left: Windows puts its weather there, before the notification area (the author's question,
            // October 31): left of the weather.
            if (weather is { } w && w.Left > half && w.Left < right) right = w.Left;
            at = right - width - margin;
            for (int i = 0; i < 10 && Over(at) is { } other; i++) at = other.Left - width - margin;
        }
        x = Math.Max(bar.Left, at);
        placeDetail = $"taskbar {bar.Left},{bar.Top} {bar.Width}x{bar.Height}, scale {scale:0.##}, "
            + $"weather {(weather is { } shown ? $"{shown.Left}-{shown.Right}" : "none")}, "
            + $"Start at {startLeft?.ToString() ?? "?"}, {(nearClock ? "near the clock" : "on the left")}, widgets of other apps: "
            + (others.Count == 0 ? "none" : string.Join(" ", others.Select(o => $"[{o.Left}-{o.Right}]")));
        return true;
    }

    /// <summary>
    /// The windows of other apps laid over the taskbar, as this one (NowPlaying's widget…): visible, not hidden by
    /// Windows (cloaked), within the height of the taskbar, narrower than half of it. Explorer's own windows are not
    /// counted, nor LocalBell's (this process, the app).
    /// </summary>
    private static List<RECT> OtherWidgets(IntPtr tray, RECT bar, int appProcessId)
    {
        var list = new List<RECT>();
        uint own = (uint)Environment.ProcessId, app = (uint)appProcessId;
        GetWindowThreadProcessId(tray, out uint explorer);
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out uint process);
            if (process == own || process == app || process == explorer) return true;
            if (DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (GetWindowRect(window, out var r) && r.Top >= bar.Top - 2 && r.Bottom <= bar.Bottom + 2 && r.Height >= 16
                && r.Width >= 16 && r.Width < bar.Width / 2 && r.Left < bar.Right && r.Right > bar.Left)
                list.Add(r);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // The Widgets button (weather) and the Start button of the taskbar, in pixels: drawn by Explorer without windows of
    // their own, found through UI Automation, away from the UI thread (Explorer may be slow to answer). The weather is
    // after Start's group with the icons centred, near the clock with the icons aligned left.
    private (int Left, int Right)? weather;
    private int? startLeft;
    private bool lookingForButtons;
    private DateTime buttonsFoundAt;
    private string taskbarShape = "";

    /// <summary>What moves the buttons of the taskbar: its place, its notification area, the alignment of its icons.</summary>
    private static string TaskbarShape()
    {
        IntPtr tray = Tray;
        GetWindowRect(tray, out var bar);
        GetWindowRect(FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null), out var notify);
        object? alignment = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl", null);
        return $"{bar.Left},{bar.Top},{bar.Right},{bar.Bottom} {notify.Left},{notify.Right} {alignment}";
    }

    private void FindTaskbarButtons()
    {
        if (lookingForButtons) return;
        lookingForButtons = true;
        Task.Run(() =>
        {
            (int Left, int Right)? widgets = null, start = null;
            try
            {
                IntPtr tray = Tray;
                if (tray != IntPtr.Zero)
                {
                    var root = AutomationElement.FromHandle(tray);
                    widgets = Bounds(root, "WidgetsButton");
                    start = Bounds(root, "StartButton");
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException) { }
            Dispatcher.BeginInvoke(() =>
            {
                weather = widgets;
                startLeft = start?.Left;
                buttonsFoundAt = DateTime.UtcNow;
                lookingForButtons = false;
                Place();
            });
        });
    }

    private static (int Left, int Right)? Bounds(AutomationElement root, string automationId)
    {
        var element = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        if (element is null || element.Current.IsOffscreen) return null;
        var bounds = element.Current.BoundingRectangle;
        if (bounds.IsEmpty || bounds.Width <= 0) return null;
        return ((int)Math.Round(bounds.Left), (int)Math.Round(bounds.Right));
    }

    private void KeepOnTop()
    {
        if (!IsVisible) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        // The window in front may still be settling: again when the UI is idle.
        Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible) SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }, DispatcherPriority.ApplicationIdle);
    }

    private IntPtr WndProc(IntPtr window, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);

            case AppBarMessage:
                if (wParam.ToInt32() == ABN_FULLSCREENAPP)
                {
                    fullScreen = lParam != IntPtr.Zero;
                    Place();
                }
                else if (wParam.ToInt32() == ABN_POSCHANGED)
                    Dispatcher.BeginInvoke(Place, DispatcherPriority.Background);
                break;

            case WM_DISPLAYCHANGE:
            case WM_SETTINGCHANGE:
                // Resolution, scale, taskbar settings (alignment, auto-hide), light / dark.
                Dispatcher.BeginInvoke(() =>
                {
                    KeepTransparent();
                    ApplyColors();
                    Place();
                    FindTaskbarButtons();
                    buttonsAgainTimer.Stop();
                    buttonsAgainTimer.Start();
                }, DispatcherPriority.Background);
                break;
        }
        return IntPtr.Zero;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(Place, DispatcherPriority.Background);
    }

    protected override void OnClosed(EventArgs e)
    {
        autoHideTimer.Stop();
        othersTimer.Stop();
        buttonsAgainTimer.Stop();
        if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
        if (appBar)
        {
            var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };
            SHAppBarMessage(ABM_REMOVE, ref data);
        }
        base.OnClosed(e);
    }
}
