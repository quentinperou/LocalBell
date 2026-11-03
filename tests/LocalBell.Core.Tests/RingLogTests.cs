using System.Net;
using LocalBell.Core.History;
using LocalBell.Core.Peers;
using LocalBell.Core.Protocol;

namespace LocalBell.Core.Tests;

public class RingLogTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 13, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Peer Pc(string id, string name) =>
        new(id, name, PeerStatus.Available, new IPEndPoint(IPAddress.Loopback, 53318), true, "0.1.2");

    private static readonly Peer Caisse = Pc("caisse", "Caisse");
    private static readonly Peer Labo = Pc("labo", "Labo");

    private static IncomingRing Ring(string id, Peer from, DateTimeOffset at, bool quiet = false) =>
        new(id, from, "Client en caisse", false, quiet, at);

    [Fact]
    public void ARingReceived_KeepsItsReply()
    {
        var time = new ManualTime();
        var log = new RingLog(time: time);
        log.AddReceived(Ring("r1", Caisse, time.GetUtcNow()));
        log.Answer("r1", RingFate.Unanswered); // the bell stopped, the popup still open
        log.Answer("r1", RingFate.Replied, "J'arrive");
        log.Answer("r1", RingFate.Closed); // a reply is the last word

        var party = Assert.Single(Assert.Single(log.Rings).Parties);
        Assert.Equal(RingFate.Replied, party.Fate);
        Assert.Equal("J'arrive", party.Reply);
        Assert.Equal("caisse", party.Id);
    }

    [Fact]
    public void ClosedAfterNoAnswer_SaysItWasSeen_NotTheOtherWayRound()
    {
        var time = new ManualTime();
        var log = new RingLog(time: time);
        log.AddReceived(Ring("r1", Caisse, time.GetUtcNow()));
        log.Answer("r1", RingFate.Unanswered);
        log.Answer("r1", RingFate.Closed);
        log.Answer("r1", RingFate.Unanswered);
        Assert.Equal(RingFate.Closed, log.Rings[0].Parties[0].Fate);
    }

    [Fact]
    public void ARingInDoNotDisturb_IsMissed()
    {
        var time = new ManualTime();
        var log = new RingLog(time: time);
        log.AddReceived(Ring("r1", Caisse, time.GetUtcNow(), quiet: true));
        Assert.Equal(RingFate.Missed, log.Rings[0].Parties[0].Fate);
    }

    [Fact]
    public void AReplyBeforeItsRingIsLogged_IsKept()
    {
        var time = new ManualTime();
        var log = new RingLog(time: time);
        log.AddReply(new IncomingReply("s1", Labo, ReplyKind.Text, "2 min"));
        log.AddSent("Viens voir", false, [new RingOutcome("s1", Labo, RingResult.Rung)]);

        var party = log.Rings[0].Parties[0];
        Assert.Equal(RingFate.Replied, party.Fate);
        Assert.Equal("2 min", party.Reply);
        Assert.False(log.Rings[0].Received);
    }

    [Fact]
    public void Everyone_IsOneRing_EachPcWithItsOwnFate()
    {
        var time = new ManualTime();
        var log = new RingLog(time: time);
        log.AddSent("Livraison", true,
        [
            new RingOutcome("s1", Caisse, RingResult.Rung),
            new RingOutcome("s2", Labo, RingResult.Rung),
            new RingOutcome("s3", Pc("acc", "Accueil"), RingResult.Quiet),
            new RingOutcome("s4", Pc("bur", "BUREAU3"), RingResult.Failed),
        ]);
        log.AddReply(new IncomingReply("s1", Caisse, ReplyKind.Text, "J'arrive"));
        log.AddReply(new IncomingReply("s2", Labo, ReplyKind.Unanswered, ""));

        var ring = Assert.Single(log.Rings);
        Assert.True(ring.Everyone);
        Assert.Equal([RingFate.Replied, RingFate.Unanswered, RingFate.Quiet, RingFate.Failed], ring.Parties.Select(p => p.Fate));
    }

    [Fact]
    public void ARingWhoseEndWasNeverTold_ReadsAsUnanswered()
    {
        var time = new ManualTime();
        var log = new RingLog(time: time);
        log.AddReceived(Ring("r1", Caisse, time.GetUtcNow()));
        var ring = log.Rings[0];
        Assert.Equal(RingFate.Ringing, log.Effective(ring, ring.Parties[0]));
        time.Now += RingLog.RingingFor + TimeSpan.FromSeconds(1);
        Assert.Equal(RingFate.Unanswered, log.Effective(ring, ring.Parties[0]));
    }

    [Fact]
    public void TheDaysPastTheOnesKept_Go()
    {
        var time = new ManualTime();
        var log = new RingLog(time: time);
        var today = time.GetLocalNow();
        log.AddReceived(Ring("old", Caisse, today.AddDays(-8)));
        log.AddReceived(Ring("week", Caisse, today.AddDays(-6)));
        log.AddReceived(Ring("yesterday", Caisse, today.AddDays(-1)));
        log.AddReceived(Ring("today", Caisse, today));
        Assert.Equal(["today", "yesterday", "week"], log.Rings.Select(r => r.Parties[0].RingId));

        log.KeepDays = 1;
        Assert.Equal(["today"], log.Rings.Select(r => r.Parties[0].RingId));
    }

    [Fact]
    public void Saved_ThenLoaded()
    {
        string folder = Path.Combine(Path.GetTempPath(), "LocalBell.Tests." + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "history.json");
        try
        {
            var time = new ManualTime();
            var log = new RingLog(path, time);
            log.AddReceived(Ring("r1", Caisse, time.GetUtcNow()));
            log.Answer("r1", RingFate.Replied, "J'arrive");
            log.AddSent("", false, [new RingOutcome("s1", Labo, RingResult.Quiet)]);
            log.Save();

            var loaded = RingLog.Load(path, time);
            Assert.Equal(2, loaded.Rings.Count);
            Assert.Equal(RingFate.Quiet, loaded.Rings[0].Parties[0].Fate);
            Assert.Equal("Labo", loaded.Rings[0].Parties[0].Name);
            Assert.Equal("J'arrive", loaded.Rings[1].Parties[0].Reply);
            Assert.Equal("Client en caisse", loaded.Rings[1].Message);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ADamagedFile_GivesAnEmptyLog()
    {
        string path = Path.Combine(Path.GetTempPath(), "LocalBell.Tests." + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Empty(RingLog.Load(path).Rings);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
