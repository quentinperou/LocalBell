using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LocalBell.Core.Network;

/// <summary>
/// An IPv4 address of this PC on one of its network cards. <see cref="HasGateway"/>: the card leads somewhere (the
/// office network, most likely), unlike the Windows hotspot or a virtual machine's card.
/// </summary>
public sealed record LocalAddress(IPAddress Address, int PrefixLength, string CardName, bool HasGateway = false)
{
    /// <summary>The broadcast address of its network (192.168.1.255 for 192.168.1.20/24).</summary>
    public IPAddress Broadcast
    {
        get
        {
            uint address = LocalNetwork.ToUInt32(Address);
            uint hostBits = PrefixLength >= 32 ? 0 : uint.MaxValue >> PrefixLength;
            return LocalNetwork.ToAddress(address | hostBits);
        }
    }
}

/// <summary>The network cards of this PC, as the discovery uses them.</summary>
public static class LocalNetwork
{
    /// <summary>
    /// The IPv4 addresses of every card that is up, every one of them: a PC often has several (Ethernet, Wi-Fi, the
    /// Windows hotspot, virtual machines), and the one Windows picks for multicast is not always the one the
    /// colleagues are on. Loopback, tunnels and the 169.254 addresses of a card that got no address are left out.
    /// </summary>
    public static IReadOnlyList<LocalAddress> Addresses()
    {
        var list = new List<LocalAddress>();
        NetworkInterface[] cards;
        try { cards = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return list; }

        foreach (var card in cards)
        {
            if (card.OperationalStatus != OperationalStatus.Up
                || card.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties properties;
            try { properties = card.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            bool gateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                && !g.Address.Equals(IPAddress.Any));
            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) || IsLinkLocal(address))
                    continue;
                list.Add(new LocalAddress(address, unicast.PrefixLength is > 0 and <= 32 ? unicast.PrefixLength : 24, card.Name, gateway));
            }
        }
        return list;
    }

    /// <summary>The addresses to give a colleague: those of the cards with a gateway first.</summary>
    public static IReadOnlyList<IPAddress> AddressesToShare() =>
        Addresses().OrderByDescending(a => a.HasGateway).Select(a => a.Address).Distinct().ToList();

    /// <summary>169.254.0.0/16: the address Windows gives a card that found no DHCP server.</summary>
    public static bool IsLinkLocal(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [169, 254, ..];

    /// <summary>
    /// The addresses a scan asks, for one address of this PC: the other hosts of its network, but never more than
    /// the 254 of its /24 (a /16 network would be 65,000 requests): without its own address, the network address and
    /// the broadcast one.
    /// </summary>
    public static IEnumerable<IPAddress> Neighbours(IPAddress address, int prefixLength)
    {
        int prefix = Math.Clamp(prefixLength, 24, 30);
        uint mine = ToUInt32(address);
        uint mask = uint.MaxValue << (32 - prefix);
        uint network = mine & mask, broadcast = network | ~mask;
        for (uint host = network + 1; host < broadcast; host++)
            if (host != mine) yield return ToAddress(host);
    }

    internal static uint ToUInt32(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    internal static IPAddress ToAddress(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
