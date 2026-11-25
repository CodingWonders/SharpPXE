// =====================================================================================
//  LeasePool.cs - address allocation for full DHCP mode (mode=dhcp)
// =====================================================================================
//
//  A DHCP server's main state is a table of "who has which address until when".
//  An address goes through two states:
//
//    OFFERED  - reserved for a client after its DISCOVER, held briefly (60 s here) so the
//               client has time to send its REQUEST. If it never does, the address is free again.
//    BOUND    - confirmed by an ACK. Held until the lease expires. The client renews at T1
//               (half the lease) to keep it.
//
//  IPv4 addresses are handled as 32-bit unsigned integers, so "the next address" is just
//  +1 and a range check is two comparisons. 192.168.1.10 = 0xC0A8010A.
//
//  This pool is in memory only. After a restart, clients keep their addresses and renew
//  them, and Commit() accepts a renewal for any free address in the pool.
// =====================================================================================

using System.Buffers.Binary;
using System.Net;

namespace SharpPXE.Dhcp;

/// <summary>In-memory address pool for full DHCP mode.</summary>
public sealed class LeasePool
{
    /// <summary>One table row. <c>Bound</c> = false means "offered, not yet confirmed".</summary>
    sealed record Lease(string Mac, uint Ip, DateTime Expires, bool Bound);

    readonly uint _start, _end;
    readonly HashSet<uint> _excluded;                    // our own IPs, the router...
    readonly Dictionary<uint, Lease> _byIp = new();
    readonly object _gate = new();                      // several sockets can call us at once
    static readonly TimeSpan OfferHold = TimeSpan.FromSeconds(60);

    public LeasePool(IPAddress start, IPAddress end, IEnumerable<IPAddress> excluded)
    {
        _start = ToUInt(start);
        _end = ToUInt(end);
        if (_end < _start) throw new ArgumentException("pool_end is before pool_start");
        _excluded = excluded.Select(ToUInt).ToHashSet();
    }

    /// <summary>Converts an IPv4 address to a number (big-endian, so the ordering is natural).</summary>
    public static uint ToUInt(IPAddress ip) => BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());

    public static IPAddress FromUInt(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return new IPAddress(b);
    }

    public bool InPool(IPAddress ip) { var v = ToUInt(ip); return v >= _start && v <= _end && !_excluded.Contains(v); }

    /// <summary>
    /// Chooses an address to OFFER, in order of preference: the address this client already
    /// has, the address it asked for (option 50) if free, or the first free address. That way
    /// a machine tends to get the same IP every time.
    /// </summary>
    public IPAddress? Offer(string mac, IPAddress? requested)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var mine = _byIp.Values.FirstOrDefault(l => l.Mac == mac);
            if (mine is not null)
            {
                if (!mine.Bound) _byIp[mine.Ip] = mine with { Expires = now + OfferHold };
                return FromUInt(mine.Ip);
            }

            uint pick = 0;
            if (requested is not null && InPool(requested) && IsFree(ToUInt(requested), now)) pick = ToUInt(requested);
            else
                for (uint v = _start; v <= _end && v != 0; v++)   // v != 0 stops if the counter wraps around
                    if (!_excluded.Contains(v) && IsFree(v, now)) { pick = v; break; }

            if (pick == 0) return null; // pool full
            _byIp[pick] = new Lease(mac, pick, now + OfferHold, false);
            return FromUInt(pick);
        }
    }

    /// <summary>
    /// Confirms a REQUEST, turning an offer (or a renewal) into a bound lease.
    /// Returns false if the address belongs to someone else or is outside the pool. The
    /// caller then sends a NAK.
    /// </summary>
    public bool Commit(string mac, IPAddress ip, TimeSpan leaseTime)
    {
        lock (_gate)
        {
            if (!InPool(ip)) return false;
            var v = ToUInt(ip);
            var now = DateTime.UtcNow;
            if (_byIp.TryGetValue(v, out var l) && l.Mac != mac && l.Expires > now) return false;
            // One address per client: drop anything else it held.
            foreach (var old in _byIp.Values.Where(x => x.Mac == mac && x.Ip != v).ToList()) _byIp.Remove(old.Ip);
            _byIp[v] = new Lease(mac, v, now + leaseTime, true);
            return true;
        }
    }

    /// <summary>DHCPRELEASE, or the client picked another server's offer.</summary>
    public void Release(string mac, IPAddress? ip = null)
    {
        lock (_gate)
            foreach (var l in _byIp.Values.Where(x => x.Mac == mac && (ip is null || x.Ip == ToUInt(ip))).ToList())
                _byIp.Remove(l.Ip);
    }

    /// <summary>
    /// DHCPDECLINE: the client found that another device already uses this address
    /// (probably a static IP). Keep it out of circulation for a while.
    /// </summary>
    public void Quarantine(IPAddress ip)
    {
        lock (_gate) _byIp[ToUInt(ip)] = new Lease("declined", ToUInt(ip), DateTime.UtcNow.AddMinutes(10), true);
    }

    bool IsFree(uint v, DateTime now) => !_byIp.TryGetValue(v, out var l) || l.Expires <= now;
}
