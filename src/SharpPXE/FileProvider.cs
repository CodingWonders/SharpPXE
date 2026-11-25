// =====================================================================================
//  FileProvider.cs - turning request paths into files, safely
// =====================================================================================
//
//  Both TFTP and HTTP let an unauthenticated client name any file it wants, so the most
//  important security rule of a boot server is: never serve anything outside the root folder.
//  Attacks to block:
//    ../../Windows/System32/config/SAM      (going up out of the root)
//    winpe%5C..%5C..%5Csecret.txt           (the same, URL-encoded with backslashes)
//    C:\secret.txt, \\server\share, file::$DATA   (absolute paths, alternate data streams)
//  The safe approach: build the full path, let the OS normalise it (Path.GetFullPath
//  collapses every "..") and then check that the result still starts with the root.
//
//  Paths also arrive in mixed styles: Windows Boot Manager asks TFTP for "\Boot\BCD"
//  with backslashes, and iPXE asks for "/boot/bcd" with slashes. We accept both.
// =====================================================================================

using System.Text;

namespace SharpPXE;

/// <summary>Maps TFTP/HTTP request paths onto the root folder, safely, and fills in iPXE script templates.</summary>
public sealed class FileProvider(Config cfg)
{
    // A trailing separator matters: without it, "C:\boot" would also match "C:\bootleg\...".
    readonly string _root = cfg.RootFullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    public string Root => _root;

    /// <summary>Returns the full path for a request path, or null if it escapes the root or doesn't exist.</summary>
    public string? Resolve(string requestPath)
    {
        // Decode %xx first, so an encoded "..%5C" can't sneak past the check below.
        var rel = Uri.UnescapeDataString(requestPath).Replace('\\', '/').TrimStart('/');
        // ':' would allow drive letters ("C:...") and NTFS streams ("file::$DATA"); NUL ends strings in native code.
        if (rel.Length == 0 || rel.Contains(':') || rel.Contains('\0')) return null;
        string full;
        try { full = Path.GetFullPath(Path.Combine(_root, rel)); }  // collapses "..", "." and duplicate separators
        catch { return null; }                                      // invalid characters, path too long...
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) return null; // escaped the root
        return File.Exists(full) ? full : null;
    }

    /// <summary>Files that get placeholders filled in before they're sent.</summary>
    public static bool IsTemplate(string fullPath) =>
        fullPath.EndsWith(".ipxe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Opens a file for reading. iPXE scripts get {server} and {http} replaced and come back
    /// as an in-memory stream. Everything else is a normal FileStream.
    /// </summary>
    /// <remarks>
    /// Both are seekable Streams with a known Length, which TFTP needs (tsize, and seeking
    /// back to resend a block) and HTTP needs (Content-Length).
    /// </remarks>
    public Stream Open(string fullPath, string serverIp, int httpPort)
    {
        if (!IsTemplate(fullPath))
            // FileShare.Read: many clients can read the same boot.wim at once.
            // SequentialScan: tells Windows to read ahead, which suits whole-file downloads.
            return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);

        var httpBase = httpPort == 80 ? $"http://{serverIp}" : $"http://{serverIp}:{httpPort}";
        var text = File.ReadAllText(fullPath)
            .Replace("{server}", serverIp, StringComparison.OrdinalIgnoreCase)
            .Replace("{http}", httpBase, StringComparison.OrdinalIgnoreCase);
        return new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false);
    }
}
