# SharpPXE

A 64-bit network boot server for Windows, written in .NET 10 and published as a single Native AOT
executable. It replaces the 32-bit Tiny PXE Server, which stops working once WOW64 is removed
(it becomes a Feature on Demand in Windows Server 2028).

| Component | Ports | Notes |
|---|---|---|
| proxyDHCP | UDP 67 | Adds boot info next to your existing DHCP server. Hands out no addresses. |
| PXE boot server | UDP 4011 | Answers the follow-up request UEFI firmware and iPXE send after the proxy offer. |
| Full DHCP (optional) | UDP 67 | `mode=dhcp` with a simple in-memory address pool. |
| TFTP | UDP 69 + ephemeral | Read-only. Supports `blksize`, `tsize`, `timeout` and `windowsize` (RFC 2347/2348/2349/7440), block-number rollover, retransmits. |
| HTTP | TCP 80 | Kestrel with range requests, for iPXE / wimboot / UEFI HTTP Boot. |

It comes as two programs in the same folder:

| Program | What it is | Needs |
|---|---|---|
| `SharpPXE.exe` | The server. Runs in a console or as a Windows service. | Nothing (Native AOT, 64-bit) |
| `SharpPXE.Gui.exe` | Optional window, similar to Tiny PXE Server: edit settings, start/stop, live log, install the service. | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |

For background on the protocols themselves (DHCP and its options, TFTP and PXE, iPXE) and on
choosing an operation mode, see the guide *Network Booting Explained* in
[docs/guide](docs/guide), available as DOCX, PDF and TXT.

To learn how it's built (the protocols, the packet-by-packet boot sequence and the code
layout), read [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md). Every source file also starts
with a header comment explaining its part.

## Boot flow (UEFI + iPXE + WinPE)

1. UEFI firmware broadcasts a DHCPDISCOVER. Your router or DHCP server gives it an address, and
   SharpPXE's proxy offer gives it `ipxe.efi` (chosen by DHCP option 93, client architecture).
2. The firmware downloads `ipxe.efi` over TFTP.
3. iPXE asks again. SharpPXE sees option 77 = `iPXE` and returns `http://<server>/boot.ipxe`
   instead, so the client doesn't load iPXE again in a loop.
4. `boot.ipxe` loads `wimboot` plus WinPE's `boot.wim` over HTTP, which is much faster than TFTP.

## Setup

1. Copy the contents of `publish\win-x64` (`SharpPXE.exe`, `SharpPXE.Gui.exe`, `SharpPXE.ini`,
   `files\`) somewhere, e.g. `C:\SharpPXE`.
2. Put the boot files in `files\`:
   ```
   files\ipxe.efi              https://boot.ipxe.org/ipxe.efi  (or your own build, e.g. signed for Secure Boot)
   files\undionly.kpxe         https://boot.ipxe.org/undionly.kpxe  (BIOS clients)
   files\wimboot               https://github.com/ipxe/wimboot/releases
   files\boot.ipxe             included sample
   files\winpe\sources\boot.wim
   files\winpe\boot\bcd        optional
   files\winpe\boot\boot.sdi   optional
   ```
3. Open `SharpPXE.Gui.exe`, or edit `SharpPXE.ini` by hand. On machines with Hyper-V, VMware or
   Wi-Fi adapters, choose only the boot network under **Serve on** (`bind=` in the INI).
4. Click **Start server** in the GUI, or run `SharpPXE.exe` in a console. To run it permanently,
   use **Service > Install service** in the GUI, or from an elevated prompt:
   ```
   SharpPXE.exe --install --config C:\SharpPXE\SharpPXE.ini
   sc.exe start SharpPXE
   ```
   `--uninstall` removes the service and the firewall rule (see [Windows Firewall](#windows-firewall)).

### iPXE script placeholders

`.ipxe` files are templated when served, over HTTP and over TFTP:

- `{server}`: the server IP on the interface the client reached
- `{http}`: `http://{server}` (plus `:port` if it isn't 80)

Boot file names in the INI also accept `{server}` and `{mac}`. Per-machine scripts go in `[mac]`.

## Windows Firewall

Every time `SharpPXE.exe` starts (as a service, in a console or from the GUI), it makes sure an
inbound **allow** rule named `SharpPXE` exists for its own path, on all profiles. If the exe has
moved, the rule is re-pointed to the new path. Creating the rule needs administrator rights:

| Started as | What happens |
|---|---|
| Windows service | Creates or repairs the rule itself (it runs as LocalSystem). |
| GUI, **Start server** | If the rule is missing, asks for administrator rights once (UAC), then starts. |
| Console, as administrator | Creates or repairs the rule. |
| Console, not elevated | Logs a warning and keeps running. Fix it with `SharpPXE.exe --firewall` from an elevated prompt. |

`SharpPXE.exe --check-firewall` exits with 0 if the rule exists and 2 if not. Set
`[server] firewall=0` (or untick it on the GUI's General tab) where Group Policy manages
firewall rules. `--uninstall` removes the rule.

If clients still get no answer, look in *Windows Defender Firewall with Advanced Security* for
**Block** rules named `sharppxe.exe`. Windows creates them if someone clicked *Cancel* on its own
"Allow access" prompt, and block rules win over allow rules.

## Things that can't share a machine

UDP 67 can't be shared with the Windows **DHCP Server** role, **WDS** (which also uses 4011 and 69),
or **Internet Connection Sharing / Hyper-V Default Switch** on the same adapter. SharpPXE logs which
bind failed. Use `bind=` to keep it off adapters that already have a DHCP service.

## The GUI

- **Start / Stop server** runs `SharpPXE.exe` as a child process and shows its log live, in color.
  If the GUI closes or crashes, the server stops with it (a Windows job object makes sure of this).
- **Settings tabs** (General, Boot files, Per machine, DHCP server, TFTP) write to `SharpPXE.ini`.
  Only the lines you change are touched, so comments and hand edits survive. If you save while
  the server or the service is running, it offers to restart it.
- **Boot files** shows next to each entry whether the file exists in the root folder.
- **Service** installs, removes, starts and stops the Windows service. Each action asks for
  administrator rights. While the service runs, the log pane follows its log file. Installing
  sets `log=SharpPXE.log` if no log file is configured.

Without arguments, the GUI looks for `SharpPXE.exe` and `SharpPXE.ini` in its own folder.
Use `--server <exe>` and `--config <ini>` to point it elsewhere.

## Building

```
.\build.ps1                  # -> publish\win-x64
.\build.ps1 -Rid win-arm64   # -> publish\win-arm64
```

The script publishes the server with Native AOT and the GUI as a small single-file exe into the
same folder. Native AOT needs Visual Studio's "Desktop development with C++" workload. The
script puts `vswhere.exe` on `PATH`, which the AOT linker setup needs.

Repository layout: `src\SharpPXE` (server), `src\SharpPXE.Gui` (WinForms), `docs\`, `SharpPXE.slnx`.

## Not implemented (yet)

- BINL / RIS, DNS server, SMB/NFS. Use Windows file sharing for anything WinPE mounts afterwards.
- Lease persistence in full DHCP mode (leases are in memory only).
