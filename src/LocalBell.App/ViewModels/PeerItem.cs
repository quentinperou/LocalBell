using System.Windows.Threading;
using LocalBell.Core.Peers;
using LocalBell.Core.Protocol;

namespace LocalBell.App.ViewModels;

/// <summary>What the row of a PC says for a while after it was rung (the author chose it on the row, October 31).</summary>
public enum NoteKind
{
    None,

    /// <summary>"Ça sonne… · the message", in the accent colour.</summary>
    Ringing,

    /// <summary>In "do not disturb": the ring waits in its missed rings.</summary>
    Quiet,

    /// <summary>It could not be reached.</summary>
    Failed,

    /// <summary>Its reply ("J'arrive · 13:53"), in green.</summary>
    Replied,

    /// <summary>Its popup was closed without a reply ("Vu, fermé sans réponse").</summary>
    Closed,

    /// <summary>Its bell stopped and nobody answered ("Pas de réponse").</summary>
    Unanswered,
}

/// <summary>A row of the list: another PC.</summary>
public sealed class PeerItem : ObservableObject
{
    private Peer peer;
    private bool isFavorite;
    private NoteKind noteKind;
    private string note = "";
    private DispatcherTimer? noteTimer;

    public PeerItem(Peer peer, bool isFavorite)
    {
        this.peer = peer;
        this.isFavorite = isFavorite;
    }

    public Peer Peer => peer;

    public string Id => peer.Id;

    public string Name => peer.Name;

    public bool Online => peer.Online;

    /// <summary>Online and not in "do not disturb": its Ring button is the accent one.</summary>
    public bool IsAvailable => peer.Online && peer.Status == PeerStatus.Available;

    public bool IsDoNotDisturb => peer.Online && peer.Status == PeerStatus.DoNotDisturb;

    public string StatusText => Strings.Get(!peer.Online ? "StatusOffline" : IsDoNotDisturb ? "StatusDnd" : "StatusOnline");

    /// <summary>The name of its Ring button, for UI Automation and screen readers ("Sonner Atelier").</summary>
    public string RingLabel => Strings.Format("RingName", peer.Name);

    public bool IsFavorite
    {
        get => isFavorite;
        set => Set(ref isFavorite, value);
    }

    public NoteKind NoteKind
    {
        get => noteKind;
        private set
        {
            if (Set(ref noteKind, value)) OnPropertyChanged(nameof(HasNote));
        }
    }

    public string Note
    {
        get => note;
        private set => Set(ref note, value);
    }

    public bool HasNote => noteKind != NoteKind.None;

    /// <summary>The last ring sent to it: what it says back about an older one does not replace the newer note.</summary>
    public string? LastRingId { get; set; }

    /// <summary>Shows a note on the row in place of its status, for a while.</summary>
    public void ShowNote(NoteKind kind, string text, TimeSpan duration)
    {
        Note = text;
        NoteKind = kind;
        if (noteTimer is null)
        {
            noteTimer = new DispatcherTimer();
            noteTimer.Tick += (_, _) =>
            {
                noteTimer.Stop();
                NoteKind = NoteKind.None;
            };
        }
        noteTimer.Stop();
        noteTimer.Interval = duration;
        noteTimer.Start();
    }

    public void Refresh() => OnPropertyChanged(null);

    public void Update(Peer newPeer)
    {
        if (newPeer == peer) return;
        peer = newPeer;
        OnPropertyChanged(nameof(Peer));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Online));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsDoNotDisturb));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(RingLabel));
    }
}
