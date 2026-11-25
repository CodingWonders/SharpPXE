// =====================================================================================
//  UdpHelpers.cs - creating UDP sockets that behave well on Windows
// =====================================================================================
//
//  DHCP and TFTP both run over UDP: single datagrams, with no connection, no delivery
//  guarantee and no ordering. Each protocol handles reliability itself with timeouts and
//  retries.
//
//  Two Windows details matter for a server:
//
//  1. WSAECONNRESET on UDP. If we send a datagram to a port where nothing listens, the
//     remote host returns an ICMP "port unreachable" message. Windows then makes the NEXT
//     receive on our socket fail with "connection reset", which makes no sense for UDP and
//     would break a server loop. The SIO_UDP_CONNRESET ioctl turns this off.
//
//  2. ExclusiveAddressUse. Without it, another program could bind the same port with
//     SO_REUSEADDR and receive our clients' packets. Exclusive use prevents that and also
//     makes a port conflict show up as a clear error at startup.
// =====================================================================================

using System.Net;
using System.Net.Sockets;

namespace SharpPXE.Net;

public static class UdpHelpers
{
    /// <summary>The control code from mstcpip.h: _WSAIOW(IOC_VENDOR, 12).</summary>
    const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);

    /// <summary>
    /// Creates a UDP socket bound to <paramref name="ip"/>:<paramref name="port"/>.
    /// On Windows a socket bound to an interface's unicast address also receives
    /// broadcasts arriving on that interface, and its own broadcasts leave through it.
    /// Port 0 means "pick any free port" (used for TFTP transfers).
    /// </summary>
    public static Socket Bind(IPAddress ip, int port, bool broadcast = false)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            s.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); // input value 0 = FALSE = don't report
            s.ExclusiveAddressUse = true;
        }
        // Sending to 255.255.255.255 needs SO_BROADCAST, otherwise the send fails.
        if (broadcast) s.EnableBroadcast = true;
        // A bigger receive buffer means fewer dropped packets when many clients boot at once.
        s.ReceiveBufferSize = 1 << 20;
        s.Bind(new IPEndPoint(ip, port));
        return s;
    }
}
