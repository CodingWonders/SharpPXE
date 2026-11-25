// =====================================================================================
//  BootPolicy.cs - which file does this client get?
// =====================================================================================
//
//  Network booting usually happens in stages, and the same machine asks DHCP more than once:
//
//   Stage 1  Firmware PXE (BIOS or UEFI) asks DHCP. It can only speak TFTP and runs only
//            programs built for its own firmware type:
//              BIOS       -> undionly.kpxe  (iPXE built as a BIOS PXE program)
//              UEFI x64   -> ipxe.efi       (iPXE built as a UEFI application)
//              UEFI ARM64 -> ipxe-arm64.efi
//            The firmware says what it is in DHCP option 93 (client architecture).
//
//   Stage 2  iPXE starts and asks DHCP AGAIN. It can speak HTTP, run scripts and show menus.
//            If we gave it ipxe.efi again it would load itself forever (the classic "iPXE
//            loop"), so it gets a script URL instead. iPXE identifies itself with option 77
//            (user class) = "iPXE" and by sending option 175.
//
//   Stage 3  The script (boot.ipxe) loads wimboot + WinPE over HTTP. No more DHCP needed.
//
//  UEFI HTTP Boot is a firmware feature that skips TFTP entirely. Those clients send
//  vendor class "HTTPClient" and expect a full http:// URL as the boot file name.
// =====================================================================================

using SharpPXE.Dhcp;

namespace SharpPXE;

/// <summary>Decides which boot file a PXE, HTTP Boot or iPXE client should get.</summary>
public sealed class BootPolicy(Config cfg)
{
    /// <param name="File">TFTP file name or http:// URL to put in the reply.</param>
    /// <param name="ClientKind">Human-readable description for the log.</param>
    /// <param name="IsIpxe">The request came from iPXE (stage 2).</param>
    /// <param name="IsHttpClient">The request came from UEFI HTTP Boot firmware.</param>
    public sealed record Decision(string File, string ClientKind, bool IsIpxe, bool IsHttpClient);

    /// <summary>
    /// Option 93 values from the IANA "Processor Architecture Types" registry (RFC 4578).
    /// 7 and 9 are both x64 UEFI: 7 was meant for "EFI BC" but is what most x64 firmware sends.
    /// </summary>
    public static string ArchName(ushort? arch) => arch switch
    {
        null => "unknown",
        0 => "BIOS",
        6 => "UEFI-IA32",
        7 or 9 => "UEFI-x64",
        10 => "UEFI-ARM32",
        11 => "UEFI-ARM64",
        15 => "HTTP-IA32",
        16 => "HTTP-x64",
        18 => "HTTP-ARM32",
        19 => "HTTP-ARM64",
        _ => $"arch-{arch}",
    };

    /// <summary>
    /// Only clients that are trying to network-boot get an answer. Every PXE ROM, iPXE and
    /// Windows Boot Manager send option 60 starting with "PXEClient". Normal devices (PCs,
    /// phones, printers) send other values or none, and proxyDHCP must ignore them.
    /// </summary>
    public static bool IsPxeOrHttpClient(DhcpPacket p)
    {
        var vc = p.VendorClass;
        return vc is not null && (vc.StartsWith("PXEClient", StringComparison.Ordinal) || vc.StartsWith("HTTPClient", StringComparison.Ordinal));
    }

    /// <summary>True when the request comes from iPXE rather than the firmware.</summary>
    public static bool IsIpxe(DhcpPacket p) =>
        p.Options.ContainsKey(DhcpOption.IpxeEncap) ||
        string.Equals(p.GetString(DhcpOption.UserClass)?.Trim(), "iPXE", StringComparison.Ordinal) ||
        // RFC 3004 defines option 77 as a list of length-prefixed strings, i.e. 04 'i' 'P' 'X' 'E'.
        // iPXE sends the plain string, but accept the RFC form too.
        (p.Options.TryGetValue(DhcpOption.UserClass, out var uc) && uc.Length == 5 && uc[0] == 4 && System.Text.Encoding.ASCII.GetString(uc, 1, 4) == "iPXE");

    /// <summary>
    /// Chooses the boot file for a request. Returns null when nothing is configured for this
    /// kind of client (e.g. an empty uefi_arm64= setting), in which case we stay silent.
    /// </summary>
    public Decision? Decide(DhcpPacket p, string serverIp)
    {
        var arch = p.ClientArch;
        bool httpClient = p.VendorClass?.StartsWith("HTTPClient", StringComparison.Ordinal) == true;
        bool ipxe = IsIpxe(p);

        string? file;
        if (ipxe)
            // Stage 2: hand iPXE its script. A [mac] entry in the INI overrides the default.
            file = cfg.MacScripts.TryGetValue(p.Mac, out var perMac) ? perMac : cfg.IpxeScript;
        else if (httpClient)
            // UEFI HTTP Boot: needs a URL.
            file = arch switch { 16 => cfg.BootHttpX64, 19 => cfg.BootHttpArm64, _ => null };
        else
            // Stage 1: firmware PXE over TFTP. Old BIOS ROMs may not send option 93 at all.
            file = arch switch
            {
                0 or null => cfg.BootBios,
                6 => cfg.BootUefiIa32,
                7 or 9 => cfg.BootUefiX64,
                11 => cfg.BootUefiArm64,
                _ => null,
            };

        if (string.IsNullOrWhiteSpace(file)) return null;

        // {server} is the address on the adapter this request arrived on, so the same INI works
        // on any interface and on any machine.
        file = file.Replace("{server}", serverIp, StringComparison.OrdinalIgnoreCase)
                   .Replace("{mac}", p.MacPretty, StringComparison.OrdinalIgnoreCase);
        var kind = ipxe ? $"iPXE/{ArchName(arch)}" : ArchName(arch);
        return new Decision(file, kind, ipxe, httpClient);
    }
}
