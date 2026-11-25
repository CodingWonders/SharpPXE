// =====================================================================================
//  DhcpServer.cs - DHCP, proxyDHCP and the PXE boot server (port 4011)
// =====================================================================================
//
//  HOW PXE FINDS ITS BOOT FILE
//  ---------------------------
//  PXE (Preboot eXecution Environment, Intel PXE spec 2.1) is the network stack in a
//  computer's firmware. When a machine network-boots it needs two things:
//    1. an IP address (from a DHCP server), and
//    2. the name of a boot file and the server to download it from (siaddr + file).
//
//  One DHCP server can provide both. In most networks, though, the router or a Windows
//  DHCP server hands out addresses and nobody wants to reconfigure it. "proxyDHCP" (part of
//  the PXE spec) solves this: a second server answers the same DISCOVER broadcast, offers
//  NO address (yiaddr = 0.0.0.0), and supplies only the boot information. The firmware
//  takes the address from the real DHCP server and the boot file from the proxy.
//
//    PXE client              DHCP server (router)          SharpPXE (proxyDHCP)
//       |--- DISCOVER (bcast, UDP 67) ---->|---------------------->|
//       |<-- OFFER  yiaddr=192.168.1.50 ---|                       |
//       |<-- OFFER  yiaddr=0, file=ipxe.efi, option 60 "PXEClient" |
//       |--- REQUEST 192.168.1.50 -------->|                       |
//       |<-- ACK --------------------------|                       |
//       |--- REQUEST (unicast, UDP 4011) -------------------------->|   many UEFI firmwares
//       |<-- ACK  file=ipxe.efi, siaddr=SharpPXE ------------------|   and iPXE do this step
//       |--- TFTP read request "ipxe.efi" (UDP 69) ---------------->|
//
//  The 4011 step comes from the PXE spec's "boot server discovery". After getting an
//  address, the client asks the boot server directly by unicast. We answer it with the same
//  boot information.
//
//  WHICH SOCKET RECEIVES WHAT
//  --------------------------
//  The client has no IP address yet, so it sends from 0.0.0.0:68 to 255.255.255.255:67,
//  and replies must be broadcast back to 255.255.255.255:68. We open one socket per
//  local IPv4 address (e.g. 192.168.1.10:67). On Windows such a socket also receives
//  broadcasts that arrive on that adapter, and broadcasts sent from it leave through that
//  adapter. That tells us which of our addresses to advertise as "next server", which
//  matters on PCs with Hyper-V, VMware or Wi-Fi adapters.
//
//  BackgroundService (Microsoft.Extensions.Hosting) is a long-running task that the .NET
//  generic host starts with the app and cancels on Ctrl+C or when the service stops.
// =====================================================================================

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using SharpPXE.Net;

namespace SharpPXE.Dhcp;

/// <summary>
/// DHCP (port 67) and PXE boot server / proxyDHCP (port 4011). In proxy mode it never hands
/// out addresses. It only adds boot information alongside an existing DHCP server, like
/// Tiny PXE Server's proxyDHCP mode.
/// </summary>
/// <remarks>
/// <c>Config cfg, BootPolicy policy</c> is a C# 12 "primary constructor". The dependency
/// injection container (set up in Program.cs) supplies both when it creates the service.
/// </remarks>
public sealed class DhcpServer(Config cfg, BootPolicy policy) : BackgroundService
{
    /// <summary>Where to send replies to clients that don't have an address yet.</summary>
    static readonly IPEndPoint BroadcastClient = new(IPAddress.Broadcast, 68);

    /// <summary>Address pool; only created in full DHCP mode.</summary>
    LeasePool? _pool;

    /// <summary>Called once by the host at startup. Opens the sockets and runs one receive loop per socket.</summary>
    protected override Task ExecuteAsync(CancellationToken ct)
    {
        if (cfg.Mode == DhcpMode.Off) { Log.Info("DHCP: disabled (mode=off)"); return Task.CompletedTask; }

        var ifaces = cfg.ResolveInterfaces();
        if (cfg.Mode == DhcpMode.Full)
        {
            // Never lease out our own addresses or the router's, even if they fall inside the pool.
            var exclude = ifaces.Select(i => i.Ip).ToList();
            if (cfg.Router is not null) exclude.Add(cfg.Router);
            _pool = new LeasePool(cfg.PoolStart!, cfg.PoolEnd!, exclude);
        }

        // Two listeners per interface: 67 for DHCP/proxyDHCP, 4011 for PXE boot-server requests.
        var loops = new List<Task>();
        foreach (var (ip, mask) in ifaces)
        {
            loops.Add(StartListener(ip, mask, 67, ct));
            loops.Add(StartListener(ip, mask, 4011, ct));
        }
        return Task.WhenAll(loops);
    }

