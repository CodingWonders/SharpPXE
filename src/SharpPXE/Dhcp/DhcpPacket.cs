// =====================================================================================
//  DhcpPacket.cs - reading and writing DHCP packets
// =====================================================================================
//
//  DHCP (RFC 2131) is an extension of the older BOOTP protocol (RFC 951). Every DHCP
//  message is a BOOTP message with a fixed 236-byte header, followed by a 4-byte
//  "magic cookie" and a variable list of options:
//
//   offset  size  field     meaning
//   ------  ----  --------  ---------------------------------------------------------
//        0     1  op        1 = BOOTREQUEST (client -> server), 2 = BOOTREPLY (server -> client)
//        1     1  htype     hardware type, 1 = Ethernet
//        2     1  hlen      hardware address length, 6 for Ethernet MACs
//        3     1  hops      incremented by DHCP relay agents
//        4     4  xid       transaction ID chosen by the client; replies must echo it
//        8     2  secs      seconds since the client started trying
//       10     2  flags     bit 15 = "broadcast": the client can't receive unicast yet
//       12     4  ciaddr    client IP, if it already has one
//       16     4  yiaddr    "your" IP: the address the server gives the client
//       20     4  siaddr    "next server": where to download the boot file from (TFTP)
//       24     4  giaddr    relay agent IP (0 if the packet was not relayed)
//       28    16  chaddr    client hardware (MAC) address, padded with zeros
//       44    64  sname     optional server host name (null-terminated ASCII)
//      108   128  file      boot file name (null-terminated ASCII)  <- what PXE downloads
//      236     4  cookie    99.130.83.99, marks the start of DHCP options
//      240     -  options   TLV list: [code][length][data...] ... [255 = end]
//
//  Options are "type-length-value" triplets. Code 0 is a single padding byte and code
//  255 ends the list; both have no length byte. Everything else is one code byte, one
//  length byte (0-255) and that many data bytes. All multi-byte numbers are big-endian
//  ("network byte order"), which is why the code uses BinaryPrimitives.*BigEndian.
//
//  The options that matter for network booting are listed in DhcpOption below.
// =====================================================================================

using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace SharpPXE.Dhcp;

/// <summary>
/// Value of option 53 (DHCP message type). A normal address lease is a four-step
/// exchange, often called DORA:
/// <code>
///   client                         server
///     |--- DISCOVER (broadcast) ---->|   "is there a DHCP server?"
///     |<-- OFFER --------------------|   "you can have 192.168.1.50"
///     |--- REQUEST (broadcast) ----->|   "I'll take 192.168.1.50 from server X"
///     |<-- ACK ----------------------|   "confirmed, it's yours for N seconds"
/// </code>
/// NAK refuses a REQUEST, RELEASE gives an address back, DECLINE reports an address
/// conflict, and INFORM asks for settings only (the client already has an address).
/// PXE reuses REQUEST/ACK (and sometimes INFORM) when it talks to a boot server on port 4011.
/// </summary>
public enum DhcpMessageType : byte
{
    Discover = 1, Offer = 2, Request = 3, Decline = 4, Ack = 5, Nak = 6, Release = 7, Inform = 8,
}

/// <summary>
/// DHCP option codes used by this server (RFC 2132, RFC 4578 for PXE, plus iPXE's own).
/// </summary>
public static class DhcpOption
{
    public const byte
        Pad = 0,              // filler byte, no length
        SubnetMask = 1,       // e.g. 255.255.255.0
        Router = 3,           // default gateway(s)
        Dns = 6,              // DNS server(s), 4 bytes each
        HostName = 12,
        DomainName = 15,
        VendorSpecific = 43,  // vendor-defined sub-options; PXE puts its own settings here
        RequestedIp = 50,     // client asks for a specific address (in DISCOVER/REQUEST)
        LeaseTime = 51,       // seconds
        MessageType = 53,     // see DhcpMessageType
        ServerId = 54,        // IP of the server sending the reply; the client echoes it back
        ParameterList = 55,   // which options the client would like to receive
        RenewalTime = 58,     // T1: when to renew with the same server (usually lease/2)
        RebindingTime = 59,   // T2: when to ask any server (usually lease*7/8)
        VendorClass = 60,     // "PXEClient:Arch:00007:UNDI:003016", "HTTPClient:...", "MSFT 5.0"...
        ClientId = 61,
        TftpServerName = 66,  // alternative to the siaddr / sname header fields
        BootFileName = 67,    // alternative to the 128-byte 'file' header field (for long names)
        UserClass = 77,       // iPXE sends "iPXE" here, which is how we recognise it
        ClientArch = 93,      // RFC 4578: client CPU / firmware type (BIOS, UEFI x64, ARM64...)
        ClientNdi = 94,       // RFC 4578: network interface (UNDI) version
        ClientGuid = 97,      // RFC 4578: machine UUID; PXE servers must echo it back
        IpxeEncap = 175,      // iPXE's encapsulated options; present only when iPXE is asking
        End = 255;            // end of options, no length
}

