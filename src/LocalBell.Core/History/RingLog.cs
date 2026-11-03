using System.Text.Json;
using LocalBell.Core.Protocol;

namespace LocalBell.Core.History;

/// <summary>What became of a ring, as Settings → History tells it.</summary>
public enum RingFate
{
    /// <summary>It rings still (or did when LocalBell stopped: see <see cref="RingLog.Effective"/>).</summary>
    Ringing,

    /// <summary>A reply button ("J'arrive"…): <see cref="LoggedParty.Reply"/>.</summary>
    Replied,

    /// <summary>The popup was closed without a reply.</summary>
    Closed,

    /// <summary>The bell stopped and nobody answered.</summary>
    Unanswered,

    /// <summary>Received in "do not disturb": no sound, no popup.</summary>
    Missed,

    /// <summary>Sent to a PC in "do not disturb".</summary>
    Quiet,

    /// <summary>Sent to a PC that could not be reached.</summary>
    Failed,
}

/// <summary>The PC that rang, or one of the PCs rung (several for "everyone"), and what became of its ring.</summary>
public sealed class LoggedParty
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RingId { get; set; } = "";
    public RingFate Fate { get; set; }
    public string? Reply { get; set; }
    public DateTimeOffset? RepliedAt { get; set; }
}

/// <summary>A ring received or sent.</summary>
public sealed class LoggedRing
{
    public DateTimeOffset At { get; set; }
    public bool Received { get; set; }

    /// <summary>Received: the PC rang everyone. Sent: this PC rang everyone (<see cref="Parties"/>: each PC rung).</summary>
    public bool Everyone { get; set; }

    public string Message { get; set; } = "";
    public List<LoggedParty> Parties { get; set; } = [];
}

/// <summary>
/// The rings received and sent (Settings → History; the author, November 3, 2026), the newest first, kept
/// <see cref="KeepDays"/> days in <c>history.json</c> beside the settings: on this PC only, never sent anywhere. Used
/// from the UI thread only.
/// </summary>
public sealed class RingLog
{
    /// <summary>A ring still "ringing" that long after it was sent is one whose end was never told (LocalBell stopped).</summary>
    public static readonly TimeSpan RingingFor = TimeSpan.FromMinutes(2);

    public const int MaxRings = 500;

    // A reply may come before the ring it answers is logged: the request was still on its way (or the others of
    // "everyone"). Kept a while for it.
    private static readonly TimeSpan PendingFor = TimeSpan.FromMinutes(1);

    private readonly string? path;
    private readonly TimeProvider time;
    private readonly List<LoggedRing> rings = [];
    private readonly List<(IncomingReply Reply, DateTimeOffset At)> pending = [];
    private int keepDays = 7;

    /// <param name="path">The file it is read from and saved to; null: in memory only.</param>
    public RingLog(string? path = null, TimeProvider? time = null)
    {
        this.path = path;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>The log of the file, or an empty one when there is none or it cannot be read (it is no great loss).</summary>
    public static RingLog Load(string path, TimeProvider? time = null)
    {
        var log = new RingLog(path, time);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<LoggedRing>>(File.ReadAllBytes(path), BellProtocol.Json) is { } saved)
                log.rings.AddRange(saved.Where(r => r?.Parties is { Count: > 0 }).OrderByDescending(r => r.At));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return log;
    }

    /// <summary>Raised when a ring is logged or what became of one changes.</summary>
    public event EventHandler? Changed;

