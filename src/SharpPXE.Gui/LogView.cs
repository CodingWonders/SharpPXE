// =====================================================================================
//  LogView.cs - the live, colored server log
// =====================================================================================
//
//  Server output arrives on background threads (process pipes, file tailing), but WinForms
//  controls may only be touched from the UI thread. So lines go into a thread-safe queue,
//  and a 100 ms UI timer moves them into the RichTextBox in batches, with redrawing paused
//  during each batch. This stays smooth even when many clients boot at once.
// =====================================================================================

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SharpPXE.Gui;

public sealed class LogView : RichTextBox
{
    const int MaxLines = 5000;
    readonly ConcurrentQueue<string> _pending = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };

    public LogView()
    {
        ReadOnly = true;
        WordWrap = false;
        DetectUrls = false;
        HideSelection = false;
        BorderStyle = BorderStyle.None;
        BackColor = SystemColors.Window;
        Font = new Font(FontFamily.Families.Any(f => f.Name == "Cascadia Mono") ? "Cascadia Mono" : "Consolas", 9f);
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    /// <summary>Thread-safe: queue a line for display.</summary>
    public void Enqueue(string line) => _pending.Enqueue(line);

    public void ClearLog() { _pending.Clear(); Clear(); }

    void Flush()
    {
        if (_pending.IsEmpty || !IsHandleCreated) return;
        SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            int n = 0;
            while (n++ < 1000 && _pending.TryDequeue(out var line))
            {
                SelectionStart = TextLength;
                SelectionLength = 0;
                SelectionColor = ColorFor(line);
                AppendText(line + "\n");
            }
            Trim();
            SelectionStart = TextLength;
            ScrollToCaret();
        }
        finally
        {
            SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            Invalidate();
        }
    }

    void Trim()
    {
        int lines = GetLineFromCharIndex(TextLength);
        if (lines <= MaxLines) return;
        int cut = GetFirstCharIndexFromLine(lines - MaxLines + MaxLines / 5);
        ReadOnly = false;
        Select(0, cut);
        SelectedText = "";
        ReadOnly = true;
    }

    /// <summary>Lines look like "12:34:56.789 WRN message" (with a date in front in the service log file).</summary>
    static Color ColorFor(string line)
    {
        if (line.Contains(" ERR ") || line.StartsWith("ERR ")) return Color.Firebrick;
        if (line.Contains(" WRN ")) return Color.DarkOrange;
        if (line.Contains(" DBG ")) return Color.Gray;
        if (line.Contains(" OFFER ") || line.Contains(" ACK ")) return Color.RoyalBlue;
        if (line.Contains(": sent '")) return Color.SeaGreen;
        if (line.StartsWith("--")) return Color.DimGray;
        return SystemColors.WindowText;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    const int WM_SETREDRAW = 0x000B;
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
