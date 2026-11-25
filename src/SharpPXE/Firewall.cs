// =====================================================================================
//  Firewall.cs - making sure Windows Firewall lets clients reach the server
// =====================================================================================
//
//  Every PXE packet arrives unsolicited: a DHCP broadcast, a TFTP read request, an HTTP
//  connection. Windows Defender Firewall drops unsolicited inbound traffic unless a rule
//  allows it. So a boot server that has started and is listening can still be invisible to
//  every client, which looks like "the client never gets an answer".
//
//  At every startup the server makes sure one inbound "allow" rule exists:
//
//      name    = SharpPXE
//      program = the full path of this SharpPXE.exe
//      profile = any (Domain, Private, Public), all protocols and ports
//
//  A rule for a program, rather than for a list of ports, also covers TFTP's random data
//  ports, which aren't known in advance.
//
//  Creating a rule needs administrator rights. The service (LocalSystem) always has them.
//  A console started without admin rights can only check and warn. The GUI runs
//  "SharpPXE.exe --firewall" elevated once when the rule is missing.
//
//  WHY netsh
//  ---------
//  The modern firewall API is a COM interface (INetFwPolicy2). Classic COM interop isn't
//  available under Native AOT. netsh.exe has been on every Windows version since XP and
//  Server Core, and its exit codes are reliable. Its text output is translated (labels
//  appear in Spanish on a Spanish system), so we never parse labels: "is the rule right?"
//  only checks that the program path, which is never translated, appears in the rule's
//  details. "chcp 65001" makes netsh print UTF-8, so paths with accents compare correctly.
//
//  One rule name per machine: if SharpPXE.exe moves (or a second copy starts with admin
//  rights), the rule is re-pointed at the new path.
//
//  Note: If someone once clicked "Cancel" on Windows' own "Allow access?" prompt for this
//  exe, Windows created BLOCK rules for it. Block rules beat allow rules. Look for rules
//  named "sharppxe.exe" in "Windows Defender Firewall with Advanced Security" and delete them.
// =====================================================================================

using System.Diagnostics;
using System.Text;

namespace SharpPXE;

public static class Firewall
{
    public const string RuleName = "SharpPXE";

    public enum State { Ok, Created, Missing, Failed }

    /// <summary>True when a rule named SharpPXE exists and points at <paramref name="exePath"/>.</summary>
    public static bool RuleMatches(string exePath)
    {
        var (code, output) = Netsh($"advfirewall firewall show rule name=\"{RuleName}\" verbose");
        // Exit code 1 = "No rules match the specified criteria".
        return code == 0 && output.Contains(exePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Makes sure the rule exists for <paramref name="exePath"/>, creating or re-pointing it when
    /// we have admin rights. <paramref name="message"/> explains the outcome for the log.
    /// </summary>
    public static State Ensure(string exePath, out string message)
    {
        try
        {
            if (RuleMatches(exePath))
            {
                message = $"inbound rule '{RuleName}' is present";
                return State.Ok;
            }
            if (!Environment.IsPrivilegedProcess)
            {
                message = $"no inbound rule '{RuleName}' for {exePath}, and creating one needs administrator rights. " +
                          "Clients may be blocked. Run once as administrator (or 'SharpPXE.exe --firewall' elevated), " +
                          "start it from SharpPXE.Gui, or install the service.";
                return State.Missing;
            }

            // Replace any rule of ours that points elsewhere (moved exe), then add the current one.
            Netsh($"advfirewall firewall delete rule name=\"{RuleName}\"");
            var (code, output) = Netsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow " +
                                       $"program=\"{exePath}\" enable=yes profile=any " +
                                       $"description=\"SharpPXE network boot server (DHCP, TFTP, HTTP)\"");
            if (code != 0 || !RuleMatches(exePath))
            {
                message = $"could not create inbound rule '{RuleName}' (netsh exit code {code}): {output.Trim()}";
                return State.Failed;
            }
            message = $"created inbound rule '{RuleName}' for {exePath}";
            return State.Created;
        }
        catch (Exception ex)
        {
            // e.g. netsh missing, or the firewall service is stopped or disabled
            message = $"could not check the firewall: {ex.Message}";
            return State.Failed;
        }
    }

    /// <summary>Removes our rule (used by --uninstall). Needs administrator rights.</summary>
    public static void Remove() => Netsh($"advfirewall firewall delete rule name=\"{RuleName}\"");

    /// <summary>Runs netsh through cmd so its output can be switched to UTF-8 first.</summary>
    static (int Code, string Output) Netsh(string args)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            // /d: skip AutoRun scripts. /s /c "...": run everything between the outer quotes as-is.
            Arguments = $"/d /s /c \"chcp 65001 >nul & netsh {args}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return (-1, "netsh timed out"); }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
