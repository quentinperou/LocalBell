using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LocalBell.Core.Network;
using LocalBell.Core.Peers;
using LocalBell.Core.Protocol;

namespace LocalBell.Core;

/// <summary>How a <see cref="BellNode"/> starts.</summary>
public sealed class NodeOptions
{
    public required string Id { get; init; }

    /// <summary>The name of this PC; the Windows name of the PC when empty.</summary>
    public string Name { get; init; } = "";

    public PeerStatus Status { get; init; }

    public string Version { get; init; } = "";

    /// <summary>Where the server listens: every card, or 127.0.0.1 in the tests.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Any;

    /// <summary>The TCP port tried first (0: any free port).</summary>
    public int ServerPort { get; init; } = BellProtocol.DefaultPort;

    /// <summary>The UDP port of the announcements, and the port a scan asks.</summary>
    public int DiscoveryPort { get; init; } = BellProtocol.DefaultPort;

    /// <summary>The UDP announcements and the scan of the network; off in the tests (HTTP only).</summary>
    public bool Discovery { get; init; } = true;

    /// <summary>The scan of the network when the announcements found nobody; off to try the app without it.</summary>
    public bool Scan { get; init; } = true;

    /// <summary>Addresses added by hand ("192.168.41.12", "PC-BUREAU:53318"): asked directly, whatever the network.</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>PCs known from an earlier run: listed offline until they are heard from, and asked at start.</summary>
    public IReadOnlyList<(PeerInfo Info, IPEndPoint EndPoint)> KnownPeers { get; init; } = [];

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// Where to write what the node does (developer mode of the app): cards, computers found or gone, rings, replies,
    /// failures. Never the text of the messages. Called from any thread.
    /// </summary>
    public Action<string>? Log { get; init; }
}

public enum RingResult
{
    /// <summary>The PC rings.</summary>
    Rung,

    /// <summary>The PC is in "do not disturb": the ring waits in its missed rings.</summary>
    Quiet,

    /// <summary>The PC could not be reached.</summary>
    Failed,
}

public sealed record RingOutcome(string RingId, Peer Peer, RingResult Result);

/// <summary>A ring received. <see cref="Quiet"/>: this PC was in "do not disturb" (no sound, no popup).</summary>
public sealed record IncomingRing(string Id, Peer From, string Message, bool Everyone, bool Quiet, DateTimeOffset At);

/// <summary>What a PC rung by this one says back: a reply ("J'arrive"…), its popup closed, or no answer.</summary>
public sealed record IncomingReply(string RingId, Peer From, ReplyKind Kind, string Text);

/// <summary>
/// LocalBell on one PC: its server, its discovery, the PCs it knows; rings them and is rung. Every PC is the same
/// (no server anywhere). How the others are found, from the simplest to the most stubborn:
/// <list type="number">
/// <item>an announcement on every card (multicast and broadcast), answered by a hello to its server;</item>
/// <item>when nobody answered after a few seconds, a hello to every address of the card's network (a scan);</item>
/// <item>the addresses added by hand, asked directly (another network: routers stop announcements);</item>
/// <item>the PCs known by each PC newly found: one link between two networks joins every PC of both.</item>
/// </list>
/// Events come on a thread of the pool.
/// </summary>
public sealed class BellNode : IAsyncDisposable
{
    /// <summary>Every 30 s: an announcement "still here", and a hello to the PCs that were not heard from.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan AskAfter = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(100);
    private static readonly TimeSpan ScanAfter = TimeSpan.FromSeconds(3);
    private const int ScanParallelism = 64;
    private const int MaxReplyLength = 60;

    // The PCs another PC lists (GET /peers): at most so many asked, so many at a time.
    private const int MaxPeersAsked = 64;
    private const int PeersParallelism = 8;

    // The answers of other PCs are a few hundred bytes of JSON; nothing bigger is read.
    private const int MaxAnswerBytes = 64 * 1024;