/// <summary>
/// A BOOTP/DHCP packet in memory. <see cref="Parse"/> turns received bytes into an object;
/// <see cref="Serialize"/> turns an object back into bytes to send.
/// </summary>
public sealed class DhcpPacket
{
    /// <summary>The 4 bytes after the BOOTP header that say "DHCP options follow" (RFC 1497).</summary>
    static readonly byte[] MagicCookie = { 99, 130, 83, 99 };

    // ---- fixed header fields (see the table at the top of the file) ----
    public byte Op = 1, HType = 1, HLen = 6, Hops;
    public uint Xid;
    public ushort Secs, Flags;
    public IPAddress CiAddr = IPAddress.Any, YiAddr = IPAddress.Any, SiAddr = IPAddress.Any, GiAddr = IPAddress.Any;
    public byte[] ChAddr = new byte[16];
    public string SName = "", File = "";

    /// <summary>
    /// Options, keyed by code. If an option appears several times in a packet, its pieces
    /// are joined in order, because RFC 3396 says that's how values longer than 255 bytes
    /// are split up.
    /// </summary>
    public readonly Dictionary<byte, byte[]> Options = new();

    /// <summary>The client set the broadcast flag: it can't receive unicast until it has an address.</summary>
    public bool Broadcast => (Flags & 0x8000) != 0;

    /// <summary>Option 53, or null for a plain BOOTP packet (which has no message type).</summary>
    public DhcpMessageType? MessageType =>
        Options.TryGetValue(DhcpOption.MessageType, out var v) && v.Length == 1 ? (DhcpMessageType)v[0] : null;

    /// <summary>MAC address as plain lowercase hex ("00155d010203"), used as a dictionary key.</summary>
    public string Mac => Convert.ToHexString(ChAddr, 0, Math.Min((int)HLen, 16)).ToLowerInvariant();

    /// <summary>MAC address for humans and log lines ("00-15-5d-01-02-03").</summary>
    public string MacPretty => string.Join("-", Enumerable.Range(0, Math.Min((int)HLen, 16)).Select(i => ChAddr[i].ToString("x2")));

    /// <summary>
    /// Option 60. PXE firmware always sends a string that starts with "PXEClient";
    /// UEFI HTTP Boot sends one that starts with "HTTPClient".
    /// </summary>
    public string? VendorClass => GetString(DhcpOption.VendorClass);

    /// <summary>
    /// Option 93 (RFC 4578 + IANA registry): 0 = BIOS, 6 = UEFI IA32, 7/9 = UEFI x64,
    /// 11 = UEFI ARM64, 16 = UEFI x64 HTTP Boot, 19 = UEFI ARM64 HTTP Boot. Null if absent.
    /// </summary>
    public ushort? ClientArch =>
        Options.TryGetValue(DhcpOption.ClientArch, out var v) && v.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(v) : null;

    public string? GetString(byte code) =>
        Options.TryGetValue(code, out var v) ? Encoding.ASCII.GetString(v).TrimEnd('\0') : null;

    public IPAddress? GetIp(byte code) =>
        Options.TryGetValue(code, out var v) && v.Length == 4 ? new IPAddress(v) : null;

    /// <summary>
    /// Decodes a received UDP payload. Returns null for anything that isn't a well-formed
    /// DHCP packet. Data arriving off the network can be anything, so every length is checked
    /// before it is used.
    /// </summary>
    public static DhcpPacket? Parse(ReadOnlySpan<byte> b)
    {
        // 236-byte header + 4-byte cookie is the minimum; without the cookie it's plain BOOTP.
        if (b.Length < 240) return null;
        if (!b.Slice(236, 4).SequenceEqual(MagicCookie)) return null;

        var p = new DhcpPacket
        {
            Op = b[0], HType = b[1], HLen = b[2], Hops = b[3],
            Xid = BinaryPrimitives.ReadUInt32BigEndian(b[4..]),
            Secs = BinaryPrimitives.ReadUInt16BigEndian(b[8..]),
            Flags = BinaryPrimitives.ReadUInt16BigEndian(b[10..]),
            CiAddr = new IPAddress(b.Slice(12, 4)),
            YiAddr = new IPAddress(b.Slice(16, 4)),
            SiAddr = new IPAddress(b.Slice(20, 4)),
            GiAddr = new IPAddress(b.Slice(24, 4)),
            ChAddr = b.Slice(28, 16).ToArray(),
            SName = ReadCString(b.Slice(44, 64)),
            File = ReadCString(b.Slice(108, 128)),
        };
        if (p.HLen > 16) p.HLen = 16; // chaddr only has room for 16 bytes

        // Walk the option list: [code][len][data...] until code 255 or the end of the packet.
        int i = 240;
        while (i < b.Length)
        {
            byte code = b[i++];
            if (code == DhcpOption.Pad) continue;   // single byte, no length
            if (code == DhcpOption.End) break;      // single byte, no length
            if (i >= b.Length) return null;          // truncated: code without length
            int len = b[i++];
            if (i + len > b.Length) return null;     // truncated: length runs past the packet
            var data = b.Slice(i, len).ToArray();
            // RFC 3396: a repeated option means "the value continues", so join the pieces.
            p.Options[code] = p.Options.TryGetValue(code, out var prev) ? [.. prev, .. data] : data;
            i += len;
        }
        return p;
    }

