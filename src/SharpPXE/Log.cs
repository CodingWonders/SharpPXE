// =====================================================================================
//  Log.cs - a deliberately tiny logger
// =====================================================================================
//
//  A boot server's log is the main debugging tool: "the client got OFFER but never asked
//  for TFTP" tells you a firewall or boot file problem right away. Each protocol event
//  becomes one line, written to the console (in colour) and optionally to a file.
//
//  Microsoft.Extensions.Logging would also work. A static class keeps the protocol code free
//  of logger plumbing and works the same in AOT and when running as a service.
// =====================================================================================

namespace SharpPXE;

public static class Log
{
    static readonly object Gate = new();   // many sockets log at once; keep lines whole
    static StreamWriter? _file;
    public static bool Verbose;            // [server] verbose=1 turns on Debug lines

    public static void OpenFile(string path)
    {
        // Relative paths are relative to the exe. A Windows service's working directory is
        // C:\Windows\System32, which is never what you want.
        var full = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        _file = new StreamWriter(new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public static void Debug(string msg) { if (Verbose) Write("DBG", msg, ConsoleColor.DarkGray); }
    public static void Info(string msg) => Write("INF", msg, null);
    public static void Warn(string msg) => Write("WRN", msg, ConsoleColor.Yellow);
    public static void Error(string msg) => Write("ERR", msg, ConsoleColor.Red);

    static void Write(string level, string msg, ConsoleColor? color)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {msg}";
        lock (Gate)
        {
            if (color is { } c) Console.ForegroundColor = c;
            Console.WriteLine(line);
            if (color is not null) Console.ResetColor();
            _file?.WriteLine($"{DateTime.Now:yyyy-MM-dd} {line}");
        }
    }
}
