using System.Globalization;
using System.Windows;
using LocalBell.App.Services;
using LocalBell.Core;
using LocalBell.Core.Protocol;

namespace LocalBell.App;

/// <summary>
/// The popup of a ring, in a corner of the screen. It never takes the keyboard from the app in use (ShowActivated
/// False), and stays above the other windows. The PC that rang is told what becomes of its ring: a reply, the popup
/// closed without one, or no answer when the bell stops; so that its row never says "it rings" when it no longer does.
/// </summary>
public partial class RingPopup : Window
{
    private readonly Action<ReplyKind> ended;
    private bool replied;
    private bool toldUnanswered;

    public RingPopup(IncomingRing ring, PopupStyle style, IReadOnlyList<string> replies, Action<string> reply, Action<ReplyKind> ended)
    {
        InitializeComponent();
        Ring = ring;
        this.ended = ended;
        Card.Show(style, ring.From.Name, ring.Message, ring.Everyone,
            ring.At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture), replies);
        Card.Replied += text =>
        {
            replied = true;
            reply(text);
            Close();
        };
        Card.Closed += () =>
        {
            if (!replied) ended(ReplyKind.Closed);
            Close();
        };
    }

    public IncomingRing Ring { get; }

    /// <summary>The bell stopped, or the popup closes by itself, and nobody answered: said once.</summary>
    public void TellUnanswered()
    {
        if (replied || toldUnanswered) return;
        toldUnanswered = true;
        ended(ReplyKind.Unanswered);
    }
}