    private readonly NodeOptions options;
    private readonly PeerDirectory directory;
    private readonly MiniHttpServer server;
    private readonly Discovery? discovery;
    private readonly HttpClient http;
    private readonly HttpClient scanHttp;
    private readonly RecentIds rings = new(256);
    private readonly RingsSent sent = new(256);
    private readonly RingThrottle throttle;
    private readonly CancellationTokenSource stop = new();
    private readonly Lock gate = new();
    private List<string> addresses;
    private volatile string id; // the one of the options, unless another PC turns out to have it too (IsThisPc)
    private string name;
    private PeerStatus status;
    private Task? loop;

    public BellNode(NodeOptions options)
    {
        this.options = options;
        id = options.Id;
        name = CleanName(options.Name);
        status = options.Status;
        addresses = [.. options.Addresses];
        directory = new PeerDirectory(options.Time);
        throttle = new RingThrottle(options.Time);
        foreach (var (info, endPoint) in options.KnownPeers)
            if (info.Checked() is { } known && known.Id != id && endPoint.AddressFamily == AddressFamily.InterNetwork)
                directory.Remember(known, endPoint);
        server = new MiniHttpServer(options.BindAddress, options.ServerPort, HandleAsync);
        if (options.Discovery) discovery = new Discovery(options.DiscoveryPort, BellProtocol.MulticastGroup, OnAnnouncement);
        // No proxy: an office proxy would get requests meant for the PC next door. No redirect: a host that answers
        // "go there" would send this PC anywhere, the Internet included.
        http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(1.5), UseProxy = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(4), MaxResponseContentBufferSize = MaxAnswerBytes };
        scanHttp = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromMilliseconds(500), UseProxy = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(1.5), MaxResponseContentBufferSize = MaxAnswerBytes };
    }

    /// <summary>This PC as the others see it.</summary>
    public PeerInfo Self => new() { Id = id, Name = name, Port = server.Port, Status = status, Version = options.Version };

    public string Name => name;

    public PeerStatus Status => status;

    /// <summary>The port of the server, once started.</summary>
    public int Port => server.Port;

    /// <summary>
    /// False when the UDP port of the announcements could not be had (another program, or a range Windows reserves for
    /// Hyper-V, WSL or Docker): this PC still announces itself and is found, but finds the others only by the scan,
    /// the addresses added by hand and their answers.
    /// </summary>
    public bool HearsAnnouncements { get; private set; }

    /// <summary>The PCs known, online first, then by name.</summary>
    public IReadOnlyList<Peer> Peers => directory.All();

    /// <summary>A PC appeared, left, or changed its name or status.</summary>
    public event EventHandler? PeersChanged;

    public event EventHandler<IncomingRing>? RingReceived;

    public event EventHandler<IncomingReply>? ReplyReceived;
    /// <summary>This PC took a new id: another PC had the same (settings copied with a disk image). The app keeps it.</summary>
    public event EventHandler<string>? IdChanged;

    /// <summary>
    /// Starts the server and the discovery, then asks the PCs known and the addresses added by hand. On the thread pool:
    /// started from a window, the loops would otherwise run on its thread and wait for it.
    /// </summary>
    public Task StartAsync() => Task.Run(StartCoreAsync);

    private async Task StartCoreAsync()
    {
        server.Start();
        Log($"Server on TCP {server.Port} ({options.BindAddress}); id {Short(id)}, name \"{name}\"");
        if (discovery is not null)
        {
            try
            {
                discovery.Start();
                HearsAnnouncements = true;
                Log($"Discovery on UDP {options.DiscoveryPort}, group {BellProtocol.MulticastGroup}; cards: {Cards()}");
            }
            catch (SocketException ex)
            {
                // The app goes on without hearing them: the others still hear this PC and answer its server.
                Log($"UDP {options.DiscoveryPort} unavailable ({ex.SocketErrorCode}): the announcements of the others are not heard; "
                    + $"the scan and the addresses added by hand find them. Cards: {Cards()}");
            }
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            discovery.Send(MakeAnnouncement(announce: true));
        }
        // The PCs known and the addresses added by hand, all at once: five PCs off no longer hold the start 20 s.
        await Safely("First hellos", () => Task.WhenAll(AskAsync(directory.All(), stop.Token), AskAddressesAsync(stop.Token)));
        loop = RunAsync(stop.Token);
    }

    private void Log(string line) => options.Log?.Invoke(line);

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    private static string Cards() => string.Join(", ", LocalNetwork.Addresses()
        .Select(a => $"{a.CardName} {a.Address}/{a.PrefixLength}{(a.HasGateway ? " (gateway)" : "")}")) is { Length: > 0 } list ? list : "none";

    /// <summary>
    /// The life of the node: a scan if nobody answered, then the heartbeat. An error in one step is logged and the next
    /// one runs: an exception here used to end the heartbeat for the rest of the session, silently.
    /// </summary>
    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            if (discovery is not null && options.Scan)
            {
                await Task.Delay(ScanAfter, options.Time, token);
                if (!directory.All().Any(p => p.Online)) await Safely("Scan", () => ScanAsync(token));
            }
            using var timer = new PeriodicTimer(Heartbeat, options.Time);
            while (await timer.WaitForNextTickAsync(token))
                await Safely("Heartbeat", () => BeatAsync(token));
        }
        catch (OperationCanceledException) { }
    }

    private async Task BeatAsync(CancellationToken token)
    {
        discovery?.Send(MakeAnnouncement(announce: false));
        await AskAsync(directory.Quiet(AskAfter), token);
        await AskAddressesAsync(token);
        var before = directory.All().Where(p => p.Online).Select(p => p.Id).ToHashSet();
        if (directory.Expire(OfflineAfter))
        {
            Log("Offline after a silence: " + string.Join(", ", directory.All().Where(p => !p.Online && before.Contains(p.Id)).Select(p => p.Name)));
            RaisePeersChanged();
        }
    }

    // A step of the loop: its error logged, never the end of the loop (cancellation still ends it).
    private async Task Safely(string what, Func<Task> step)
    {
        try { await step(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"{what}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- Ringing and answering ----

    /// <summary>Rings a PC, with a message or not; each of its addresses is tried in turn.</summary>
    public async Task<RingOutcome> RingAsync(Peer peer, string? message, bool everyone = false, CancellationToken token = default)
    {
        var current = directory.Find(peer.Id) ?? peer;
        string ringId = Guid.NewGuid().ToString("N");
        string text = BellProtocol.Clean(message, BellProtocol.MaxMessageLength);
        var ring = new Ring { Id = ringId, From = Self, Message = text.Length > 0 ? text : null, Everyone = everyone };
        sent.Add(ringId, current.Id); // its replies are taken from that PC only
        foreach (var endPoint in current.EndPoints)
        {
            var answer = await PostAsync<RingAnswer>(http, endPoint, BellProtocol.RingPath, ring, token);
            if (answer is not null)
            {
                Log($"Ring {Short(ringId)} to {current.Name} at {endPoint}{(everyone ? " (everyone)" : "")}{(text.Length > 0 ? ", with a message" : "")}: {(answer.Quiet ? "quiet (do not disturb)" : "rung")}");
                return new RingOutcome(ringId, current, answer.Quiet ? RingResult.Quiet : RingResult.Rung);
            }
            Log($"Ring {Short(ringId)} to {current.Name}: {endPoint} did not answer");
        }
        Log($"Ring {Short(ringId)} to {current.Name}: failed ({current.EndPoints.Count} address(es) tried)");
        return new RingOutcome(ringId, current, RingResult.Failed);
    }

    /// <summary>Rings every PC online at once.</summary>
    public async Task<IReadOnlyList<RingOutcome>> RingEveryoneAsync(string? message, CancellationToken token = default) =>
        await Task.WhenAll(directory.All().Where(p => p.Online).Select(p => RingAsync(p, message, everyone: true, token)));

    /// <summary>
    /// Answers a ring ("J'arrive"…), or tells the PC that rang that its ring was closed or not answered. False when it
    /// could not be reached.
    /// </summary>
    public async Task<bool> ReplyAsync(IncomingRing ring, string text, ReplyKind kind = ReplyKind.Text, CancellationToken token = default)
    {
        var from = directory.Find(ring.From.Id) ?? ring.From;
        var reply = new Reply
        {
            RingId = ring.Id, From = Self, Kind = kind, Text = kind == ReplyKind.Text ? BellProtocol.Clean(text, MaxReplyLength) : "",
        };
        foreach (var endPoint in from.EndPoints)
            if (await PostAsync<RingAnswer>(http, endPoint, BellProtocol.ReplyPath, reply, token) is not null)
            {
                Log($"Reply to ring {Short(ring.Id)} of {from.Name}: {kind}");
                return true;
            }
        Log($"Reply to ring {Short(ring.Id)} of {from.Name}: {kind}, could not be sent");
        return false;
    }

    // ---- Settings that change while it runs ----

    /// <summary>Renames this PC; the others see it within seconds.</summary>
    public void Rename(string newName)
    {
        name = CleanName(newName);
        ShareChange();
    }

    /// <summary>"Do not disturb" on or off; the others see it within seconds.</summary>
    public void SetStatus(PeerStatus newStatus)
    {
        status = newStatus;
        ShareChange();
    }

    private void ShareChange()
    {
        discovery?.Send(MakeAnnouncement(announce: false));
        _ = AskAsync(directory.All().Where(p => p.Online).ToList(), stop.Token);
    }

    /// <summary>The addresses added by hand, after a change in Settings.</summary>
    public void SetAddresses(IEnumerable<string> list)
    {
        lock (gate) addresses = [.. list];
        _ = AskAddressesAsync(stop.Token);
    }

    /// <summary>Asks an address typed by the user: the PC that answers there, or null when none does.</summary>
    public async Task<Peer?> ProbeAsync(string address, CancellationToken token = default)
    {
        foreach (var endPoint in await ResolveAsync(address, token))
            if (await HelloAsync(http, endPoint, token) is { } info) return directory.Find(info.Id);
        return null;
    }

    /// <summary>Removes a PC from the list (it comes back if it is heard from again).</summary>
    public void Forget(string peerId)
    {
        if (directory.Forget(peerId)) RaisePeersChanged();
    }

    /// <summary>"Actualiser": the announcement again, every PC known asked, and a scan of the networks.</summary>
    public async Task RefreshAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        if (discovery is not null)
        {
            discovery.JoinGroup();
            discovery.Send(MakeAnnouncement(announce: true));
        }
        await AskAsync(directory.All(), linked.Token);
        await AskAddressesAsync(linked.Token);
        if (discovery is not null && options.Scan) await ScanAsync(linked.Token);
    }

    // ---- Finding the others ----

    private Announcement MakeAnnouncement(bool announce, bool leaving = false) =>
        new() { Peer = Self, Announce = announce, Leaving = leaving };

    private void OnAnnouncement(Announcement announcement, IPAddress address)
    {
        // On the receiving thread of the discovery: an exception here would end it for the session.
        try
        {
            var info = announcement.Peer?.Checked();
            if (info is null || IsThisPc(info, address)) return;
            if (announcement.Leaving)
            {
                Log($"UDP from {address}: {info.Name} leaves");
                if (directory.Left(info.Id)) RaisePeersChanged();
                return;
            }
            if (announcement.Announce) Log($"UDP from {address}: {info.Name} starts (or looks again), answered");
            Heard(info, address);
            if (announcement.Announce) _ = AnswerAsync(info, address);
        }
        catch (Exception ex)
        {
            Log($"Announcement from {address}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// True for this PC's own id. Another PC may have it too: settings copied with a disk image (the audit, October 31),
    /// and the two would ignore each other and show as one on the other screens. It is told apart by another name at
    /// an address that is not this PC's (this PC's own announcements come back on its cards): the one whose name comes
    /// last takes a new id (the app keeps it), tells the other, and the other is then a PC like any other.
    /// </summary>
    private bool IsThisPc(PeerInfo info, IPAddress from)
    {
        if (info.Id != id) return false;
        if (info.Name == name || LocalNetwork.Addresses().Any(local => local.Address.Equals(from))) return true;
        if (string.CompareOrdinal(name, info.Name) < 0) return true; // the other one takes a new id
        string old = id;
        id = Guid.NewGuid().ToString("N");
        Log($"Id {Short(old)} is also the one of {info.Name} at {from} (settings copied with a disk image?): new id {Short(id)}");
        IdChanged?.Invoke(this, id);
        discovery?.Send(MakeAnnouncement(announce: true));
        _ = AnswerAsync(info, from);
        return false;
    }
    /// <summary>A PC that starts wants to know the others: a hello to its server, else an answer by UDP.</summary>
    private async Task AnswerAsync(PeerInfo info, IPAddress address)
    {
        try
        {
            if (await HelloAsync(http, new IPEndPoint(address, info.Port), stop.Token) is null)
                discovery?.SendTo(MakeAnnouncement(announce: false), address);
        }
        catch (OperationCanceledException) { }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        if (discovery is null || stop.IsCancellationRequested) return;
        Log($"Network changed; cards: {Cards()}");
        discovery.JoinGroup();
        discovery.Send(MakeAnnouncement(announce: true));
    }

    /// <summary>A PC heard from; when it is new (or back), it is asked the PCs it knows.</summary>
    private void Heard(PeerInfo info, IPAddress address)
    {
        var change = directory.Seen(info, address);
        if (change == SeenChange.None) return;
        Log($"{info.Name} ({Short(info.Id)}, {info.Version}) at {address}:{info.Port}: "
            + (change == SeenChange.Appeared ? "online" : $"now {info.Status}"));
        RaisePeersChanged();
        if (change == SeenChange.Appeared) _ = AskTheirPeersAsync(info.Id, new IPEndPoint(address, info.Port));
    }

    /// <summary>
    /// The PCs a PC newly found knows, each asked once: only addresses of a local network, at most
    /// <see cref="MaxPeersAsked"/>, a few at a time. A host that lists thousands of Internet addresses would otherwise
    /// send this PC knocking on each. A PC that answers is a real LocalBell: confirmed.
    /// </summary>
    private async Task AskTheirPeersAsync(string id, IPEndPoint endPoint)
    {
        try
        {
            var list = await GetAsync<PeerAddress[]>(http, endPoint, BellProtocol.PeersPath, stop.Token);
            if (list is null) return;
            directory.Confirm(id);
            var locals = LocalNetwork.Addresses();
            var unknown = list
                .Select(p => (Info: p.Peer?.Checked(), Ok: IPAddress.TryParse(p.Address, out var a), Address: a))
                .Where(p => p.Info is not null && p.Ok && p.Info.Id != id && directory.Find(p.Info.Id) is not { Online: true }
                    && LocalNetwork.IsLan(p.Address!, endPoint.Address, locals))
                .Select(p => new IPEndPoint(p.Address!, p.Info!.Port))
                .Distinct()
                .Take(MaxPeersAsked)
                .ToList();
            await Parallel.ForEachAsync(unknown, new ParallelOptions { MaxDegreeOfParallelism = PeersParallelism, CancellationToken = stop.Token },
                async (e, t) => await HelloAsync(http, e, t));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"PCs known by {endPoint}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>A hello to each PC (each of its addresses until one answers), all at once.</summary>
    private Task AskAsync(IEnumerable<Peer> peers, CancellationToken token) =>
        Task.WhenAll(peers.Select(async peer =>
        {
            foreach (var endPoint in peer.EndPoints)
                if (await HelloAsync(http, endPoint, token) is not null) return;
        }));

    /// <summary>A hello to the addresses added by hand, all at once, unless a PC online already answers there.</summary>
    private Task AskAddressesAsync(CancellationToken token)
    {
        List<string> list;
        lock (gate) list = addresses;
        return Task.WhenAll(list.Select(async address =>
        {
            var endPoints = await ResolveAsync(address, token);
            if (endPoints.Count == 0) Log($"Address added by hand \"{address}\": not found (name not resolved, or not IPv4)");
            foreach (var endPoint in endPoints)
            {
                if (directory.IsOnlineAt(endPoint)) break;
                if (await HelloAsync(http, endPoint, token) is not null) break;
                Log($"Address added by hand \"{address}\": {endPoint} does not answer");
            }
        }));
    }

    /// <summary>A hello to every address of the networks of this PC (at most a /24 per card), a few dozen at a time.</summary>
    private async Task ScanAsync(CancellationToken token)
    {
        var targets = LocalNetwork.Addresses()
            .SelectMany(local => LocalNetwork.Neighbours(local.Address, local.PrefixLength))
            .Distinct()
            .Select(address => new IPEndPoint(address, options.DiscoveryPort))
            .Where(endPoint => !directory.IsOnlineAt(endPoint))
            .ToList();
        Log($"Scan of {targets.Count} addresses (nobody answered the announcement, or Refresh)");
        int found = 0;
        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = ScanParallelism, CancellationToken = token },
            async (endPoint, t) =>
            {
                if (await HelloAsync(scanHttp, endPoint, t) is not null) Interlocked.Increment(ref found);
            });
        Log($"Scan done: {found} computer(s) answered");
    }

    /// <summary>
    /// "192.168.41.12", "192.168.41.12:53318", "PC-BUREAU" or "PC-BUREAU:53318", as typed in Settings. IPv4 only, as
    /// the rest of LocalBell: an IPv6 address is refused.
    /// </summary>
    public static (string Host, int Port)? ParseAddress(string text, int defaultPort = BellProtocol.DefaultPort)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Contains(' ') || text.Contains('[') || text.Count(c => c == ':') > 1) return null;
        int colon = text.LastIndexOf(':');
        string host = colon < 0 ? text : text[..colon];
        int port = defaultPort;
        if (colon >= 0 && (colon == 0 || !int.TryParse(text[(colon + 1)..], out port) || port is <= 0 or > 65535)) return null;
        // 253: the longest a DNS name can be.
        if (host.Length > 253 || IPAddress.TryParse(host, out var ip) && ip.AddressFamily != AddressFamily.InterNetwork) return null;
        return (host, port);
    }

    private static async Task<IReadOnlyList<IPEndPoint>> ResolveAsync(string text, CancellationToken token)
    {
        if (ParseAddress(text) is not { } parsed) return [];
        if (IPAddress.TryParse(parsed.Host, out var ip)) return [new IPEndPoint(ip, parsed.Port)];
        try
        {
            var found = await Dns.GetHostAddressesAsync(parsed.Host, AddressFamily.InterNetwork, token);
            return found.Select(a => new IPEndPoint(a, parsed.Port)).ToList();
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException) { return []; }
    }

    // ---- The server ----

    private Task<HttpResponseData> HandleAsync(HttpRequestData request, CancellationToken token) =>
        Task.FromResult((request.Method, request.Path) switch
        {
            ("POST", BellProtocol.HelloPath) => OnHello(request),
            ("GET", BellProtocol.PeersPath) => HttpResponseData.Ok(BellProtocol.Serialize(directory.All()
                .Where(p => p.Online)
                .Select(p => new PeerAddress
                {
                    Peer = new PeerInfo { Id = p.Id, Name = p.Name, Port = p.EndPoint.Port, Status = p.Status, Version = p.Version },
                    Address = p.EndPoint.Address.ToString(),
                })
                .ToArray())),
            ("POST", BellProtocol.RingPath) => OnRing(request),
            ("POST", BellProtocol.ReplyPath) => OnReply(request),
            _ => HttpResponseData.NotFound,
        });

    private HttpResponseData OnHello(HttpRequestData request)
    {
        var hello = BellProtocol.Deserialize<Hello>(request.Body);
        if (hello?.Peer?.Checked() is not { } info) return HttpResponseData.BadRequest;
        if (!IsThisPc(info, request.Remote))
        {
            if (!hello.Leaving) Heard(info, request.Remote);
            else
            {
                Log($"HTTP from {request.Remote}: {info.Name} leaves");
                if (directory.Left(info.Id)) RaisePeersChanged();
            }
        }
        return HttpResponseData.Ok(BellProtocol.Serialize(Self));
    }

    private HttpResponseData OnRing(HttpRequestData request)
    {
        var ring = BellProtocol.Deserialize<Ring>(request.Body);
        if (ring?.From?.Checked() is not { } from || !BellProtocol.IsId(ring.Id) || from.Id == id)
            return HttpResponseData.BadRequest;
        Heard(from, request.Remote);
        bool quiet = status == PeerStatus.DoNotDisturb;
        if (!rings.Add(ring.Id)) Log($"Ring {Short(ring.Id)} from {from.Name}: again (another address), shown once");
        else if (!throttle.Allow(request.Remote)) Log($"Ring {Short(ring.Id)} from {from.Name} at {request.Remote}: too many rings from there, not shown");
        else if (directory.Find(from.Id) is { } peer)
        {
            Log($"Ring {Short(ring.Id)} from {from.Name} at {request.Remote}{(ring.Everyone ? " (everyone)" : "")}"
                + $"{(string.IsNullOrWhiteSpace(ring.Message) ? "" : ", with a message")}{(quiet ? ": quiet (do not disturb)" : "")}");
            RingReceived?.Invoke(this, new IncomingRing(ring.Id, peer, BellProtocol.Clean(ring.Message, BellProtocol.MaxMessageLength),
                ring.Everyone, quiet, options.Time.GetUtcNow()));
        }
        return HttpResponseData.Ok(BellProtocol.Serialize(new RingAnswer { Quiet = quiet }));
    }

    private HttpResponseData OnReply(HttpRequestData request)
    {
        var reply = BellProtocol.Deserialize<Reply>(request.Body);
        if (reply?.From?.Checked() is not { } from || !BellProtocol.IsId(reply.RingId) || from.Id == id
            || !Enum.IsDefined(reply.Kind))
            return HttpResponseData.BadRequest;
        Heard(from, request.Remote);
        // Only a reply to a ring this PC sent, from the PC it rang.
        if (!sent.Matches(reply.RingId, from.Id))
        {
            Log($"Reply from {from.Name} to ring {Short(reply.RingId)}: not a ring sent to it, ignored");
            return HttpResponseData.Ok(BellProtocol.Serialize(new RingAnswer()));
        }
        Log($"Reply from {from.Name} to ring {Short(reply.RingId)}: {reply.Kind}");
        if (directory.Find(from.Id) is { } peer)
            ReplyReceived?.Invoke(this, new IncomingReply(reply.RingId, peer, reply.Kind, BellProtocol.Clean(reply.Text, MaxReplyLength)));
        return HttpResponseData.Ok(BellProtocol.Serialize(new RingAnswer()));
    }

    // ---- HTTP ----

    /// <summary>POST /hello to a server: the PC that answers there (now known), or null.</summary>
    private async Task<PeerInfo?> HelloAsync(HttpClient client, IPEndPoint endPoint, CancellationToken token)
    {
        var answer = await PostAsync<PeerInfo>(client, endPoint, BellProtocol.HelloPath, new Hello { Peer = Self }, token);
        if (answer?.Checked() is not { } info || IsThisPc(info, endPoint.Address)) return null;
        Heard(info, endPoint.Address);
        directory.Confirm(info.Id); // it answered at that address: a real LocalBell
        return info;
    }

    private static Uri UriOf(IPEndPoint endPoint, string path) => new($"http://{endPoint.Address}:{endPoint.Port}{path}");

    /// <summary>The answer of the server, or null when it could not be reached or did not answer as LocalBell does.</summary>
    private static async Task<T?> PostAsync<T>(HttpClient client, IPEndPoint endPoint, string path, object body, CancellationToken token)
        where T : class
    {
        using var content = new ByteArrayContent(BellProtocol.Serialize(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await SendAsync<T>(() => client.PostAsync(UriOf(endPoint, path), content, token), token);
    }

    private static Task<T?> GetAsync<T>(HttpClient client, IPEndPoint endPoint, string path, CancellationToken token) where T : class =>
        SendAsync<T>(() => client.GetAsync(UriOf(endPoint, path), token), token);

    private static async Task<T?> SendAsync<T>(Func<Task<HttpResponseMessage>> send, CancellationToken token) where T : class
    {
        try
        {
            using var response = await send();
            if (!response.IsSuccessStatusCode) return null;
            return BellProtocol.Deserialize<T>(await response.Content.ReadAsByteArrayAsync(token));
        }
        // Unreachable, too slow, too big an answer (MaxAnswerBytes), or an address no URL can hold.
        catch (Exception ex) when (!token.IsCancellationRequested
            && ex is HttpRequestException or TaskCanceledException or IOException or UriFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private void RaisePeersChanged() => PeersChanged?.Invoke(this, EventArgs.Empty);

    private static string CleanName(string text) =>
        BellProtocol.Clean(text, BellProtocol.MaxNameLength) is { Length: > 0 } clean ? clean : Environment.MachineName;

    /// <summary>Tells the others this PC leaves (they show it offline at once), then stops.</summary>
    public async ValueTask DisposeAsync()
    {
        if (stop.IsCancellationRequested) return;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        stop.Cancel();
        Log("Leaving: the others are told");
        discovery?.Send(MakeAnnouncement(announce: false, leaving: true));

        // The PCs that do not hear the announcements (another network, added by hand) are told directly, quickly.
        using (var quick = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            var bye = new Hello { Peer = Self, Leaving = true };
            await Task.WhenAll(directory.All().Where(p => p.Online).Select(async p =>
            {
                try { await PostAsync<PeerInfo>(http, p.EndPoint, BellProtocol.HelloPath, bye, quick.Token); }
                catch (OperationCanceledException) { }
            }));
        }

        if (loop is not null) await loop;
        discovery?.Dispose();
        await server.DisposeAsync();
        http.Dispose();
        scanHttp.Dispose();
        stop.Dispose();
    }

    /// <summary>The last rings sent, with the PC each one went to: a reply is taken only from that PC.</summary>
    private sealed class RingsSent(int capacity)
    {
        private readonly Queue<string> order = new();
        private readonly Dictionary<string, string> peers = [];

        public void Add(string ringId, string peerId)
        {
            lock (peers)
            {
                peers[ringId] = peerId;
                order.Enqueue(ringId);
                if (order.Count > capacity) peers.Remove(order.Dequeue());
            }
        }

        public bool Matches(string ringId, string peerId)
        {
            lock (peers)
                return peers.TryGetValue(ringId, out string? rung) && rung == peerId;
        }
    }

    /// <summary>
    /// Rings shown per address: a burst of <see cref="Burst"/>, then one every <see cref="Every"/>. A host that rings in
    /// a loop with ever new ids would otherwise bury the PC under popups and bells.
    /// </summary>
    private sealed class RingThrottle(TimeProvider time)
    {
        private const double Burst = 5;
        private static readonly TimeSpan Every = TimeSpan.FromSeconds(2);
        private readonly Dictionary<IPAddress, (double Tokens, DateTimeOffset At)> buckets = [];

        public bool Allow(IPAddress address)
        {
            var now = time.GetUtcNow();
            lock (buckets)
            {
                // Forgotten once full again: the table never grows past the hosts that rang lately.
                foreach (var stale in buckets.Where(b => now - b.Value.At > Every * Burst).Select(b => b.Key).ToList())
                    buckets.Remove(stale);
                var (tokens, at) = buckets.TryGetValue(address, out var bucket) ? bucket : (Burst, now);
                tokens = Math.Min(Burst, tokens + (now - at) / Every);
                if (tokens < 1)
                {
                    buckets[address] = (tokens, now);
                    return false;
                }
                buckets[address] = (tokens - 1, now);
                return true;
            }
        }
    }

    /// <summary>The last ring ids received: a ring that arrives twice (two addresses) shows once.</summary>
    private sealed class RecentIds(int capacity)
    {
        private readonly Queue<string> order = new();
        private readonly HashSet<string> ids = [];

        public bool Add(string id)
        {
            lock (ids)
            {
                if (!ids.Add(id)) return false;
                order.Enqueue(id);
                if (order.Count > capacity) ids.Remove(order.Dequeue());
                return true;
            }
        }
    }
}
