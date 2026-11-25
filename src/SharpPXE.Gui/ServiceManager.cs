// =====================================================================================
//  ServiceManager.cs - the SharpPXE Windows service, seen from the GUI
// =====================================================================================
//
//  Any user can read a service's status. Installing, removing, starting and stopping it
//  need administrator rights. Rather than making the whole GUI run elevated (a UAC prompt
//  every time it opens), each of those actions launches a short elevated command
//  (Verb = "runas"), so UAC prompts only for that action:
//
//    install / uninstall  ->  SharpPXE.exe --install / --uninstall  (reuses the server's code)
//    start / stop         ->  sc.exe start / stop SharpPXE
// =====================================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace SharpPXE.Gui;

public static class ServiceManager
{
    public const string Name = "SharpPXE";

    /// <summary>Null when the service isn't installed.</summary>
    public static ServiceControllerStatus? Status()
    {
        try
        {
            using var sc = new ServiceController(Name);
            return sc.Status;   // throws InvalidOperationException if it doesn't exist
        }
        catch (InvalidOperationException) { return null; }
    }

    public static string Describe(ServiceControllerStatus? s) => s switch
    {
        null => "Not installed",
        ServiceControllerStatus.Running => "Running",
        ServiceControllerStatus.Stopped => "Stopped",
        ServiceControllerStatus.StartPending => "Starting...",
        ServiceControllerStatus.StopPending => "Stopping...",
        _ => s.ToString()!,
    };

    public static bool Install(string serverExe, string configPath) =>
        RunElevated(serverExe, $"--install --config \"{configPath}\"");

    public static bool Uninstall(string serverExe) => RunElevated(serverExe, "--uninstall");

    public static bool Start() => RunElevated("sc.exe", $"start {Name}");

    public static bool Stop() => RunElevated("sc.exe", $"stop {Name}");

    /// <summary>
    /// Runs a command with administrator rights and waits for it. Returns false if the user
    /// cancelled the UAC prompt or the command failed.
    /// </summary>
    public static bool RunElevated(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = true,      // required for Verb
                Verb = "runas",              // ask UAC for elevation
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC "No"
        {
            return false;
        }
    }
}
