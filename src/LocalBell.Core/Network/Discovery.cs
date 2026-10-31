using System.Net;
using System.Net.Sockets;
using LocalBell.Core.Protocol;

namespace LocalBell.Core.Network;

/// <summary>
/// The UDP part of the discovery: listens for the announcements of the other PCs, and sends this PC's on every
/// network card, to the multicast group and to the broadcast address of the card's network. Two ways, because some
/// Wi-Fi access points drop one or the other. Duplicates are harmless: a PC is known by its id.
/// </summary>
public sealed class Discovery : IDisposable
{
    private readonly int port;
    private readonly IPAddress group;
    private readonly Action<Announcement, IPAddress> received;
    private readonly CancellationTokenSource stop = new();
    private Socket? socket;

    public Discovery(int port, IPAddress group, Action<Announcement, IPAddress> received)
    {
        this.port = port;
        this.group = group;
        this.received = received;
    }

    /// <summary>
    /// Listens on the port, shared with other programs (ReuseAddress: a second LocalBell on the same PC, for a test,
    /// gets the multicast announcements too), and joins the group on every card. Throws a <see cref="SocketException"/>
    /// when the port cannot be had (held by another program, or in a range Windows reserves for Hyper-V, WSL or Docker);
    /// <see cref="Send"/> works all the same.
    /// </summary>
    public void Start()
    {
        var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = false };
        try
        {
            udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch
        {
            udp.Dispose();
            throw;
        }
        socket = udp;
        JoinGroup();
        _ = ReceiveAsync(udp, stop.Token);
    }

    /// <summary>
    /// Joins the group on every card, again on those already joined: after Wi-Fi drops and comes back with the same
    /// address, Windows may have dropped the membership.
    /// </summary>
    public void JoinGroup()
    {
        if (socket is null) return;
        foreach (var local in LocalNetwork.Addresses())
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, local.Address));
            }
            catch (SocketException)
            {
                // Already joined, or a card that went away meanwhile.
            }
        }
    }

    private async Task ReceiveAsync(Socket udp, CancellationToken token)
    {
        var buffer = new byte[4096];
        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try { result = await udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; } // ICMP "port unreachable" of an earlier send, among others

            var announcement = BellProtocol.Deserialize<Announcement>(buffer.AsSpan(0, result.ReceivedBytes));
            if (announcement is { App: BellProtocol.AppTag } && announcement.Protocol <= BellProtocol.Version)
                received(announcement, ((IPEndPoint)result.RemoteEndPoint).Address);
        }
    }

    /// <summary>
    /// Sends the announcement out of every card: a socket bound to the card's address, so that Windows uses that card
    /// for the multicast and the broadcast alike.
    /// </summary>
    public void Send(Announcement announcement)
    {
        byte[] datagram = BellProtocol.Serialize(announcement);
        foreach (var local in LocalNetwork.Addresses())
        {
            try
            {
                using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
                sender.Bind(new IPEndPoint(local.Address, 0));
                sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.Address.GetAddressBytes());
                sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                sender.SendTo(datagram, new IPEndPoint(group, port));
                sender.SendTo(datagram, new IPEndPoint(local.Broadcast, port));
            }
            catch (SocketException)
            {
                // A card going down, or a network that refuses broadcasts: the others still get it.
            }
        }
    }

    /// <summary>An answer to one PC only, when its server cannot be reached over HTTP.</summary>
    public void SendTo(Announcement announcement, IPAddress address)
    {
        try
        {
            using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sender.SendTo(BellProtocol.Serialize(announcement), new IPEndPoint(address, port));
        }
        catch (SocketException) { }
    }

    public void Dispose()
    {
        stop.Cancel();
        socket?.Dispose();
        stop.Dispose();
    }
}
