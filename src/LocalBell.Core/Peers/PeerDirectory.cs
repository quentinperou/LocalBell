using System.Net;
using LocalBell.Core.Protocol;

namespace LocalBell.Core.Peers;

/// <summary>Another PC as the app shows it.</summary>
public sealed record Peer(string Id, string Name, PeerStatus Status, IPEndPoint EndPoint, bool Online, string Version)
{
    /// <summary>The other addresses it was heard at, most recent first (a PC on two networks): tried in turn.</summary>
    public IReadOnlyList<IPEndPoint> EndPoints { get; init; } = [EndPoint];
}

/// <summary>What changed when a PC was heard from.</summary>
public enum SeenChange
{
    /// <summary>Nothing shown changed (its periodic announcement).</summary>
    None,

    /// <summary>Its name or its status changed.</summary>
    Changed,

    /// <summary>A PC not known before, or back online: worth asking it the PCs it knows.</summary>
    Appeared,
}

/// <summary>
/// The PCs known to this one, by id: online while they are heard from, offline after a silence or when they leave.
/// Offline PCs stay in the list (greyed, at the bottom). Safe to use from several threads.
/// </summary>
public sealed class PeerDirectory(TimeProvider time)
{
    private const int MaxEndPoints = 3;

    private sealed class Entry
    {
        public required PeerInfo Info;
        public required List<IPEndPoint> EndPoints;
        public bool Online;
        public DateTimeOffset LastSeen;

        public Peer ToPeer() => new(Info.Id, Info.Name, Info.Status, EndPoints[0], Online, Info.Version) { EndPoints = EndPoints.ToArray() };
    }

    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries = [];

    /// <summary>A PC heard from (an announcement, a request, an answer), at the address it was heard at.</summary>
    public SeenChange Seen(PeerInfo info, IPAddress address)
    {
        var endPoint = new IPEndPoint(address, info.Port);
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (!entries.TryGetValue(info.Id, out var entry))
            {
                entries[info.Id] = new Entry { Info = info, EndPoints = [endPoint], Online = true, LastSeen = now };
                return SeenChange.Appeared;
            }

            var change = !entry.Online ? SeenChange.Appeared
                : entry.Info.Name != info.Name || entry.Info.Status != info.Status ? SeenChange.Changed
                : SeenChange.None;
            entry.Info = info;
            entry.Online = true;
            entry.LastSeen = now;
            entry.EndPoints.Remove(endPoint);
            entry.EndPoints.Insert(0, endPoint);
            if (entry.EndPoints.Count > MaxEndPoints) entry.EndPoints.RemoveAt(MaxEndPoints);
            return change;
        }
    }

    /// <summary>A PC known from an earlier run, offline until it is heard from. True when it was added.</summary>
    public bool Remember(PeerInfo info, IPEndPoint endPoint)
    {
        lock (gate)
            return entries.TryAdd(info.Id, new Entry { Info = info, EndPoints = [endPoint], Online = false, LastSeen = DateTimeOffset.MinValue });
    }

    /// <summary>A PC that said it leaves. True when it was online.</summary>
    public bool Left(string id)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(id, out var entry) || !entry.Online) return false;
            entry.Online = false;
            return true;
        }
    }

    /// <summary>Puts offline the PCs not heard from for <paramref name="silence"/>. True when one changed.</summary>
    public bool Expire(TimeSpan silence)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            bool changed = false;
            foreach (var entry in entries.Values.Where(e => e.Online && now - e.LastSeen > silence))
            {
                entry.Online = false;
                changed = true;
            }
            return changed;
        }
    }

    /// <summary>Forgets a PC (removed from the list by the user).</summary>
    public bool Forget(string id)
    {
        lock (gate)
            return entries.Remove(id);
    }

    public Peer? Find(string id)
    {
        lock (gate)
            return entries.TryGetValue(id, out var entry) ? entry.ToPeer() : null;
    }

    /// <summary>The PCs, online first, then by name.</summary>
    public IReadOnlyList<Peer> All()
    {
        lock (gate)
            return entries.Values.Select(e => e.ToPeer())
                .OrderByDescending(p => p.Online)
                .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
    }

    /// <summary>The PCs not heard from for <paramref name="silence"/> (online or not): to be asked directly.</summary>
    public IReadOnlyList<Peer> Quiet(TimeSpan silence)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            return entries.Values.Where(e => now - e.LastSeen > silence).Select(e => e.ToPeer()).ToList();
        }
    }

    /// <summary>True when one online PC is reached at this address and port.</summary>
    public bool IsOnlineAt(IPEndPoint endPoint)
    {
        lock (gate)
            return entries.Values.Any(e => e.Online && e.EndPoints.Contains(endPoint));
    }
}
