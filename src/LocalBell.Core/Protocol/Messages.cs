namespace LocalBell.Core.Protocol;

/// <summary>Whether a PC is rung with a sound and a popup, or keeps the rings as missed.</summary>
public enum PeerStatus
{
    Available,
    DoNotDisturb,
}

/// <summary>What a PC says about itself in every message: who it is and where its server listens.</summary>
public sealed record PeerInfo
{
    /// <summary>Made once per install: the name of a PC can change, its id does not.</summary>
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>The TCP port of its server (its address is the one the message came from).</summary>
    public int Port { get; init; }

    public PeerStatus Status { get; init; }

    /// <summary>The version of the app, for information.</summary>
    public string Version { get; init; } = "";

    /// <summary>The same, safe to keep and show; null when it cannot be used (no id, no port).</summary>
    public PeerInfo? Checked()
    {
        if (Id.Length is 0 or > BellProtocol.MaxIdLength || Port is <= 0 or > 65535 || !Enum.IsDefined(Status)) return null;
        string name = BellProtocol.Clean(Name, BellProtocol.MaxNameLength);
        return this with { Name = name.Length > 0 ? name : "?", Version = BellProtocol.Clean(Version, 32) };
    }
}

/// <summary>The UDP datagram of the discovery, sent to the multicast group and to the broadcast address of each card.</summary>
public sealed record Announcement
{
    public string App { get; init; } = BellProtocol.AppTag;

    public int Protocol { get; init; } = BellProtocol.Version;

    public PeerInfo? Peer { get; init; }

    /// <summary>
    /// True when the sender wants an answer (it starts, it was refreshed, its network changed): the others say hello
    /// to its server. False for the periodic announcements, which only say "still here".
    /// </summary>
    public bool Announce { get; init; }

    /// <summary>The app closes: the others show the PC offline at once.</summary>
    public bool Leaving { get; init; }
}

/// <summary>Body of POST /hello: the caller introduces itself; the answer is the <see cref="PeerInfo"/> of the server.</summary>
public sealed record Hello
{
    public PeerInfo? Peer { get; init; }

    public bool Leaving { get; init; }
}

/// <summary>A PC known by the server, as GET /peers lists them: one link between two networks then joins them all.</summary>
public sealed record PeerAddress
{
    public PeerInfo? Peer { get; init; }

    /// <summary>The IP address the server reaches it at.</summary>
    public string Address { get; init; } = "";
}

/// <summary>Body of POST /ring.</summary>
public sealed record Ring
{
    /// <summary>Made by the caller: a ring sent twice (two addresses) shows once, and the reply names it.</summary>
    public string Id { get; init; } = "";

    public PeerInfo? From { get; init; }

    /// <summary>Optional text, shown in the popup.</summary>
    public string? Message { get; init; }

    /// <summary>Sent to everyone at once ("Sonner tout le monde").</summary>
    public bool Everyone { get; init; }
}

/// <summary>Answer to POST /ring.</summary>
public sealed record RingAnswer
{
    /// <summary>The PC is in "do not disturb": no sound, no popup, the ring kept as missed.</summary>
    public bool Quiet { get; init; }
}

/// <summary>What the PC rung says back about a ring.</summary>
public enum ReplyKind
{
    /// <summary>A reply button ("J'arrive"…): <see cref="Reply.Text"/>.</summary>
    Text,

    /// <summary>The popup was closed without a reply: seen.</summary>
    Closed,

    /// <summary>The bell stopped (or the popup closed by itself) and nobody answered.</summary>
    Unanswered,
}

/// <summary>
/// Body of POST /reply: what the PC rung says back to the one that rang, so that its row never says "it rings" when it
/// no longer does (the author, October 31, 2026).
/// </summary>
public sealed record Reply
{
    public string RingId { get; init; } = "";

    public PeerInfo? From { get; init; }

    public ReplyKind Kind { get; init; }

    public string Text { get; init; } = "";
}
