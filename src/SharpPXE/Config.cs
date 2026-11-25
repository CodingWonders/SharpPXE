// =====================================================================================
//  Config.cs - settings from SharpPXE.ini, and choosing which network adapters to use
// =====================================================================================
//
//  The INI format was chosen to feel like Tiny PXE Server's config.ini:
//
//    [section]
//    key=value      ; comment
//
//  The parser is hand-written (about 30 lines) instead of using Microsoft.Extensions.
//  Configuration binding, which relies on reflection that Native AOT trims away.
//  Every setting is one line in the switch inside Apply(), so adding an option means adding
//  a field and a case.
//
//  Interface selection matters more for a boot server than for most programs. DHCP works
//  with broadcasts, which stay on one network segment, and the address we advertise as
//  "next server" must be reachable from that segment. A typical Windows machine has
//  several adapters (Wi-Fi, Hyper-V switches, VMware NAT, VPN), so we open sockets per
//  adapter address and always answer with the address of the adapter the request came in on.
// =====================================================================================

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SharpPXE;

/// <summary>[server] mode=: proxy (boot info only), dhcp (addresses + boot info), off (TFTP/HTTP only).</summary>
public enum DhcpMode { Off, Proxy, Full }

/// <summary>
/// Settings loaded from SharpPXE.ini. The field initialisers are the defaults used when a
/// key is missing. Public fields instead of properties keep it simple; nothing else changes them.
/// </summary>
public sealed class Config
{
    // [server]
    public DhcpMode Mode = DhcpMode.Proxy;
    public List<IPAddress> Bind = new();
    public string Root = "files";
    public bool Tftp = true;
    public bool Http = true;
    public int HttpPort = 80;
    public string? LogFile;
    public bool Verbose;
    /// <summary>Check/create the Windows Firewall rule at startup (see Firewall.cs).</summary>
    public bool Firewall = true;

    // [boot]  filenames are relative to Root (TFTP) unless they are http:// URLs.
    public string BootBios = "undionly.kpxe";
    public string BootUefiX64 = "ipxe.efi";
    public string BootUefiIa32 = "ipxe-i386.efi";
    public string BootUefiArm64 = "ipxe-arm64.efi";
    public string BootHttpX64 = "http://{server}/ipxe.efi";
    public string BootHttpArm64 = "http://{server}/ipxe-arm64.efi";
    /// <summary>What to hand iPXE once it is running (detected via option 77 / 175).</summary>
    public string IpxeScript = "http://{server}/boot.ipxe";
    /// <summary>Per-MAC overrides of the iPXE script, e.g. [mac] 00-15-5d-01-02-03=http://{server}/special.ipxe</summary>
    public Dictionary<string, string> MacScripts = new(StringComparer.OrdinalIgnoreCase);

    // [dhcp]  only used when mode=dhcp
    public IPAddress? PoolStart, PoolEnd, SubnetMask, Router;
    public List<IPAddress> Dns = new();
    public string? DomainName;
    public int LeaseSeconds = 3600;

    // [tftp]
    public int TftpMaxBlockSize = 1468;   // keep under a 1500 MTU by default
    public int TftpMaxWindow = 16;
    public int TftpTimeoutMs = 1500;
    public int TftpRetries = 6;

