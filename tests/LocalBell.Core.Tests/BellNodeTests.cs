using System.Net;
using LocalBell.Core.Peers;
using LocalBell.Core.Protocol;

namespace LocalBell.Core.Tests;

/// <summary>
/// Several LocalBell on this PC, on 127.0.0.1 only and without the UDP discovery: nothing leaves the PC. They find each
/// other through the addresses added by hand and through the PCs each one knows.
/// </summary>
public class BellNodeTests
{
    private static NodeOptions Options(string id, string name, params string[] addresses) => new()
    {
        Id = id,
        Name = name,
        Version = "test",
        BindAddress = IPAddress.Loopback,
        ServerPort = 0,
        Discovery = false,
        Addresses = addresses,
    };

    private static async Task<BellNode> StartAsync(string id, string name, params string[] addresses)
    {
        var node = new BellNode(Options(id, name, addresses));
        await node.StartAsync();
        return node;
    }

    private static string AddressOf(BellNode node) => $"127.0.0.1:{node.Port}";

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until) Assert.Fail("Never happened: " + what);
            await Task.Delay(20);
        }
    }

    private static Peer PeerOf(BellNode node, string id) => node.Peers.Single(p => p.Id == id);

    [Fact]
    public async Task TwoPcs_FindEachOther_ThroughAnAddressAddedByHand()
    {
        await using var atelier = await StartAsync("a", "Atelier");
        await using var caisse = await StartAsync("c", "Caisse", AddressOf(atelier));

        Assert.True(PeerOf(caisse, "a") is { Name: "Atelier", Online: true });
        Assert.True(PeerOf(atelier, "c") is { Name: "Caisse", Online: true });
        Assert.DoesNotContain(atelier.Peers, p => p.Id == "a");
    }

    [Fact]
    public async Task Ring_ArrivesWithItsMessage_AndTheReplyComesBack()
    {
        await using var atelier = await StartAsync("a", "Atelier");
        await using var caisse = await StartAsync("c", "Caisse", AddressOf(atelier));
        var rung = new TaskCompletionSource<IncomingRing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replied = new TaskCompletionSource<IncomingReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        atelier.RingReceived += (_, ring) => rung.TrySetResult(ring);
        caisse.ReplyReceived += (_, reply) => replied.TrySetResult(reply);

        var outcome = await caisse.RingAsync(PeerOf(caisse, "a"), "Client\nen caisse");
        Assert.Equal(RingResult.Rung, outcome.Result);

        var ring = await rung.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((outcome.RingId, "c", "Caisse", "Client en caisse", false, false),
            (ring.Id, ring.From.Id, ring.From.Name, ring.Message, ring.Quiet, ring.Everyone));

        Assert.True(await atelier.ReplyAsync(ring, "J'arrive"));
        var reply = await replied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((outcome.RingId, "a", ReplyKind.Text, "J'arrive"), (reply.RingId, reply.From.Id, reply.Kind, reply.Text));
    }

    [Theory]
    [InlineData(ReplyKind.Closed)]
    [InlineData(ReplyKind.Unanswered)]
    public async Task TheCaller_IsToldWhenItNoLongerRings(ReplyKind kind)
    {
        await using var atelier = await StartAsync("a", "Atelier");
        await using var caisse = await StartAsync("c", "Caisse", AddressOf(atelier));
        var rung = new TaskCompletionSource<IncomingRing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var told = new TaskCompletionSource<IncomingReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        atelier.RingReceived += (_, ring) => rung.TrySetResult(ring);
        caisse.ReplyReceived += (_, reply) => told.TrySetResult(reply);

        var outcome = await caisse.RingAsync(PeerOf(caisse, "a"), null);
        Assert.True(await atelier.ReplyAsync(await rung.Task.WaitAsync(TimeSpan.FromSeconds(5)), "ignored", kind));
        var reply = await told.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((outcome.RingId, kind, ""), (reply.RingId, reply.Kind, reply.Text));
    }

    [Fact]
    public async Task DoNotDisturb_KeepsTheRingQuiet_AndTheOthersSeeIt()
    {
        await using var accueil = await StartAsync("a", "Accueil");
        await using var caisse = await StartAsync("c", "Caisse", AddressOf(accueil));
        var rung = new TaskCompletionSource<IncomingRing>(TaskCreationOptions.RunContinuationsAsynchronously);
        accueil.RingReceived += (_, ring) => rung.TrySetResult(ring);

        accueil.SetStatus(PeerStatus.DoNotDisturb);
        await Eventually(() => PeerOf(caisse, "a").Status == PeerStatus.DoNotDisturb, "Caisse sees Accueil in do not disturb");

        var outcome = await caisse.RingAsync(PeerOf(caisse, "a"), null);
        Assert.Equal(RingResult.Quiet, outcome.Result);
        Assert.True((await rung.Task.WaitAsync(TimeSpan.FromSeconds(5))).Quiet);
    }

    [Fact]
    public async Task Rename_IsSeenByTheOthers()
    {
        await using var atelier = await StartAsync("a", "Atelier");
        await using var caisse = await StartAsync("c", "Caisse", AddressOf(atelier));
        caisse.Rename("Caisse 2");
        await Eventually(() => PeerOf(atelier, "c").Name == "Caisse 2", "Atelier sees the new name");
    }

    [Fact]
    public async Task OneLink_JoinsEveryPc_ThroughThePcsEachOneKnows()
    {
        // Bureau knows Atelier, Caisse knows Bureau only: Caisse learns Atelier from Bureau, and Atelier learns Caisse.
        await using var atelier = await StartAsync("a", "Atelier");
        await using var bureau = await StartAsync("b", "Bureau", AddressOf(atelier));
        await using var caisse = await StartAsync("c", "Caisse", AddressOf(bureau));

        await Eventually(() => caisse.Peers.Any(p => p is { Id: "a", Online: true }), "Caisse knows Atelier");
        await Eventually(() => atelier.Peers.Any(p => p is { Id: "c", Online: true }), "Atelier knows Caisse");
    }

    [Fact]
    public async Task RingEveryone_RingsEachPcOnline()
    {
        await using var atelier = await StartAsync("a", "Atelier");
        await using var bureau = await StartAsync("b", "Bureau", AddressOf(atelier));
        await using var caisse = await StartAsync("c", "Caisse", AddressOf(atelier), AddressOf(bureau));
        int count = 0;
        atelier.RingReceived += (_, r) => { if (r.Everyone) Interlocked.Increment(ref count); };
        bureau.RingReceived += (_, r) => { if (r.Everyone) Interlocked.Increment(ref count); };

        var outcomes = await caisse.RingEveryoneAsync("Réunion");
        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal(RingResult.Rung, o.Result));
        await Eventually(() => count == 2, "both rung");
    }

    [Fact]
    public async Task APcThatCloses_IsShownOfflineAtOnce()
    {
        await using var atelier = await StartAsync("a", "Atelier");
        var caisse = await StartAsync("c", "Caisse", AddressOf(atelier));
        Assert.True(PeerOf(atelier, "c").Online);

        await caisse.DisposeAsync();
        await Eventually(() => !PeerOf(atelier, "c").Online, "Caisse offline");
    }

    [Fact]
    public async Task Ring_ToAPcThatIsGone_Fails()
    {
        await using var caisse = await StartAsync("c", "Caisse");
        var atelier = await StartAsync("a", "Atelier", AddressOf(caisse));
        var gone = PeerOf(caisse, "a");
        await atelier.DisposeAsync();

        Assert.Equal(RingResult.Failed, (await caisse.RingAsync(gone, null)).Result);
    }

    [Fact]
    public async Task Probe_FindsThePcAtAnAddress_OrNothing()
    {
        await using var atelier = await StartAsync("a", "Atelier");
        await using var caisse = await StartAsync("c", "Caisse");

        Assert.Equal("a", (await caisse.ProbeAsync(AddressOf(atelier)))?.Id);
        Assert.Null(await caisse.ProbeAsync("127.0.0.1:1"));
    }

    [Theory]
    [InlineData("192.168.41.12", "192.168.41.12", 53318)]
    [InlineData(" 192.168.41.12:5000 ", "192.168.41.12", 5000)]
    [InlineData("PC-BUREAU", "PC-BUREAU", 53318)]
    [InlineData("PC-BUREAU:53319", "PC-BUREAU", 53319)]
    public void ParseAddress_ReadsWhatIsTypedInSettings(string text, string host, int port) =>
        Assert.Equal((host, port), BellNode.ParseAddress(text));

    [Theory]
    [InlineData("")]
    [InlineData("PC BUREAU")]
    [InlineData("192.168.41.12:")]
    [InlineData("192.168.41.12:70000")]
    [InlineData(":53318")]
    public void ParseAddress_RefusesTheRest(string text) => Assert.Null(BellNode.ParseAddress(text));
}