    Task StartListener(IPAddress ip, IPAddress mask, int port, CancellationToken ct)
    {
        Socket sock;
        try { sock = UdpHelpers.Bind(ip, port, broadcast: true); }
        catch (SocketException ex)
        {
            // Most often another DHCP service (Windows DHCP Server, WDS, Internet Connection
            // Sharing) already owns the port on this address.
            Log.Error($"DHCP: cannot bind {ip}:{port} ({ex.SocketErrorCode}). Is another DHCP/WDS/PXE server running on this machine?");
            return Task.CompletedTask;
        }
        Log.Info($"DHCP: listening on {ip}:{port} ({(port == 67 ? (cfg.Mode == DhcpMode.Proxy ? "proxyDHCP" : "DHCP") : "PXE boot server")})");
        return Task.Run(() => ReceiveLoop(sock, ip, mask, port, ct), ct);
    }

    /// <summary>
    /// Waits for a packet, works out the reply, sends it, repeats. DHCP is one request, one
    /// reply, and no session state between packets (except the lease pool), so one loop per
    /// socket is enough.
    /// </summary>
    async Task ReceiveLoop(Socket sock, IPAddress ip, IPAddress mask, int port, CancellationToken ct)
    {
        using var _ = sock;
        var buf = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await sock.ReceiveFromAsync(buf, SocketFlags.None, any, ct); }
            catch (OperationCanceledException) { break; }                       // shutting down
            catch (SocketException ex) { Log.Debug($"DHCP {ip}:{port} receive error {ex.SocketErrorCode}"); continue; }

