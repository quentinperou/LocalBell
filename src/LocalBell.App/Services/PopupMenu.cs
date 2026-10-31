using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace LocalBell.App.Services;

/// <summary>
/// The menus of the bell near the clock and of the taskbar widget, drawn as Windows' own (Menus.xaml) in Windows' mode,
/// as NowPlaying's. They are built each time they open, so that they show the current state and language.
/// </summary>
internal static class PopupMenu
{
    // The margin of the menu around its border, for its shadow (Menus.xaml).
    private const int ShadowRoom = 12;

    private static ContextMenu? open;

    /// <summary>
    /// Opens a menu of <paramref name="items"/> at the pointer, or above the taskbar when <paramref name="aboveY"/> (its
    /// top, in screen pixels) is given: never over it.
    /// </summary>
    public static void Show(IEnumerable<object> items, int? aboveY = null)
    {
        if (open is { IsOpen: true })
        {
            open.IsOpen = false;
            return;
        }

        // A WPF menu of an app with no window in front loses the mouse capture as it opens, and closes at once. So the
        // menu belongs to a tiny invisible window under the pointer, activated first (a click on the icon or the widget
        // gives the app the right to come to the front), and closed with the menu.
        var host = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = 1,
            Height = 1,
            Title = "LocalBell Menu",
        };
        host.SourceInitialized += (_, _) =>
        {
            NativeMethods.GetCursorPos(out var pointer);
            NativeMethods.SetWindowPos(new WindowInteropHelper(host).Handle, NativeMethods.HWND_TOPMOST,
                pointer.X, aboveY ?? pointer.Y, 1, 1, NativeMethods.SWP_NOACTIVATE);
        };
        host.Show();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(host).Handle);
        host.Activate();

        var menu = new ContextMenu { MinWidth = 240 };
        foreach (var item in items) menu.Items.Add(item);
        Dress(menu);
        menu.PlacementTarget = host;
        menu.Placement = aboveY is null ? PlacementMode.MousePoint : PlacementMode.Top;
        menu.HorizontalOffset = -ShadowRoom;
        menu.VerticalOffset = aboveY is null ? -ShadowRoom : 0;
        // Not the popup itself: activating it would deactivate the host, which closes the menu.
        menu.Closed += (_, _) =>
        {
            open = null;
            host.Close();
        };
        open = menu;
        menu.IsOpen = true;
    }

    /// <summary>An item with a glyph of Segoe Fluent Icons (or none) and what it does.</summary>
    public static MenuItem Item(string header, string glyph, Action? action, bool bold = false)
    {
        var item = new MenuItem { Header = header, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        if (glyph.Length > 0)
            item.Icon = new TextBlock
            {
                Text = glyph,
                FontSize = 15,
                FontWeight = FontWeights.Normal,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            };
        if (action is not null) item.Click += (_, _) => action();
        return item;
    }

    /// <summary>An item with the counter bell of the app as its icon, in the colour of the menu's text.</summary>
    public static MenuItem BellItem(string header, Action action, bool bold = false)
    {
        var item = Item(header, "", action, bold);
        var bell = new System.Windows.Shapes.Path
        {
            Data = (Geometry)Application.Current.FindResource("BellGeometry"),
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
        };
        bell.SetBinding(System.Windows.Shapes.Shape.FillProperty,
            new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(MenuItem), 1) });
        item.Icon = bell;
        return item;
    }

    // The look of Windows 11's menus, in Windows' mode (that of the taskbar), whatever the theme of the app windows.
    private static void Dress(ContextMenu menu)
    {
        menu.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/LocalBell;component/Menus.xaml", UriKind.Relative)));
        menu.Style = (Style)menu.Resources["WindowsMenu"];
        bool dark = WindowsTheme.TaskbarIsDark;
        (string Key, string Light, string Dark)[] colors =
        [
            ("MenuSurface", "#F9F9F9", "#2C2C2C"),
            ("MenuStroke", "#E3E3E3", "#424242"),
            ("MenuText", "#1B1B1B", "#FFFFFF"),
            ("MenuTextDisabled", "#A0A0A0", "#7A7A7A"),
            ("MenuHover", "#EDEDED", "#383838"),
            ("MenuDivider", "#E5E5E5", "#404040"),
        ];
        foreach (var (key, light, darkColor) in colors)
            menu.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkColor : light));
    }
}
