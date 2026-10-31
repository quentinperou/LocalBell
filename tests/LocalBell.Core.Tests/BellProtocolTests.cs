using System.Text;
using LocalBell.Core.Protocol;

namespace LocalBell.Core.Tests;

public class BellProtocolTests
{
    [Theory]
    [InlineData("  Caisse  ", 40, "Caisse")]
    [InlineData("Client\nen caisse", 40, "Client en caisse")]
    [InlineData("Atelier", 3, "Ate")]
    [InlineData("   ", 40, "")]
    [InlineData(null, 40, "")]
    public void Clean_RemovesControlCharactersAndCutsToTheLength(string? text, int max, string expected) =>
        Assert.Equal(expected, BellProtocol.Clean(text, max));

    [Fact]
    public void Clean_DoesNotSplitAnEmoji()
    {
        string text = "ab🔔"; // the bell is two UTF-16 characters
        Assert.Equal("ab", BellProtocol.Clean(text, 3));
    }

    [Theory]
    [InlineData("Ca\u200Bisse", "Caisse")]            // zero-width space
    [InlineData("\u202Eessiac", "essiac")]           // right-to-left override: shown reversed
    [InlineData("\u200B\u200D", "")]                 // invisible only: no name
    [InlineData("Client   en\u2028caisse", "Client en caisse")]
    public void Clean_DropsInvisibleCharactersAndRunsOfSpaces(string text, string expected) =>
        Assert.Equal(expected, BellProtocol.Clean(text, 40));

    [Fact]
    public void Checked_RefusesAPeerWithoutIdOrPort()
    {
        Assert.Null(new PeerInfo { Id = "", Name = "A", Port = 1 }.Checked());
        Assert.Null(new PeerInfo { Id = "x", Name = "A", Port = 0 }.Checked());
        Assert.Null(new PeerInfo { Id = new string('x', 65), Name = "A", Port = 1 }.Checked());
        Assert.Null(new PeerInfo { Id = "x", Name = "A", Port = 1, Status = (PeerStatus)7 }.Checked());
    }

    [Theory]
    [InlineData("8f4e2b1c9d0a4e7f8b2c3d4e5f6a7b8c", true)]
    [InlineData("test-a", true)]
    [InlineData("a\r\nForged log line", false)]
    [InlineData("a b", false)]
    [InlineData("é", false)]
    public void IsId_TakesLettersDigitsAndDashesOnly(string id, bool expected) =>
        Assert.Equal(expected, BellProtocol.IsId(id));

    [Fact]
    public void Checked_CleansTheNameAndNamesAPeerWithoutOne()
    {
        Assert.Equal("Caisse", new PeerInfo { Id = "x", Name = " Caisse\r\n", Port = 1 }.Checked()!.Name);
        Assert.Equal("?", new PeerInfo { Id = "x", Name = "", Port = 1 }.Checked()!.Name);
    }

    [Fact]
    public void Announcement_TravelsAsCamelCaseJson()
    {
        var announcement = new Announcement
        {
            Peer = new PeerInfo { Id = "id1", Name = "Caisse", Port = 53318, Status = PeerStatus.DoNotDisturb, Version = "0.1.0" },
            Announce = true,
        };
        string json = Encoding.UTF8.GetString(BellProtocol.Serialize(announcement));
        Assert.Contains("\"app\":\"LocalBell\"", json);
        Assert.Contains("\"status\":\"doNotDisturb\"", json);

        var back = BellProtocol.Deserialize<Announcement>(Encoding.UTF8.GetBytes(json));
        Assert.Equal(announcement.Peer, back!.Peer);
        Assert.True(back.Announce);
        Assert.False(back.Leaving);
    }

    [Fact]
    public void Deserialize_GivesNullForWhatIsNotJson() =>
        Assert.Null(BellProtocol.Deserialize<Announcement>("M-SEARCH * HTTP/1.1"u8));
}
