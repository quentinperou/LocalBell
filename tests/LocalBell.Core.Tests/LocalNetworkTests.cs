using System.Net;
using LocalBell.Core.Network;

namespace LocalBell.Core.Tests;

public class LocalNetworkTests
{
    [Fact]
    public void Neighbours_OfASlash24_AreTheOther253Hosts()
    {
        var list = LocalNetwork.Neighbours(IPAddress.Parse("192.168.40.156"), 24).ToList();
        Assert.Equal(253, list.Count);
        Assert.Equal(IPAddress.Parse("192.168.40.1"), list[0]);
        Assert.Equal(IPAddress.Parse("192.168.40.254"), list[^1]);
        Assert.DoesNotContain(IPAddress.Parse("192.168.40.156"), list);
    }

    [Fact]
    public void Neighbours_OfALargerNetwork_StayInTheSlash24()
    {
        var list = LocalNetwork.Neighbours(IPAddress.Parse("10.1.2.3"), 16).ToList();
        Assert.Equal(253, list.Count);
        Assert.All(list, a => Assert.StartsWith("10.1.2.", a.ToString()));
    }

    [Fact]
    public void Neighbours_OfASmallNetwork_AreItsHosts()
    {
        var list = LocalNetwork.Neighbours(IPAddress.Parse("192.168.1.9"), 29).ToList();
        Assert.Equal(["192.168.1.10", "192.168.1.11", "192.168.1.12", "192.168.1.13", "192.168.1.14"], list.Select(a => a.ToString()));
    }

    [Theory]
    [InlineData("192.168.40.156", 24, "192.168.40.255")]
    [InlineData("10.1.2.3", 16, "10.1.255.255")]
    [InlineData("192.168.1.9", 29, "192.168.1.15")]
    public void Broadcast_IsTheLastAddressOfTheNetwork(string address, int prefix, string expected) =>
        Assert.Equal(IPAddress.Parse(expected), new LocalAddress(IPAddress.Parse(address), prefix, "test").Broadcast);

    [Fact]
    public void IsLinkLocal_TellsTheAddressOfACardWithoutDhcp()
    {
        Assert.True(LocalNetwork.IsLinkLocal(IPAddress.Parse("169.254.112.170")));
        Assert.False(LocalNetwork.IsLinkLocal(IPAddress.Parse("192.168.137.1")));
    }
}