            try
            {
                var req = DhcpPacket.Parse(buf.AsSpan(0, r.ReceivedBytes));
                // Op 1 = request from a client. Ignore replies from other DHCP servers.
                if (req is null || req.Op != 1) continue;
                var remote = (IPEndPoint)r.RemoteEndPoint;
                var (reply, dest) = port == 67 ? Handle67(req, ip, mask) : Handle4011(req, ip, remote);
                if (reply is not null && dest is not null)
                    await sock.SendToAsync(reply.Serialize(), SocketFlags.None, dest, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad packet must never stop the server.
                Log.Error($"DHCP {ip}:{port}: {ex.Message}");
            }
        }
    }

    // ---- port 67 ----------------------------------------------------------------------------

    /// <summary>
    /// Handles a packet on the standard DHCP port. Returns the reply and where to send it, or
    /// (null, null) to stay silent. Staying silent is normal: in proxy mode we ignore every
    /// non-PXE device on the network.
    /// </summary>
    (DhcpPacket?, IPEndPoint?) Handle67(DhcpPacket req, IPAddress ip, IPAddress mask)
    {
        var type = req.MessageType;
        if (type is null) return (null, null); // plain BOOTP (no option 53): not supported
        bool pxe = BootPolicy.IsPxeOrHttpClient(req);

        Log.Debug($"DHCP {ip}: {type} from {req.MacPretty} vc='{req.VendorClass}' arch={BootPolicy.ArchName(req.ClientArch)}");

        // ---- proxyDHCP: answer only PXE/HTTP-boot DISCOVERs, with boot info and no address.
        if (cfg.Mode == DhcpMode.Proxy)
        {
            if (type != DhcpMessageType.Discover || !pxe) return (null, null);
            var offer = BuildBootReply(req, DhcpMessageType.Offer, ip, out var d);
            if (offer is null) return (null, null);
            Log.Info($"proxyDHCP {ip}: OFFER to {req.MacPretty} ({d!.ClientKind}) -> {d.File}");
            return (offer, ReplyDestination(req, offer));
        }

        // ---- full DHCP: a normal DHCP server, plus boot info for PXE clients.
        // Only serve the subnet the pool is in. A relayed request (giaddr set) came from a
        // router's "ip helper" for another subnet, so it's always ours to answer.
        bool relayed = !req.GiAddr.Equals(IPAddress.Any);
        if (!relayed && !SameSubnet(ip, cfg.PoolStart!, mask)) return (null, null);
        var pool = _pool!;
        var lease = TimeSpan.FromSeconds(cfg.LeaseSeconds);

        switch (type)
        {
            case DhcpMessageType.Discover:
            {
                // Reserve an address for a short time and offer it.
                var addr = pool.Offer(req.Mac, req.GetIp(DhcpOption.RequestedIp));
                if (addr is null) { Log.Warn($"DHCP: pool exhausted, no offer for {req.MacPretty}"); return (null, null); }
                var offer = BuildAddressReply(req, DhcpMessageType.Offer, ip, addr, pxe, out var d);
                Log.Info($"DHCP {ip}: OFFER {addr} to {req.MacPretty}" + (d is null ? "" : $" ({d.ClientKind}) -> {d.File}"));
                return (offer, ReplyDestination(req, offer));
            }
            case DhcpMessageType.Request:
            {
                // The REQUEST is broadcast, so every server sees it. Option 54 says which
                // server's offer the client picked. If it wasn't ours, free our reservation.
                var sid = req.GetIp(DhcpOption.ServerId);
                if (sid is not null && !sid.Equals(ip)) { pool.Release(req.Mac); return (null, null); }

                // The address comes from option 50 (after an OFFER) or ciaddr (when renewing).
                var want = req.GetIp(DhcpOption.RequestedIp) ?? req.CiAddr;
                if (want.Equals(IPAddress.Any)) return (null, null);
                if (!pool.Commit(req.Mac, want, lease))
                {
                    // Address taken or not ours. NAK only if we're responsible for it. Otherwise
                    // a client renewing a lease from another server would be kicked off the network.
                    if (sid is null && !pool.InPool(want)) return (null, null);
                    var nak = req.CreateReply(DhcpMessageType.Nak);
                    nak.SetIp(DhcpOption.ServerId, ip);
                    Log.Info($"DHCP {ip}: NAK {want} to {req.MacPretty}");
                    return (nak, relayed ? new IPEndPoint(req.GiAddr, 67) : BroadcastClient);
                }
                var ack = BuildAddressReply(req, DhcpMessageType.Ack, ip, want, pxe, out var d);
                Log.Info($"DHCP {ip}: ACK {want} to {req.MacPretty}" + (d is null ? "" : $" ({d.ClientKind}) -> {d.File}"));
                return (ack, ReplyDestination(req, ack));
            }
            case DhcpMessageType.Release:
                pool.Release(req.Mac, req.CiAddr);
                Log.Info($"DHCP {ip}: RELEASE {req.CiAddr} from {req.MacPretty}");
                return (null, null);
            case DhcpMessageType.Decline:
                // The client found (via ARP) that someone else already uses the address.
                if (req.GetIp(DhcpOption.RequestedIp) is { } declined) { pool.Quarantine(declined); Log.Warn($"DHCP: {req.MacPretty} declined {declined} (address conflict?)"); }
                return (null, null);
            case DhcpMessageType.Inform:
            {
                // The client already has an address and wants settings only: no lease options.
                var ack = BuildAddressReply(req, DhcpMessageType.Ack, ip, IPAddress.Any, pxe, out _);
                ack.Options.Remove(DhcpOption.LeaseTime);
                ack.Options.Remove(DhcpOption.RenewalTime);
                ack.Options.Remove(DhcpOption.RebindingTime);
                return (ack, new IPEndPoint(req.CiAddr, 68));
            }
        }
        return (null, null);
    }

    // ---- port 4011 --------------------------------------------------------------------------

    /// <summary>
    /// PXE boot-server request. The client now has an IP address and sends a REQUEST (or
    /// INFORM) straight to us. We reply by unicast to whatever address and port it sent from.
    /// </summary>
    (DhcpPacket?, IPEndPoint?) Handle4011(DhcpPacket req, IPAddress ip, IPEndPoint remote)
    {
        if (req.MessageType is not (DhcpMessageType.Request or DhcpMessageType.Inform)) return (null, null);
        if (!BootPolicy.IsPxeOrHttpClient(req)) return (null, null);
        var ack = BuildBootReply(req, DhcpMessageType.Ack, ip, out var d);
        if (ack is null) return (null, null);
        ack.CiAddr = req.CiAddr;
        Log.Info($"PXE {ip}:4011: ACK to {req.MacPretty} @ {remote.Address} ({d!.ClientKind}) -> {d.File}");
        return (ack, remote);
    }

    // ---- reply builders ---------------------------------------------------------------------

    /// <summary>
    /// A proxyDHCP or boot-server reply: boot information only. yiaddr stays 0.0.0.0, so the
    /// client keeps the address the real DHCP server gave it.
    /// </summary>
    DhcpPacket? BuildBootReply(DhcpPacket req, DhcpMessageType type, IPAddress serverIp, out BootPolicy.Decision? d)
    {
        d = policy.Decide(req, serverIp.ToString());
        if (d is null)
        {
            Log.Warn($"PXE: no boot file configured for {req.MacPretty} ({BootPolicy.ArchName(req.ClientArch)}), ignoring");
            return null;
        }
        var r = req.CreateReply(type);
        r.SetIp(DhcpOption.ServerId, serverIp);
        AddBootInfo(r, req, serverIp, d);
        return r;
    }

    /// <summary>A full DHCP reply: address, network settings and (for PXE clients) boot information.</summary>
    DhcpPacket BuildAddressReply(DhcpPacket req, DhcpMessageType type, IPAddress serverIp, IPAddress addr, bool pxe, out BootPolicy.Decision? d)
    {
        var r = req.CreateReply(type);
        r.YiAddr = addr;
        r.SetIp(DhcpOption.ServerId, serverIp);
        r.SetUInt32(DhcpOption.LeaseTime, (uint)cfg.LeaseSeconds);
        r.SetUInt32(DhcpOption.RenewalTime, (uint)(cfg.LeaseSeconds / 2));        // T1 (RFC 2131 default)
        r.SetUInt32(DhcpOption.RebindingTime, (uint)(cfg.LeaseSeconds * 7 / 8));  // T2 (RFC 2131 default)
        r.SetIp(DhcpOption.SubnetMask, cfg.SubnetMask!);
        if (cfg.Router is not null) r.SetIp(DhcpOption.Router, cfg.Router);
        if (cfg.Dns.Count > 0) r.Options[DhcpOption.Dns] = cfg.Dns.SelectMany(a => a.GetAddressBytes()).ToArray();
        if (cfg.DomainName is not null) r.SetString(DhcpOption.DomainName, cfg.DomainName);

        d = null;
        if (pxe)
        {
            d = policy.Decide(req, serverIp.ToString());
            if (d is not null) AddBootInfo(r, req, serverIp, d);
        }
        return r;
    }

    /// <summary>
    /// Adds the fields that turn a DHCP reply into a PXE boot instruction.
    /// </summary>
    static void AddBootInfo(DhcpPacket r, DhcpPacket req, IPAddress serverIp, BootPolicy.Decision d)
    {
        // siaddr = TFTP server; 'file' = what to download. Names that don't fit in the 128-byte
        // header field (long URLs) go in option 67, which iPXE and HTTP Boot also read.
        r.SiAddr = serverIp;
        if (d.File.Length < 128) r.File = d.File;
        else r.SetString(DhcpOption.BootFileName, d.File);

        // The PXE spec says a boot server's replies must carry option 60 = "PXEClient"
        // (or "HTTPClient" for UEFI HTTP Boot), or the firmware ignores them.
        r.SetString(DhcpOption.VendorClass, d.IsHttpClient ? "HTTPClient" : "PXEClient");

        // Option 97 (machine UUID) must be echoed back unchanged when the client sent it.
        if (req.Options.TryGetValue(DhcpOption.ClientGuid, out var guid)) r.Options[DhcpOption.ClientGuid] = guid;

        // Option 43 holds PXE sub-options, themselves encoded as [code][len][data]:
        //   6 = PXE_DISCOVERY_CONTROL, length 1, value 8 (bit 3) = "just download the file in
        //   this packet; skip boot-server discovery and the PXE boot menu", then 255 = end.
        // Without it, some BIOS PXE ROMs wait for a boot menu that never comes.
        // iPXE and HTTP Boot clients don't use it.
        if (!d.IsIpxe && !d.IsHttpClient) r.Options[DhcpOption.VendorSpecific] = [6, 1, 8, 255];
    }

    /// <summary>
    /// Where a port-67 reply goes, following RFC 2131 section 4.1:
    /// - relayed request          -> back to the relay agent on port 67
    /// - client already has an IP -> unicast to it on port 68
    /// - otherwise                -> broadcast on port 68
    /// </summary>
    static IPEndPoint ReplyDestination(DhcpPacket req, DhcpPacket reply)
    {
        if (!req.GiAddr.Equals(IPAddress.Any)) return new IPEndPoint(req.GiAddr, 67);
        if (!req.CiAddr.Equals(IPAddress.Any)) return new IPEndPoint(req.CiAddr, 68);
        // A real DHCP server could unicast to yiaddr by adding an ARP entry by hand. Broadcast
        // is simpler, and every client accepts it.
        return BroadcastClient;
    }

    /// <summary>True when both addresses are in the same subnet (identical after AND with the mask).</summary>
    static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var m = LeasePool.ToUInt(mask);
        return (LeasePool.ToUInt(a) & m) == (LeasePool.ToUInt(b) & m);
    }
}
