// =====================================================================================
//  TftpServer.cs - Trivial File Transfer Protocol (read-only)
// =====================================================================================
//
//  TFTP (RFC 1350) is the only file transfer protocol every PXE ROM supports. It is small
//  enough to fit in a network card's boot ROM: UDP only, no login, no directory listing.
//
//  PACKETS (all start with a 2-byte big-endian opcode)
//  ----------------------------------------------------
//    1 RRQ    read request    [01][filename\0][mode\0]{[option\0][value\0]...}
//    2 WRQ    write request   (we refuse these)
//    3 DATA                   [03][block#: 2 bytes][up to blksize bytes of data]
//    4 ACK                    [04][block#: 2 bytes]
//    5 ERROR                  [05][code: 2 bytes][message\0]
//    6 OACK   option ack      [06]{[option\0][value\0]...}   (RFC 2347)
//
//  A TRANSFER
//  ----------
//  The client sends RRQ to port 69. The server replies from a NEW random port (the
//  "transfer ID"), and the rest of the transfer uses that port pair. So port 69 is only a
//  front door, and many transfers can run in parallel, each on its own socket.
//
//    client:2070  -> server:69     RRQ "ipxe.efi" octet blksize=1468 tsize=0 windowsize=8
//    client:2070 <-  server:50123  OACK blksize=1468 tsize=1100000 windowsize=8
//    client:2070  -> server:50123  ACK 0          (accepts the options)
//    client:2070 <-  server:50123  DATA 1..8      (one window of 8 blocks)
//    client:2070  -> server:50123  ACK 8
//    ...                           the last DATA block is shorter than blksize = end of file
//
//  Original TFTP is "lock-step": 512-byte blocks, one ACK per block. On a LAN that's slow,
//  because every block waits a full round trip. The extensions fix this:
//    blksize    (RFC 2348)  bigger blocks. 1468 fills a 1500-byte Ethernet frame
//                           (1500 - 20 IP - 8 UDP - 4 TFTP = 1468).
//    windowsize (RFC 7440)  send N blocks before waiting for an ACK. Very large speed-up.
//    tsize      (RFC 2349)  the client asks for the file size (to allocate memory, show progress).
//    timeout    (RFC 2349)  client-chosen retransmit timeout in seconds.
//  The server may lower any value the client asks for, but never raise it.
//
//  RELIABILITY
//  -----------
//  UDP can lose packets. If we don't get an ACK in time, we resend everything after the last
//  ACKed block. With windows, a client that sees a gap ACKs the last block it got in order,
//  and we resend from there.
//
//  BLOCK NUMBER ROLLOVER
//  ---------------------
//  Block numbers are 16 bits, so they wrap from 65535 to 0. A 300 MB boot.wim at 1468
//  bytes per block is about 214,000 blocks, so it wraps 3 times. Internally we count blocks
//  with a 64-bit "absolute" number and only cut it to 16 bits on the wire (see Unwrap).
// =====================================================================================

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using SharpPXE.Net;

namespace SharpPXE.Tftp;

/// <summary>
/// Read-only TFTP server: RFC 1350 with the option extension (2347), blksize (2348),
/// timeout/tsize (2349) and windowsize (7440). Each transfer uses its own ephemeral port.
/// </summary>
public sealed class TftpServer(Config cfg, FileProvider files) : BackgroundService
{
    // Opcodes and error codes from RFC 1350 / 2347.
    const ushort OpRrq = 1, OpWrq = 2, OpData = 3, OpAck = 4, OpError = 5, OpOack = 6;
    const ushort ErrUndefined = 0, ErrNotFound = 1, ErrAccess = 2, ErrIllegal = 4, ErrUnknownTid = 5, ErrOptions = 8;