    public string RootFullPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Root));

    /// <summary>
    /// Reads the INI file line by line: track the current [section], split each line at the
    /// first '=', and pass (section, key, value) to Apply(). A bad value stops startup with
    /// the file name and line number. A server that starts with half its settings wrong is
    /// harder to debug than one that refuses to start.
    /// </summary>
    public static Config Load(string path)
    {
        var c = new Config();
        if (!File.Exists(path))
        {
            Log.Warn($"Config file not found: {path} (using defaults)");
            return c;
        }

        string section = "";
        int lineNo = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            lineNo++;
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0) { Log.Warn($"{Path.GetFileName(path)}:{lineNo}: ignoring '{line}'"); continue; }
            var key = line[..eq].Trim().ToLowerInvariant();
            var val = line[(eq + 1)..].Trim();
            try { c.Apply(section, key, val); }
            catch (Exception ex) { throw new FormatException($"{Path.GetFileName(path)}:{lineNo}: bad value for {key}: {ex.Message}"); }
        }
        c.Validate();
        return c;
    }

    /// <summary>Removes a comment from a line.</summary>
    static string StripComment(string s)
    {
        // A comment is ';' or '#' at the start of a line, or ' ;' / ' #' (with a space before
        // it) after a value. Requiring the space keeps URLs like http://x/#a intact.
        var t = s.TrimStart();
        if (t.StartsWith(';') || t.StartsWith('#')) return "";
        int i = s.IndexOf(" ;", StringComparison.Ordinal);
        int j = s.IndexOf(" #", StringComparison.Ordinal);
        int cut = (i, j) switch { (< 0, < 0) => -1, (< 0, _) => j, (_, < 0) => i, _ => Math.Min(i, j) };
        return cut >= 0 ? s[..cut] : s;
    }

    /// <summary>Applies one setting. Unknown keys only produce a warning, so a typo doesn't stop the server.</summary>
    void Apply(string section, string key, string val)
    {
        switch (section, key)
        {
            case ("server", "mode"):
                Mode = val.ToLowerInvariant() switch
                {
                    "proxy" or "proxydhcp" => DhcpMode.Proxy,
                    "dhcp" or "full" => DhcpMode.Full,
                    "off" or "none" or "0" => DhcpMode.Off,
                    _ => throw new FormatException("expected proxy, dhcp or off"),
                };
                break;
            case ("server", "bind"): Bind = ParseIpList(val); break;
            case ("server", "root"): Root = val; break;
            case ("server", "tftp"): Tftp = ParseBool(val); break;
            case ("server", "http"): Http = ParseBool(val); break;
            case ("server", "http_port"): HttpPort = int.Parse(val); break;
            case ("server", "log"): LogFile = val.Length == 0 ? null : val; break;
            case ("server", "verbose"): Verbose = ParseBool(val); break;
            case ("server", "firewall"): Firewall = ParseBool(val); break;

            case ("boot", "bios"): BootBios = val; break;
            case ("boot", "uefi_x64"): BootUefiX64 = val; break;
            case ("boot", "uefi_ia32"): BootUefiIa32 = val; break;
            case ("boot", "uefi_arm64"): BootUefiArm64 = val; break;
            case ("boot", "httpboot_x64"): BootHttpX64 = val; break;
            case ("boot", "httpboot_arm64"): BootHttpArm64 = val; break;
            case ("boot", "ipxe"): IpxeScript = val; break;

            case ("mac", _): MacScripts[NormalizeMac(key)] = val; break;

            case ("dhcp", "pool_start"): PoolStart = IPAddress.Parse(val); break;
            case ("dhcp", "pool_end"): PoolEnd = IPAddress.Parse(val); break;
            case ("dhcp", "subnet"): SubnetMask = IPAddress.Parse(val); break;
            case ("dhcp", "router"): Router = val.Length == 0 ? null : IPAddress.Parse(val); break;
            case ("dhcp", "dns"): Dns = ParseIpList(val); break;
            case ("dhcp", "domain"): DomainName = val.Length == 0 ? null : val; break;
            case ("dhcp", "lease_time"): LeaseSeconds = int.Parse(val); break;

            case ("tftp", "max_blksize"): TftpMaxBlockSize = Math.Clamp(int.Parse(val), 512, 65464); break;
            case ("tftp", "max_windowsize"): TftpMaxWindow = Math.Clamp(int.Parse(val), 1, 64); break;
            case ("tftp", "timeout_ms"): TftpTimeoutMs = Math.Clamp(int.Parse(val), 100, 30000); break;
            case ("tftp", "retries"): TftpRetries = Math.Clamp(int.Parse(val), 1, 50); break;

            default: Log.Warn($"Unknown setting [{section}] {key}"); break;
        }
    }

    void Validate()
    {
        if (Mode == DhcpMode.Full && (PoolStart is null || PoolEnd is null || SubnetMask is null))
            throw new FormatException("mode=dhcp requires [dhcp] pool_start, pool_end and subnet");
    }

    static bool ParseBool(string v) => v.ToLowerInvariant() is "1" or "true" or "yes" or "on";

    static List<IPAddress> ParseIpList(string v) =>
        v.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(IPAddress.Parse).ToList();

    /// <summary>"00-15-5D-01:02.03" -> "00155d010203", the same format as <see cref="Dhcp.DhcpPacket.Mac"/>.</summary>
    public static string NormalizeMac(string mac) =>
        new string(mac.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();

    /// <summary>Local IPv4 addresses to serve on: the configured list, or every up, non-loopback, non-APIPA address.</summary>
    /// <remarks>
    /// 169.254.x.x addresses are skipped because Windows assigns them to adapters that got no
    /// DHCP lease. They're usually unplugged or virtual, and serving on them only adds noise.
    /// </remarks>
    public List<(IPAddress Ip, IPAddress Mask)> ResolveInterfaces()
    {
        var all = new List<(IPAddress, IPAddress)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            // Loopback is only used when listed explicitly in bind= (handy for testing).
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback && Bind.Count == 0) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                all.Add((ua.Address, ua.IPv4Mask ?? IPAddress.Parse("255.255.255.0")));
            }
        }

        if (Bind.Count > 0)
        {
            var result = new List<(IPAddress, IPAddress)>();
            foreach (var ip in Bind)
            {
                var match = all.FirstOrDefault(a => a.Item1.Equals(ip));
                if (match.Item1 is null) Log.Warn($"bind address {ip} is not on this machine (or the adapter is down)");
                else result.Add(match);
            }
            return result;
        }
        return all.Where(a => !a.Item1.ToString().StartsWith("169.254.")).ToList();
    }
}
