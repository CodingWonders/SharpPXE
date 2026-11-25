// =====================================================================================
//  ServerProcess.cs - running SharpPXE.exe as a child process of the GUI
// =====================================================================================
//
//  "Online" in the GUI starts the real server, SharpPXE.exe, with its stdout/stderr
//  redirected into pipes. Each line the server logs arrives here as an event and goes
//  into the log pane. The GUI and the service therefore run exactly the same server code.
//
//  If the GUI crashes or is killed from Task Manager, the server must not keep running
//  invisibly while holding ports 67/69/80. A Windows "job object" with
//  JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE handles this: the child is placed in a job whose only
//  handle belongs to the GUI. When the GUI process ends, for any reason, Windows closes
//  that handle and ends everything in the job.
// =====================================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpPXE.Gui;

public sealed class ServerProcess : IDisposable
{
    Process? _proc;
    readonly IntPtr _job = CreateKillOnCloseJob();

    /// <summary>Raised (on a thread-pool thread) for every line the server writes.</summary>
    public event Action<string>? Line;

    /// <summary>Raised (on a thread-pool thread) when the server exits, with its exit code.</summary>
    public event Action<int>? Exited;

    public bool IsRunning => _proc is { HasExited: false };

    public void Start(string exePath, string configPath)
    {
        if (IsRunning) return;
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(configPath);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) Line?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Line?.Invoke("ERR " + e.Data); };
        p.Exited += (_, _) => Exited?.Invoke(SafeExitCode(p));
        p.Start();
        if (_job != IntPtr.Zero) AssignProcessToJobObject(_job, p.Handle);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _proc = p;
    }

    /// <summary>
    /// Stops the server. The server keeps no state that needs saving (leases are in memory),
    /// so terminating it is safe: Windows closes its sockets immediately.
    /// </summary>
    public void Stop()
    {
        var p = _proc;
        if (p is null || p.HasExited) return;
        try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); }
        catch (InvalidOperationException) { }   // already gone
        catch (Win32Exception) { }
    }

    static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }

    public void Dispose()
    {
        Stop();
        _proc?.Dispose();
        if (_job != IntPtr.Zero) CloseHandle(_job);
    }

    // ---- Win32 job object ---------------------------------------------------------------------

    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    static unsafe IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &info, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
        {
            CloseHandle(job);
            return IntPtr.Zero;
        }
        return job;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern unsafe bool SetInformationJobObject(IntPtr job, int infoClass, void* info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
