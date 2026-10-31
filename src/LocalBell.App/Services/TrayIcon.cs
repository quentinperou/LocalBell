using System.Windows;
using Forms = System.Windows.Forms;

namespace LocalBell.App.Services;

/// <summary>
/// The bell near the clock: a click does what Settings → General says (the quick panel, or the window), a right click
/// opens the menu (<see cref="PopupMenu"/>).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon;

    public TrayIcon(Action click, Func<IEnumerable<object>> menuItems)
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/LocalBell.ico"))!.Stream;
        icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "LocalBell",
            Visible = true,
        };
        icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) click();
            else if (e.Button == Forms.MouseButtons.Right) PopupMenu.Show(menuItems());
        };
    }

    /// <summary>The text shown when the mouse rests on the bell (Windows keeps 127 characters).</summary>
    public string Tooltip
    {
        set => icon.Text = value.Length > 127 ? value[..127] : value;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