    /// <summary>Opens UDP 69 on every interface and starts one listener per socket.</summary>
    protected override Task ExecuteAsync(CancellationToken ct)
    {
        if (!cfg.Tftp) { Log.Info("TFTP: disabled"); return Task.CompletedTask; }
        var loops = new List<Task>();
        foreach (var (ip, _) in cfg.ResolveInterfaces())
        {
            Socket sock;
            try { sock = UdpHelpers.Bind(ip, 69); }
            catch (SocketException ex) { Log.Error($"TFTP: cannot bind {ip}:69 ({ex.SocketErrorCode})"); continue; }
            Log.Info($"TFTP: listening on {ip}:69, root {files.Root}");
            loops.Add(Task.Run(() => Listen(sock, ip, ct), ct));
        }
        return Task.WhenAll(loops);
    }

    /// <summary>
    /// The port-69 front door: receive a request, start a transfer task for it, and go
    /// straight back to listening. Ten PCs booting at once means ten transfers in parallel.
    /// </summary>
    async Task Listen(Socket sock, IPAddress ip, CancellationToken ct)
    {
        using var listener = sock;
        var buf = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await sock.ReceiveFromAsync(buf, SocketFlags.None, any, ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }

            var remote = (IPEndPoint)r.RemoteEndPoint;
            var packet = buf.AsSpan(0, r.ReceivedBytes).ToArray(); // copy: buf is reused for the next packet
            _ = Task.Run(() => HandleRequest(packet, ip, remote, ct), ct); // fire and forget
        }
    }

    /// <summary>Parses an RRQ, finds the file and runs the transfer on a fresh socket.</summary>
    async Task HandleRequest(byte[] pkt, IPAddress localIp, IPEndPoint client, CancellationToken ct)
    {
        // Port 0 = "any free port". This new port is the server side of the transfer ID.
        using var sock = UdpHelpers.Bind(localIp, 0);
        try
        {
            if (pkt.Length < 4) return;
            ushort op = BinaryPrimitives.ReadUInt16BigEndian(pkt);
            if (op == OpWrq) { await SendError(sock, client, ErrAccess, "Write not supported", ct); return; }
            if (op != OpRrq) { await SendError(sock, client, ErrIllegal, "Illegal operation", ct); return; }

            // After the opcode the RRQ is a list of zero-terminated strings:
            // filename, mode, then option name/value pairs.
            var fields = Encoding.ASCII.GetString(pkt, 2, pkt.Length - 2).Split('\0');
            if (fields.Length < 2) { await SendError(sock, client, ErrIllegal, "Malformed request", ct); return; }
            var fileName = fields[0];
            // fields[1] is the mode: "octet" (binary) or "netascii" (text with CRLF conversion).
            // Boot loaders always use octet, so we send the bytes unchanged either way.
            var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 2; i + 1 < fields.Length; i += 2)
                if (fields[i].Length > 0) opts[fields[i]] = fields[i + 1];

            // FileProvider rejects paths that try to escape the root folder ("../../Windows/...").
            var full = files.Resolve(fileName);
            if (full is null)
            {
                Log.Warn($"TFTP {client.Address}: not found '{fileName}'");
                await SendError(sock, client, ErrNotFound, "File not found", ct);
                return;
            }

            await using var stream = files.Open(full, localIp.ToString(), cfg.HttpPort);
            await Transfer(sock, client, fileName, stream, opts, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"TFTP {client.Address}: {ex.Message}");
            try { await SendError(sock, client, ErrUndefined, "Server error", CancellationToken.None); } catch { }
        }
    }

    /// <summary>
    /// Sends one file: option negotiation, then windows of DATA blocks until the client has
    /// acknowledged the last (short) block.
    /// </summary>
    async Task Transfer(Socket sock, IPEndPoint client, string name, Stream data, Dictionary<string, string> opts, CancellationToken ct)
    {
        long length = data.Length;

        // ---- 1. Option negotiation ------------------------------------------------------------
        // Defaults are plain RFC 1350. For each option the client asked for, we pick a value
        // (never more than requested or configured) and list it in the OACK. Options we
        // don't list are refused, and the client falls back to the default.
        int blk = 512, window = 1, timeoutMs = cfg.TftpTimeoutMs;
        var oack = new List<(string, string)>();

        if (opts.TryGetValue("blksize", out var s) && int.TryParse(s, out var reqBlk) && reqBlk >= 8)
        { blk = Math.Min(reqBlk, cfg.TftpMaxBlockSize); oack.Add(("blksize", blk.ToString())); }
        if (opts.TryGetValue("windowsize", out s) && int.TryParse(s, out var reqWin) && reqWin >= 1)
        { window = Math.Min(reqWin, cfg.TftpMaxWindow); oack.Add(("windowsize", window.ToString())); }
        if (opts.TryGetValue("timeout", out s) && int.TryParse(s, out var reqTo) && reqTo is >= 1 and <= 255)
        { timeoutMs = reqTo * 1000; oack.Add(("timeout", reqTo.ToString())); }
        if (opts.ContainsKey("tsize"))
            oack.Add(("tsize", length.ToString())); // the client sends tsize=0; we fill in the real size

        Log.Info($"TFTP {client.Address}: RRQ '{name}' ({length:N0} bytes, blksize {blk}, window {window})");
        var sw = Stopwatch.StartNew();
        var rx = new byte[1024];

        // A file always ends with a block SHORTER than blksize. If its size is an exact
        // multiple of blksize, that last block is empty (0 bytes). Hence "+ 1".
        long totalBlocks = length / blk + 1;

        // With an OACK, the client confirms with ACK 0 before any data flows.
        if (oack.Count > 0)
        {
            var o = BuildOack(oack);
            bool acked = false;
            for (int attempt = 0; attempt < cfg.TftpRetries && !acked; attempt++)
            {
                await sock.SendToAsync(o, SocketFlags.None, client, ct);
                var (kind, val) = await Receive(sock, client, rx, timeoutMs, ct);
                if (kind == OpAck && val == 0) acked = true;
                else if (kind == OpError)
                {
                    // Many BIOS PXE ROMs send RRQ with tsize only to learn the size, answer the
                    // OACK with an ERROR to cancel, then send a second RRQ for the real download.
                    // That's normal, not a failure.
                    Log.Debug($"TFTP {client.Address}: client ended transfer after OACK (code {val})");
                    return;
                }
            }
            if (!acked) { Log.Warn($"TFTP {client.Address}: no ACK for OACK, giving up on '{name}'"); return; }
        }

        // ---- 2. Data transfer: sliding window --------------------------------------------------
        long acked0 = 0;      // highest block the client has confirmed (absolute block number)
        long sent = 0;        // highest block we have sent (absolute)
        int retries = 0;
        var tx = new byte[4 + blk];

        while (acked0 < totalBlocks)
        {
            // Send the window: the blocks after the last confirmed one, up to 'window' of them.
            long first = acked0 + 1;
            long last = Math.Min(acked0 + window, totalBlocks);
            for (long b = first; b <= last; b++)
            {
                int n = ReadBlock(data, b, blk, tx);
                await sock.SendToAsync(new ReadOnlyMemory<byte>(tx, 0, 4 + n), SocketFlags.None, client, ct);
            }
            sent = last;

            // Wait for an ACK that moves us forward.
            while (true)
            {
                var (kind, val) = await Receive(sock, client, rx, timeoutMs, ct);
                if (kind == 0) // timeout: the window or the ACK was lost
                {
                    if (++retries > cfg.TftpRetries) { Log.Warn($"TFTP {client.Address}: timed out on '{name}' at block {acked0 + 1}"); return; }
                    break; // go round the outer loop: resend the window
                }
                if (kind == OpError) { Log.Warn($"TFTP {client.Address}: client aborted '{name}' (code {val})"); return; }
                if (kind != OpAck) continue;

                long abs = Unwrap(val, acked0);
                if (abs > acked0 && abs <= sent)
                {
                    // Progress. If the client ACKed only part of the window (it lost a block),
                    // the next window starts right after what it confirmed, which is a resend.
                    acked0 = abs;
                    retries = 0;
                    break;
                }
                // The client re-ACKed the same block: with windows, that means "I saw a gap".
                if (abs == acked0 && window > 1) break;

                // In lock-step mode a duplicate ACK is just a late echo. Answering each one with
                // another DATA packet would double the traffic every round: the "Sorcerer's
                // Apprentice" bug (RFC 1123 section 4.2.3.1). So we ignore it.
            }
        }

        sw.Stop();
        var secs = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        Log.Info($"TFTP {client.Address}: sent '{name}' {length:N0} bytes in {secs:F2}s ({length / secs / 1048576:F2} MiB/s)");
    }

    /// <summary>
    /// Converts a 16-bit block number from the wire back into the absolute block number nearest
    /// <paramref name="reference"/>. Example: reference = 65534 and the ACK says 1, so the
    /// absolute block is 65537 (it wrapped), not 1.
    /// </summary>
    static long Unwrap(ushort block, long reference)
    {
        long abs = (reference & ~0xFFFFL) | block;          // same 65536-block "page" as the reference
        if (abs < reference - 0x8000) abs += 0x10000;       // too far behind: it's in the next page
        else if (abs > reference + 0x8000) abs -= 0x10000;  // too far ahead: it's in the previous page
        return abs;
    }

    /// <summary>Builds a DATA packet for block number <paramref name="block"/> in <paramref name="tx"/>; returns the data length.</summary>
    static int ReadBlock(Stream data, long block, int blk, byte[] tx)
    {
        BinaryPrimitives.WriteUInt16BigEndian(tx, OpData);
        BinaryPrimitives.WriteUInt16BigEndian(tx.AsSpan(2), (ushort)(block & 0xFFFF)); // 65536 -> 0 (rollover)
        // Seek to the block every time: a resend may need to go back to an earlier block.
        data.Position = (block - 1) * blk;
        int total = 0;
        while (total < blk)
        {
            int n = data.Read(tx, 4 + total, blk - total);
            if (n == 0) break; // end of file
            total += n;
        }
        return total;
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for a packet from the client.
    /// Returns (0, 0) on timeout, otherwise (opcode, block number or error code).
    /// </summary>
    static async Task<(ushort Kind, ushort Value)> Receive(Socket sock, IPEndPoint client, byte[] rx, int timeoutMs, CancellationToken ct)
    {
        // A token that cancels after the timeout OR when the server shuts down.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            SocketReceiveFromResult r;
            try { r = await sock.ReceiveFromAsync(rx, SocketFlags.None, any, cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (0, 0); }
            catch (SocketException) { continue; }

            // Packets from any other address/port are not part of this transfer (RFC 1350:
            // "unknown transfer ID"). Reply with an error, but keep going.
            var from = (IPEndPoint)r.RemoteEndPoint;
            if (!from.Equals(client))
            {
                await SendError(sock, from, ErrUnknownTid, "Unknown transfer ID", ct);
                continue;
            }
            if (r.ReceivedBytes < 4) continue;
            return (BinaryPrimitives.ReadUInt16BigEndian(rx), BinaryPrimitives.ReadUInt16BigEndian(rx.AsSpan(2)));
        }
    }

    /// <summary>OACK: opcode 6 followed by "name\0value\0" pairs.</summary>
    static byte[] BuildOack(List<(string Name, string Value)> opts)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0); ms.WriteByte((byte)OpOack);
        foreach (var (n, v) in opts)
        {
            ms.Write(Encoding.ASCII.GetBytes(n)); ms.WriteByte(0);
            ms.Write(Encoding.ASCII.GetBytes(v)); ms.WriteByte(0);
        }
        return ms.ToArray();
    }

    /// <summary>ERROR: opcode 5, error code, message, zero terminator.</summary>
    static async Task SendError(Socket sock, EndPoint to, ushort code, string msg, CancellationToken ct)
    {
        var m = Encoding.ASCII.GetBytes(msg);
        var p = new byte[5 + m.Length];
        BinaryPrimitives.WriteUInt16BigEndian(p, OpError);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), code);
        m.CopyTo(p, 4);
        await sock.SendToAsync(p, SocketFlags.None, to, ct);
    }
}
