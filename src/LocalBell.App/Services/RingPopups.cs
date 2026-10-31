using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LocalBell.Core;
using LocalBell.Core.Protocol;
using Forms = System.Windows.Forms;

namespace LocalBell.App.Services;

/// <summary>
/// The popups of the rings received, piled up in the corner chosen in Settings, and the bell: it rings again
/// (<see cref="AppSettings.Repeat"/> times in all) while a popup waits for an answer. While it rings, the PC's sound is
/// turned back on if the user asked for it (<see cref="WindowsVolume"/>).
/// </summary>
public sealed class RingPopups
{
    private const double Gap = -16; // the popups have a 16 px margin for their shadow: they overlap it

    private readonly AppSettings settings;
    private readonly List<RingPopup> open = [];
    private readonly DispatcherTimer bell = new();
    private int ringsLeft;
    private WindowsVolume? audible;

    public RingPopups(AppSettings settings)
    {
        this.settings = settings;
        bell.Tick += (_, _) => RingAgain();
    }

    /// <summary>
    /// Shows the popup of a ring and rings. <paramref name="reply"/>: a reply button; <paramref name="ended"/>: what the
    /// PC that rang is told when there is none (closed, or no answer when the bell stops).
    /// </summary>
    public void Show(IncomingRing ring, Action<string> reply, Action<ReplyKind> ended)
    {
        var popup = new RingPopup(ring, settings.PopupStyle, settings.Replies.Take(3).ToList(), reply, ended);
        popup.Closed += (_, _) =>
        {
            open.Remove(popup);
            Arrange();
            if (open.Count == 0) StopBell();
        };
        open.Add(popup);

        // Out of the screen first: its size is known once shown, then it takes its place in the pile.
        popup.Left = -10000;
        popup.Top = -10000;
        popup.Show();
        popup.UpdateLayout();
        Arrange();
        DevLog.Write($"Popup of {ring.From.Name}: {settings.PopupStyle}, {settings.Corner}, at {popup.Left:0},{popup.Top:0}"
            + $", {open.Count} open; bell {settings.Sound}, volume {settings.Volume}, {settings.Repeat} time(s)");

        if (settings.CloseAfterSeconds > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(settings.CloseAfterSeconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                popup.TellUnanswered();
                popup.Close();
            };
            timer.Start();
        }

        if (settings.RingWhenMuted && audible is null)
        {
            audible = WindowsVolume.MakeAudible();
            if (audible is not null) DevLog.Write("The sound of the PC was off or low: turned back on while it rings");
        }
        BellSound.Play(settings.Sound, settings.SoundFile, settings.Volume);
        ringsLeft = settings.Repeat - 1;
        bell.Interval = BellSound.Length(settings.Sound) + TimeSpan.FromSeconds(1);
        bell.Stop();
        bell.Start();
    }

    /// <summary>
    /// Rings again while a popup waits; after the last ring, its sound is given time to end, and the PCs that rang are
    /// told that nobody answered (a reply may still come from a popup left open).
    /// </summary>
    private void RingAgain()
    {
        if (open.Count == 0 || ringsLeft <= 0)
        {
            foreach (var popup in open) popup.TellUnanswered();
            StopBell();
            return;
        }
        ringsLeft--;
        BellSound.Play(settings.Sound, settings.SoundFile, settings.Volume);
    }

    /// <summary>Every popup answered or closed, or the last ring over: silence, and the PC's sound as it was.</summary>
    private void StopBell()
    {
        bell.Stop();
        ringsLeft = 0;
        BellSound.Stop();
        audible?.Restore();
        audible = null;
    }

    /// <summary>The newest popup in the corner, the older ones next to it towards the middle of the screen.</summary>
    private void Arrange()
    {
        if (open.Count == 0) return;
        var area = WorkArea(open[^1]);
        bool bottom = settings.Corner is ScreenCorner.BottomRight or ScreenCorner.BottomLeft;
        bool right = settings.Corner is ScreenCorner.BottomRight or ScreenCorner.TopRight;
        double offset = 0;
        for (int i = open.Count - 1; i >= 0; i--)
        {
            var popup = open[i];
            double width = popup.ActualWidth, height = popup.ActualHeight;
            popup.Left = right ? area.Right - width : area.Left;
            popup.Top = bottom ? area.Bottom - offset - height : area.Top + offset;
            offset += height + Gap;
        }
    }

    /// <summary>The work area (without the taskbar) of the main screen, or of the screen of the mouse, in WPF units.</summary>
    private Rect WorkArea(Window popup)
    {
        if (!settings.PopupOnMouseScreen) return SystemParameters.WorkArea;
        var pixels = Forms.Screen.FromPoint(Forms.Control.MousePosition).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(popup);
        return new Rect(pixels.Left / dpi.DpiScaleX, pixels.Top / dpi.DpiScaleY, pixels.Width / dpi.DpiScaleX, pixels.Height / dpi.DpiScaleY);
    }
}
