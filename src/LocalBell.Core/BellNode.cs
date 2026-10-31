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

    private readonly NodeOptions options;
    private readonly PeerDirectory directory;
    private readonly MiniHttpServer server;
    private readonly Discovery? discovery;
    private readonly HttpClient http;
    private readonly HttpClient scanHttp;
    private readonly RecentIds rings = new(256);
    private readonly CancellationTokenSource stop = new();
    private readonly Lock gate = new();
    private List<string> addresses;
    private string name;
    private PeerStatus status;
    private Task? loop;

    public BellNode(NodeOptions options)
    {
        this.options = options;
        name = CleanName(options.Name);
        status = options.Status;
        addresses = [.. options.Addresses];
        directory = new PeerDirectory(options.Time);
        foreach (var (info, endPoint) in options.KnownPeers)
            if (info.Checked() is { } known && known.Id != options.Id) directory.Remember(known, endPoint);
        server = new MiniHttpServer(options.BindAddress, options.ServerPort, HandleAsync);
        if (options.Discovery) discovery = new Discovery(options.DiscoveryPort, BellProtocol.MulticastGroup, OnAnnouncement);
        // No proxy: an office proxy would get requests meant for the PC next door.
        http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(1.5), UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(4) };
        scanHttp = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromMilliseconds(500), UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(1.5) };
    }

    /// <summary>This PC as the others see it.</summary>
    public PeerInfo Self => new() { Id = options.Id, Name = name, Port = server.Port, Status = status, Version = options.Version };

    public string Name => name;

    public PeerStatus Status => status;

    /// <summary>The port of the server, once started.</summary>
    public int Port => server.Port;

    /// <summary>The PCs known, online first, then by name.</summary>
    public IReadOnlyList<Peer> Peers => directory.All();

    /// <summary>A PC appeared, left, or changed its name or status.</summary>
    public event EventHandler? PeersChanged;

    public event EventHandler<IncomingRing>? RingReceived;

    public event EventHandler<IncomingReply>? ReplyReceived;

    /// <summary>
    /// Starts the server and the discovery, then asks the PCs known and the addresses added by hand. On the thread pool:
    /// started from a window, the loops would otherwise run on its thread and wait for it.
    /// </summary>
    public Task StartAsync() => Task.Run(StartCoreAsync);

    private async Task StartCoreAsync()
    {
        server.Start();
        Log($"Server on TCP {server.Port} ({options.BindAddress}); id {Short(options.Id)}, name \"{name}\"");
        if (discovery is not null)
        {
            discovery.Start();
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            Log($"Discovery on UDP {options.DiscoveryPort}, group {BellProtocol.MulticastGroup}; cards: {Cards()}");
            discovery.Send(MakeAnnouncement(announce: true));
        }
        await AskAsync(directory.All(), stop.Token);
        await AskAddressesAsync(stop.Token);
        loop = RunAsync(stop.Token);
    }

    private void Log(string line) => options.Log?.Invoke(line);

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    private static string Cards() => string.Join(", ", LocalNetwork.Addresses()
        .Select(a => $"{a.CardName} {a.Address}/{a.PrefixLength}{(a.HasGateway ? " (gateway)" : "")}")) is { Length: > 0 } list ? list : "none";

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            if (discovery is not null && options.Scan)
            {
                await Task.Delay(ScanAfter, options.Time, token);
                if (!directory.All().Any(p => p.Online)) await ScanAsync(token);
            }
            using var timer = new PeriodicTimer(Heartbeat, options.Time);
            while (await timer.WaitForNextTickAsync(token))
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
        }
        catch (OperationCanceledException) { }
    }

    // ---- Ringing and answering ----

    /// <summary>Rings a PC, with a message or not; each of its addresses is tried in turn.</summary>
    public async Task<RingOutcome> RingAsync(Peer peer, string? message, bool everyone = false, CancellationToken token = default)
    {
        var current = directory.Find(peer.Id) ?? peer;
        string ringId = Guid.NewGuid().ToString("N");
        string text = BellProtocol.Clean(message, BellProtocol.MaxMessageLength);
        var ring = new Ring { Id = ringId, From = Self, Message = text.Length > 0 ? text : null, Everyone = everyone };
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
        var info = announcement.Peer?.Checked();
        if (info is null || info.Id == options.Id) return;
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
        if (change == SeenChange.Appeared) _ = AskTheirPeersAsync(new IPEndPoint(address, info.Port));
    }

    private async Task AskTheirPeersAsync(IPEndPoint endPoint)
    {
        try
        {
            var list = await GetAsync<PeerAddress[]>(http, endPoint, BellProtocol.PeersPath, stop.Token);
            if (list is null) return;
            var unknown = list
                .Select(p => (Info: p.Peer?.Checked(), Ok: IPAddress.TryParse(p.Address, out var a), Address: a))
                .Where(p => p.Info is not null && p.Ok && p.Info.Id != options.Id && directory.Find(p.Info.Id) is not { Online: true })
                .Select(p => new IPEndPoint(p.Address!, p.Info!.Port));
            await Task.WhenAll(unknown.Select(e => HelloAsync(http, e, stop.Token)));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>A hello to each PC (each of its addresses until one answers), all at once.</summary>
    private Task AskAsync(IEnumerable<Peer> peers, CancellationToken token) =>
        Task.WhenAll(peers.Select(async peer =>
        {
            foreach (var endPoint in peer.EndPoints)
                if (await HelloAsync(http, endPoint, token) is not null) return;
        }));

    /// <summary>A hello to the addresses added by hand, unless a PC online already answers there.</summary>
    private async Task AskAddressesAsync(CancellationToken token)
    {
        List<string> list;
        lock (gate) list = addresses;
        foreach (var address in list)
        {
            var endPoints = await ResolveAsync(address, token);
            if (endPoints.Count == 0) Log($"Address added by hand \"{address}\": not found (name not resolved)");
            foreach (var endPoint in endPoints)
            {
                if (directory.IsOnlineAt(endPoint)) break;
                if (await HelloAsync(http, endPoint, token) is not null) break;
                Log($"Address added by hand \"{address}\": {endPoint} does not answer");
            }
        }
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

    /// <summary>"192.168.41.12", "192.168.41.12:53318", "PC-BUREAU" or "PC-BUREAU:53318", as typed in Settings.</summary>
    public static (string Host, int Port)? ParseAddress(string text, int defaultPort = BellProtocol.DefaultPort)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Contains(' ')) return null;
        int colon = text.LastIndexOf(':');
        if (colon < 0) return (text, defaultPort);
        if (colon == 0 || !int.TryParse(text[(colon + 1)..], out int port) || port is <= 0 or > 65535) return null;
        return (text[..colon], port);
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
        catch (SocketException) { return []; }
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
        if (info.Id != options.Id)
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
        if (ring?.From?.Checked() is not { } from || ring.Id.Length is 0 or > BellProtocol.MaxIdLength || from.Id == options.Id)
            return HttpResponseData.BadRequest;
        Heard(from, request.Remote);
        bool quiet = status == PeerStatus.DoNotDisturb;
        if (!rings.Add(ring.Id)) Log($"Ring {Short(ring.Id)} from {from.Name}: again (another address), shown once");
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
        if (reply?.From?.Checked() is not { } from || reply.RingId.Length is 0 or > BellProtocol.MaxIdLength || from.Id == options.Id
            || !Enum.IsDefined(reply.Kind))
            return HttpResponseData.BadRequest;
        Heard(from, request.Remote);
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
        if (answer?.Checked() is not { } info || info.Id == options.Id) return null;
        Heard(info, endPoint.Address);
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
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or IOException)
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