    /// <summary>1: today only; else that many days, today included.</summary>
    public int KeepDays
    {
        get => keepDays;
        set
        {
            keepDays = Math.Max(1, value);
            if (Trim()) Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The rings of the days kept, the newest first.</summary>
    public IReadOnlyList<LoggedRing> Rings
    {
        get
        {
            Trim();
            return rings.ToList();
        }
    }

    /// <summary>A ring received: ringing, or missed in "do not disturb".</summary>
    public void AddReceived(IncomingRing ring)
    {
        Add(new LoggedRing
        {
            At = ring.At,
            Received = true,
            Everyone = ring.Everyone,
            Message = ring.Message,
            Parties = [new LoggedParty { Id = ring.From.Id, Name = ring.From.Name, RingId = ring.Id, Fate = ring.Quiet ? RingFate.Missed : RingFate.Ringing }],
        });
    }

    /// <summary>This PC's answer to a ring received: a reply, its popup closed, or no answer.</summary>
    public void Answer(string ringId, RingFate fate, string? reply = null)
    {
        if (Find(ringId, received: true) is not { } party) return;
        if (Apply(party, fate, reply)) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A ring sent, to one PC or to everyone: what each PC said at once (it rings, quiet, unreachable).</summary>
    public void AddSent(string message, bool everyone, IReadOnlyList<RingOutcome> outcomes)
    {
        if (outcomes.Count == 0) return;
        var ring = new LoggedRing
        {
            At = time.GetLocalNow(),
            Everyone = everyone,
            Message = message,
            Parties = outcomes.Select(o => new LoggedParty
            {
                Id = o.Peer.Id,
                Name = o.Peer.Name,
                RingId = o.RingId,
                Fate = o.Result switch { RingResult.Quiet => RingFate.Quiet, RingResult.Failed => RingFate.Failed, _ => RingFate.Ringing },
            }).ToList(),
        };
        // The replies that came first.
        foreach (var party in ring.Parties)
            foreach (var early in pending.Where(p => p.Reply.RingId == party.RingId).ToList())
            {
                Apply(party, Fate(early.Reply.Kind), early.Reply.Text, early.At);
                pending.Remove(early);
            }
        Add(ring);
    }

    /// <summary>What a PC rung by this one says back.</summary>
    public void AddReply(IncomingReply reply)
    {
        var now = time.GetLocalNow();
        if (Find(reply.RingId, received: false) is { } party)
        {
            if (Apply(party, Fate(reply.Kind), reply.Text, now)) Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        pending.RemoveAll(p => now - p.At > PendingFor);
        pending.Add((reply, now));
    }

    public void Clear()
    {
        if (rings.Count == 0) return;
        rings.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// What became of a ring as it should read now: one still ringing long after it was sent had its end never told
    /// (LocalBell stopped, or a newer ring of the same PC took the place of its popup), and nobody answered it.
    /// </summary>
    public RingFate Effective(LoggedRing ring, LoggedParty party) =>
        party.Fate == RingFate.Ringing && time.GetLocalNow() - ring.At > RingingFor ? RingFate.Unanswered : party.Fate;

    /// <summary>Writes the file whole or not at all (a temporary file beside it, then swapped).</summary>
    public void Save()
    {
        if (path is null) return;
        Trim();
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(rings, BellProtocol.Json));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Add(LoggedRing ring)
    {
        rings.Insert(0, ring);
        if (rings.Count > MaxRings) rings.RemoveRange(MaxRings, rings.Count - MaxRings);
        Trim();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // The rings of the days before the first day kept go. True when some went.
    private bool Trim()
    {
        var first = time.GetLocalNow().Date.AddDays(1 - keepDays);
        return rings.RemoveAll(r => r.At.ToLocalTime().Date < first) > 0;
    }

    private LoggedParty? Find(string ringId, bool received) =>
        rings.Where(r => r.Received == received).SelectMany(r => r.Parties).FirstOrDefault(p => p.RingId == ringId);

    private static RingFate Fate(ReplyKind kind) => kind switch
    {
        ReplyKind.Text => RingFate.Replied,
        ReplyKind.Closed => RingFate.Closed,
        _ => RingFate.Unanswered,
    };

    // A reply is the last word; "closed" after "no answer" says more (the popup was seen later). True when it changed.
    private bool Apply(LoggedParty party, RingFate fate, string? reply, DateTimeOffset? at = null)
    {
        if (party.Fate == RingFate.Replied || (fate == RingFate.Unanswered && party.Fate == RingFate.Closed)) return false;
        party.Fate = fate;
        if (fate == RingFate.Replied)
        {
            party.Reply = reply;
            party.RepliedAt = at ?? time.GetLocalNow();
        }
        return true;
    }
}
