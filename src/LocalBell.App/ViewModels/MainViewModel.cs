using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using LocalBell.App.Services;
using LocalBell.Core;
using LocalBell.Core.Peers;
using LocalBell.Core.Protocol;

namespace LocalBell.App.ViewModels;

/// <summary>A ring received in "do not disturb", as the flyout of the widget lists it.</summary>
public sealed record MissedRing(IncomingRing Ring, string Text);

/// <summary>A ready-made message under the list; chosen, it fills the message box.</summary>
public sealed class ChipItem(string text) : ObservableObject
{
    private bool isSelected;

    public string Text { get; } = text;

    public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
}

/// <summary>
/// The list window and what lives behind it while the window is hidden: the PCs, ringing them, the rings received,
/// "do not disturb". The node's events come from the thread pool and are brought to the UI thread here.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings settings;
    private readonly BellNode node;
    private readonly RingPopups popups;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer saveTimer;
    private readonly DispatcherTimer dndTimer;
    private string message = "";

    public MainViewModel(AppSettings settings, BellNode node, RingPopups popups)
    {
        this.settings = settings;
        this.node = node;
        this.popups = popups;
        dispatcher = Dispatcher.CurrentDispatcher;

        foreach (string text in settings.Messages) Chips.Add(new ChipItem(text));
        RingCommand = new RelayCommand(p => { if (p is PeerItem item) _ = RingAsync(item); });
        RingEveryoneCommand = new RelayCommand(OnRingEveryoneClick);
        FavoriteCommand = new RelayCommand(p => { if (p is PeerItem item) ToggleFavorite(item); });
        ForgetCommand = new RelayCommand(p => { if (p is PeerItem { CanForget: true } item) Forget(item); });
        RingBackCommand = new RelayCommand(p => { if (p is MissedRing missed) RingBack(missed); });
        ChipCommand = new RelayCommand(p => { if (p is ChipItem chip) Message = chip.IsSelected ? "" : chip.Text; });
        DoNotDisturbOffCommand = new RelayCommand(TurnOffDoNotDisturb);

        // The PCs heard from are kept, to list them offline at the next start; written at most every 2 s.
        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            settings.Save();
        };
        dndTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        dndTimer.Tick += (_, _) => { if (settings.DoNotDisturbUntil is not null && !settings.IsDoNotDisturb) TurnOffDoNotDisturb(); };
        dndTimer.Start();

        node.PeersChanged += (_, _) => dispatcher.BeginInvoke(RefreshPeers);
        node.RingReceived += (_, ring) => dispatcher.BeginInvoke(() => OnRing(ring));
        node.ReplyReceived += (_, reply) => dispatcher.BeginInvoke(() => OnReply(reply));
        RefreshPeers();
    }

    public ObservableCollection<PeerItem> Peers { get; } = [];

    public ObservableCollection<ChipItem> Chips { get; } = [];

    /// <summary>
    /// The rings received in "do not disturb", counted by the badge of the widget and listed in its flyout ("Atelier
    /// vous a sonné · 14:20 · Rappeler") until the flyout was seen.
    /// </summary>
    public ObservableCollection<MissedRing> MissedRings { get; } = [];

    public RelayCommand RingBackCommand { get; private set; } = null!;

    /// <summary>The favourite PC (its star), rung in one click from the widget.</summary>
    public PeerItem? Favorite => Peers.FirstOrDefault(p => p.IsFavorite);

    /// <summary>Rings back the PC of a missed ring, and forgets that ring.</summary>
    private void RingBack(MissedRing missed)
    {
        MissedRings.Remove(missed);
        OnPropertyChanged(nameof(DoNotDisturbDetail));
        if (Peers.FirstOrDefault(p => p.Id == missed.Ring.From.Id) is { } row) _ = RingAsync(row);
    }

    /// <summary>The missed rings were seen (the flyout of the widget closed).</summary>
    public void ClearMissed()
    {
        MissedRings.Clear();
        OnPropertyChanged(nameof(DoNotDisturbDetail));
    }

    public ToastViewModel Toast { get; } = new();

    public RelayCommand RingCommand { get; }
    public RelayCommand RingEveryoneCommand { get; }
    public RelayCommand FavoriteCommand { get; }

    public RelayCommand ForgetCommand { get; }
    public RelayCommand ChipCommand { get; }
    public RelayCommand DoNotDisturbOffCommand { get; }

    public string ThisPcText => Strings.Format("ThisPc", node.Name);

    /// <summary>Renames this PC (Settings → General): saved, and told to the others.</summary>
    public void Rename(string name)
    {
        node.Rename(name);
        settings.Name = node.Name;
        settings.Save();
        OnPropertyChanged(nameof(ThisPcText));
        NameChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when this PC was renamed (the bell near the clock shows the name).</summary>
    public event EventHandler? NameChanged;

    /// <summary>The ready-made messages after a change in Settings.</summary>
    public void ReloadMessages()
    {
        Chips.Clear();
        foreach (string text in settings.Messages) Chips.Add(new ChipItem(text) { IsSelected = text == Message });
    }

    /// <summary>Every text again, in the language just chosen.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(null);
        foreach (var peer in Peers) peer.Refresh();
    }

    /// <summary>No other PC known yet: the list shows that it is looking.</summary>
    public bool IsSearching => Peers.Count == 0;

    public string Message
    {
        get => message;
        set
        {
            if (!Set(ref message, value)) return;
            foreach (var chip in Chips) chip.IsSelected = chip.Text == value;
        }
    }

    // ---- The list ----

    /// <summary>Brings the rows in step with the node, in place, so that a row being clicked does not jump away.</summary>
    private void RefreshPeers()
    {
        var peers = node.Peers;
        for (int i = 0; i < peers.Count; i++)
        {
            var peer = peers[i];
            int at = IndexOf(peer.Id);
            if (at < 0)
            {
                Peers.Insert(i, new PeerItem(peer, peer.Id == settings.Favorite));
                continue;
            }
            Peers[at].Update(peer);
            if (at != i) Peers.Move(at, i);
        }
        while (Peers.Count > peers.Count) Peers.RemoveAt(Peers.Count - 1);
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(Favorite));

        // Only the PCs that answered once (or were known before): a name in one datagram is not kept for the next run.
        settings.KnownPcs = peers.Where(p => p.Verified).Select(p => new KnownPc
        {
            Id = p.Id, Name = p.Name, Address = p.EndPoint.Address.ToString(), Port = p.EndPoint.Port,
        }).ToList();
        saveTimer.Stop();
        saveTimer.Start();
    }

    private int IndexOf(string id)
    {
        for (int i = 0; i < Peers.Count; i++)
            if (Peers[i].Id == id) return i;
        return -1;
    }

    private void ToggleFavorite(PeerItem item)
    {
        settings.Favorite = settings.Favorite == item.Id ? null : item.Id;
        foreach (var row in Peers) row.IsFavorite = row.Id == settings.Favorite;
        settings.Save();
        OnPropertyChanged(nameof(Favorite));
    }

    /// <summary>
    /// "Oublier ce poste" (a right click on its row; the author, November 3): a PC that is no more leaves the list and
    /// the PCs kept for the next start (the node says its list changed: <see cref="RefreshPeers"/>), and is no longer the
    /// favourite. It comes back if it is ever heard from again.
    /// </summary>
    private void Forget(PeerItem item)
    {
        if (settings.Favorite == item.Id) settings.Favorite = null;
        node.Forget(item.Id);
    }

    // ---- Ringing ----

    // How long the row of a PC rung says it. "Ça sonne…" lasts until the PC rung says it no longer rings (a reply, its
    // popup closed, no answer): the two minutes are only for a PC that never says it. Its reply stays long: the caller
    // may look away.
    private static readonly TimeSpan RingingNote = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ResultNote = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan EndNote = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ReplyNote = TimeSpan.FromMinutes(2);

    /// <summary>Rings a PC; its row says "Ça sonne…", then what happened (the author's choice: no notice over the list).</summary>
    public async Task RingAsync(PeerItem item)
    {
        string text = Message;
        item.ShowNote(NoteKind.Ringing, RingingText(text), RingingNote);
        var outcome = await node.RingAsync(item.Peer, text);
        item.LastRingId = outcome.RingId;
        ShowOutcome(item, outcome.Result, text);
        if (outcome.Result != RingResult.Failed) Message = "";
    }

    private static string RingingText(string message) =>
        message.Length > 0 ? $"{Strings.Get("NoteRinging")} · {message}" : Strings.Get("NoteRinging");

    private static void ShowOutcome(PeerItem item, RingResult result, string message)
    {
        switch (result)
        {
            case RingResult.Quiet:
                item.ShowNote(NoteKind.Quiet, Strings.Get("NoteQuiet"), ResultNote);
                break;
            case RingResult.Failed:
                item.ShowNote(NoteKind.Failed, Strings.Get("NoteFailed"), ResultNote);
                break;
            default:
                // A reply or the end of the ring may already have come while the request was on its way.
                if (item.NoteKind == NoteKind.Ringing) item.ShowNote(NoteKind.Ringing, RingingText(message), RingingNote);
                break;
        }
    }

    // ---- "Sonner tout le monde", in two clicks ----

    private DispatcherTimer? confirmTimer;
    private bool isConfirmingEveryone;

    /// <summary>The first click asks for a second one, for 3 s (the author's choice A, October 31).</summary>
    public bool IsConfirmingEveryone
    {
        get => isConfirmingEveryone;
        private set => Set(ref isConfirmingEveryone, value);
    }

    public string ConfirmEveryoneText => Strings.Count("ConfirmEveryone", Peers.Count(p => p.Online));

    private void OnRingEveryoneClick()
    {
        if (IsConfirmingEveryone)
        {
            confirmTimer?.Stop();
            IsConfirmingEveryone = false;
            _ = RingEveryoneAsync();
            return;
        }
        if (!Peers.Any(p => p.Online))
        {
            Toast.Show(Strings.Get("NobodyOnline"), "", "");
            return;
        }
        OnPropertyChanged(nameof(ConfirmEveryoneText));
        if (confirmTimer is null)
        {
            confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            confirmTimer.Tick += (_, _) =>
            {
                confirmTimer.Stop();
                IsConfirmingEveryone = false;
            };
        }
        IsConfirmingEveryone = true;
        confirmTimer.Start();
    }

    private async Task RingEveryoneAsync()
    {
        string text = Message;
        var rows = Peers.Where(p => p.Online).ToList();
        foreach (var row in rows) row.ShowNote(NoteKind.Ringing, RingingText(text), RingingNote);
        var outcomes = await node.RingEveryoneAsync(text);
        foreach (var outcome in outcomes)
        {
            if (Peers.FirstOrDefault(p => p.Id == outcome.Peer.Id) is not { } row) continue;
            row.LastRingId = outcome.RingId;
            ShowOutcome(row, outcome.Result, text);
        }
        if (outcomes.Any(o => o.Result != RingResult.Failed)) Message = "";
    }

    /// <summary>
    /// What a PC rung says back shows on its row, with its time: its reply in green, or, grey, that its popup was closed
    /// or nobody answered (about its last ring only).
    /// </summary>
    private void OnReply(IncomingReply reply)
    {
        string time = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
        var row = Peers.FirstOrDefault(p => p.Id == reply.From.Id);
        if (reply.Kind == ReplyKind.Text)
        {
            if (row is not null) row.ShowNote(NoteKind.Replied, Strings.Format("NoteReply", reply.Text, time), ReplyNote);
            else Toast.Show(Strings.Format("ReplyTitle", reply.From.Name, reply.Text), "", "");
            return;
        }
        if (row is null || (row.LastRingId is { } last && last != reply.RingId) || row.NoteKind == NoteKind.Replied) return;
        if (reply.Kind == ReplyKind.Closed) row.ShowNote(NoteKind.Closed, Strings.Format("NoteClosed", time), EndNote);
        else row.ShowNote(NoteKind.Unanswered, Strings.Format("NoteUnanswered", time), EndNote);
    }

    /// <summary>A ring received: a popup and the bell, or, in "do not disturb", a missed ring.</summary>
    private void OnRing(IncomingRing ring)
    {
        if (ring.Quiet)
        {
            MissedRings.Insert(0, new MissedRing(ring,
                Strings.Format("MissedLine", ring.From.Name, ring.At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))));
            OnPropertyChanged(nameof(DoNotDisturbDetail));
            return;
        }
        popups.Show(ring, text => _ = node.ReplyAsync(ring, text), kind => _ = node.ReplyAsync(ring, "", kind));
    }

    // ---- "Do not disturb" ----

    public bool IsDoNotDisturb => settings.IsDoNotDisturb;

    public string DoNotDisturbTitle
    {
        get
        {
            if (settings.DoNotDisturbUntil is not { } until || until == DateTimeOffset.MaxValue) return Strings.Get("StatusDnd");
            return until.Date > DateTimeOffset.Now.Date
                ? Strings.Get("DndUntilTomorrow")
                : Strings.Format("DndUntil", until.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
        }
    }

    public string DoNotDisturbDetail => MissedRings.Count == 0 ? Strings.Get("DndNoMissed") : Strings.Count("DndMissed", MissedRings.Count);

    /// <summary>"30", "60", "tomorrow" (6 am) or "forever", as the menus offer it.</summary>
    public void TurnOnDoNotDisturb(string length)
    {
        var now = DateTimeOffset.Now;
        settings.DoNotDisturbUntil = length switch
        {
            "30" => now.AddMinutes(30),
            "60" => now.AddHours(1),
            "tomorrow" => new DateTimeOffset(now.Date.AddDays(1).AddHours(6), now.Offset),
            _ => DateTimeOffset.MaxValue,
        };
        settings.Save();
        node.SetStatus(PeerStatus.DoNotDisturb);
        popups.Silence(); // turned on while it rings (the audit, October 31)
        RaiseDoNotDisturb();
    }

    public void TurnOffDoNotDisturb()
    {
        settings.DoNotDisturbUntil = null;
        settings.Save();
        node.SetStatus(PeerStatus.Available);
        // The missed rings stay for the widget (its badge, its flyout and "Rappeler"); told here once.
        if (MissedRings.Count > 0)
            Toast.Show(Strings.Count("DndMissed", MissedRings.Count),
                string.Join(", ", MissedRings.Select(r => r.Ring.From.Name).Distinct()), "");
        RaiseDoNotDisturb();
    }

    /// <summary>Raised when "do not disturb" changes (the bell near the clock follows it).</summary>
    public event EventHandler? DoNotDisturbChanged;

    private void RaiseDoNotDisturb()
    {
        OnPropertyChanged(nameof(IsDoNotDisturb));
        OnPropertyChanged(nameof(DoNotDisturbTitle));
        OnPropertyChanged(nameof(DoNotDisturbDetail));
        DoNotDisturbChanged?.Invoke(this, EventArgs.Empty);
    }
}