    /// <summary>Reads a C-style string: bytes up to the first zero.</summary>
    static string ReadCString(ReadOnlySpan<byte> s)
    {
        int z = s.IndexOf((byte)0);
        return Encoding.ASCII.GetString(z < 0 ? s : s[..z]);
    }

    /// <summary>Encodes the packet into bytes ready to send with UDP.</summary>
    public byte[] Serialize()
    {
        using var ms = new MemoryStream(576);

        // The fixed header, built on the stack and written in one go.
        Span<byte> hdr = stackalloc byte[240];
        hdr.Clear();
        hdr[0] = Op; hdr[1] = HType; hdr[2] = HLen; hdr[3] = Hops;
        BinaryPrimitives.WriteUInt32BigEndian(hdr[4..], Xid);
        BinaryPrimitives.WriteUInt16BigEndian(hdr[8..], Secs);
        BinaryPrimitives.WriteUInt16BigEndian(hdr[10..], Flags);
        CiAddr.TryWriteBytes(hdr.Slice(12, 4), out _);
        YiAddr.TryWriteBytes(hdr.Slice(16, 4), out _);
        SiAddr.TryWriteBytes(hdr.Slice(20, 4), out _);
        GiAddr.TryWriteBytes(hdr.Slice(24, 4), out _);
        ChAddr.AsSpan(0, 16).CopyTo(hdr.Slice(28, 16));
        WriteCString(hdr.Slice(44, 64), SName);
        WriteCString(hdr.Slice(108, 128), File);
        MagicCookie.CopyTo(hdr[236..]);
        ms.Write(hdr);

        // Option 53 goes first. The RFC doesn't require it, but some firmware expects it.
        if (Options.TryGetValue(DhcpOption.MessageType, out var mt)) WriteOption(ms, DhcpOption.MessageType, mt);
        foreach (var (code, data) in Options)
            if (code != DhcpOption.MessageType) WriteOption(ms, code, data);
        ms.WriteByte(DhcpOption.End);

        // BOOTP packets were 300 bytes minimum. Some old PXE ROMs still drop anything shorter.
        while (ms.Length < 300) ms.WriteByte(0);
        return ms.ToArray();
    }

    /// <summary>Writes one option, split into 255-byte pieces if needed (RFC 3396).</summary>
    static void WriteOption(Stream s, byte code, byte[] data)
    {
        int off = 0;
        do
        {
            int n = Math.Min(255, data.Length - off);
            s.WriteByte(code);
            s.WriteByte((byte)n);
            s.Write(data, off, n);
            off += n;
        } while (off < data.Length);
    }

    /// <summary>Writes a string into a fixed-size field, always leaving room for the final zero.</summary>
    static void WriteCString(Span<byte> dest, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        bytes.AsSpan(0, Math.Min(bytes.Length, dest.Length - 1)).CopyTo(dest);
    }

    /// <summary>
    /// Starts a reply to this request. A reply must copy xid (so the client can match it to
    /// its request), chaddr (so the client knows the reply is for it), flags and giaddr (so a
    /// relay agent can forward it back).
    /// </summary>
    public DhcpPacket CreateReply(DhcpMessageType type)
    {
        var r = new DhcpPacket
        {
            Op = 2, HType = HType, HLen = HLen, Xid = Xid, Flags = Flags, GiAddr = GiAddr,
            ChAddr = (byte[])ChAddr.Clone(),
        };
        r.Options[DhcpOption.MessageType] = [(byte)type];
        return r;
    }

    // Small helpers for setting typed option values.
    public void SetIp(byte code, IPAddress ip) => Options[code] = ip.GetAddressBytes();
    public void SetString(byte code, string s) => Options[code] = Encoding.ASCII.GetBytes(s);
    public void SetUInt32(byte code, uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        Options[code] = b;
    }
}
