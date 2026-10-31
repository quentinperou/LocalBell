using System.Net;
using LocalBell.Core.Peers;
using LocalBell.Core.Protocol;

namespace LocalBell.Core.Tests;

public class PeerDirectoryTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 14, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static PeerInfo Info(string id, string name, PeerStatus status = PeerStatus.Available) =>
        new() { Id = id, Name = name, Port = 53318, Status = status };

    private static readonly IPAddress A = IPAddress.Parse("192.168.40.10");
    private static readonly IPAddress B = IPAddress.Parse("192.168.41.10");

    [Fact]
    public void Seen_TellsWhatChanged()
    {
        var directory = new PeerDirectory(new ManualTime());
        Assert.Equal(SeenChange.Appeared, directory.Seen(Info("1", "Atelier"), A));
        Assert.Equal(SeenChange.None, directory.Seen(Info("1", "Atelier"), A));
        Assert.Equal(SeenChange.Changed, directory.Seen(Info("1", "Atelier", PeerStatus.DoNotDisturb), A));
        Assert.Equal(SeenChange.Changed, directory.Seen(Info("1", "Atelier 2", PeerStatus.DoNotDisturb), A));
    }

    [Fact]
    public void AFullDirectory_MakesRoomFromTheOldestOfflineUnverifiedPc()
    {
        var time = new ManualTime();
        var directory = new PeerDirectory(time, capacity: 3);
        directory.Remember(Info("known", "Atelier"), new IPEndPoint(A, 53318)); // offline, verified (an earlier run)
        directory.Seen(Info("junk1", "?"), A);
        time.Now += TimeSpan.FromMinutes(1);
        directory.Seen(Info("junk2", "?"), A);
        time.Now += TimeSpan.FromMinutes(5);
        directory.Expire(TimeSpan.FromSeconds(100)); // all offline now

        Assert.Equal(SeenChange.Appeared, directory.Seen(Info("new", "Caisse"), B));
        Assert.Equal(["junk2", "known", "new"], directory.All().Select(p => p.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AFullDirectoryOfPcsOnline_TakesNoNewcomer()
    {
        var directory = new PeerDirectory(new ManualTime(), capacity: 2);
        directory.Seen(Info("1", "Atelier"), A);
        directory.Seen(Info("2", "Caisse"), A);
        Assert.Equal(SeenChange.None, directory.Seen(Info("3", "Bureau"), B));
        Assert.Null(directory.Find("3"));
    }

    [Fact]
    public void Verified_ComesFromAnAnswer_OrFromAnEarlierRun()
    {
        var directory = new PeerDirectory(new ManualTime());
        directory.Seen(Info("1", "Atelier"), A);
        directory.Remember(Info("2", "Caisse"), new IPEndPoint(B, 53318));
        Assert.False(directory.Find("1")!.Verified);
        Assert.True(directory.Find("2")!.Verified);

        directory.Confirm("1");
        Assert.True(directory.Find("1")!.Verified);
    }

    [Fact]
    public void Seen_AtAnotherAddress_KeepsBothNewestFirst()
    {
        var directory = new PeerDirectory(new ManualTime());
        directory.Seen(Info("1", "Atelier"), A);
        Assert.Equal(SeenChange.None, directory.Seen(Info("1", "Atelier"), B));
        Assert.Equal([new IPEndPoint(B, 53318), new IPEndPoint(A, 53318)], directory.Find("1")!.EndPoints);
    }

    [Fact]
    public void Expire_PutsOfflineThePcsNotHeardFrom_AndTheyComeBack()
    {
        var time = new ManualTime();
        var directory = new PeerDirectory(time);
        directory.Seen(Info("1", "Atelier"), A);
        time.Now += TimeSpan.FromSeconds(99);
        Assert.False(directory.Expire(TimeSpan.FromSeconds(100)));
        time.Now += TimeSpan.FromSeconds(2);
        Assert.True(directory.Expire(TimeSpan.FromSeconds(100)));
        Assert.False(directory.Find("1")!.Online);
        Assert.Equal(SeenChange.Appeared, directory.Seen(Info("1", "Atelier"), A));
    }

    [Fact]
    public void Left_PutsOfflineOnce()
    {
        var directory = new PeerDirectory(new ManualTime());
        directory.Seen(Info("1", "Atelier"), A);
        Assert.True(directory.Left("1"));
        Assert.False(directory.Left("1"));
        Assert.False(directory.Left("unknown"));
    }

    [Fact]
    public void Remember_ListsAPcOfflineUntilHeardFrom()
    {
        var directory = new PeerDirectory(new ManualTime());
        Assert.True(directory.Remember(Info("1", "Réserve"), new IPEndPoint(A, 53318)));
        Assert.False(directory.Find("1")!.Online);
        Assert.Single(directory.Quiet(TimeSpan.FromSeconds(45)));
        Assert.Equal(SeenChange.Appeared, directory.Seen(Info("1", "Réserve"), A));
    }

    [Fact]
    public void All_ListsTheOnlinePcsFirst_ThenByName()
    {
        var directory = new PeerDirectory(new ManualTime());
        directory.Seen(Info("1", "Labo photo"), A);
        directory.Seen(Info("2", "atelier"), A);
        directory.Remember(Info("3", "Accueil"), new IPEndPoint(B, 53318));
        Assert.Equal(["atelier", "Labo photo", "Accueil"], directory.All().Select(p => p.Name));
    }

    [Fact]
    public void IsOnlineAt_KnowsTheAddressesOfThePcsOnline()
    {
        var directory = new PeerDirectory(new ManualTime());
        directory.Seen(Info("1", "Atelier"), A);
        Assert.True(directory.IsOnlineAt(new IPEndPoint(A, 53318)));
        Assert.False(directory.IsOnlineAt(new IPEndPoint(A, 53319)));
        directory.Left("1");
        Assert.False(directory.IsOnlineAt(new IPEndPoint(A, 53318)));
    }
}
