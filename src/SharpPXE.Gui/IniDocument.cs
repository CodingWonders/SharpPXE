// =====================================================================================
//  IniDocument.cs - editing SharpPXE.ini without losing comments
// =====================================================================================
//
//  The INI file is the single source of truth: the server reads it, and people also edit
//  it by hand. So the GUI must not rewrite it from scratch, which would lose every comment
//  and the user's ordering. Instead the file is kept as a list of lines, and Set() changes
//  only the one line that holds a key:
//
//    1. If an active "key=value" line exists in the section, replace its value and keep
//       any trailing " ; comment".
//    2. Else, if a commented-out example exists ("; pool_start=192.168.1.100"), turn that
//       line into the real setting, so it stays where the user expects it.
//    3. Else, add the key at the end of the section (creating the section if needed).
//
//  The comment rules match Config.StripComment in the server.
// =====================================================================================

using System.Text;
using System.Text.RegularExpressions;

namespace SharpPXE.Gui;

public sealed partial class IniDocument
{
    readonly List<string> _lines;
    public string Path { get; }

    IniDocument(string path, List<string> lines) { Path = path; _lines = lines; }

    public static IniDocument Load(string path) =>
        new(path, File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>());

    public void Save() => File.WriteAllLines(Path, _lines, new UTF8Encoding(false));

    [GeneratedRegex(@"^\s*\[(?<name>[^\]]+)\]\s*$")]
    private static partial Regex SectionRx();

    // "key = value" where the key doesn't start with a comment character.
    [GeneratedRegex(@"^\s*(?<key>[^;#=\s\[][^=]*?)\s*=(?<rest>.*)$")]
    private static partial Regex KeyRx();

    /// <summary>Value of [section] key, or null if it isn't set.</summary>
    public string? Get(string section, string key)
    {
        var (start, end) = FindSection(section);
        if (start < 0) return null;
        for (int i = start + 1; i < end; i++)
            if (MatchKey(_lines[i], out var k, out var value, out _) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }

    /// <summary>Sets [section] key=value, keeping the file's layout and comments.</summary>
    public void Set(string section, string key, string value)
    {
        var (start, end) = FindSection(section);
        if (start < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0) _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return;
        }

        // 1. Active line
        for (int i = start + 1; i < end; i++)
            if (MatchKey(_lines[i], out var k, out _, out var comment) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = $"{k}={value}{comment}";
                return;
            }

        // 2. Commented-out example of the same key
        var commented = new Regex(@"^\s*[;#]\s*" + Regex.Escape(key) + @"\s*=", RegexOptions.IgnoreCase);
        for (int i = start + 1; i < end; i++)
            if (commented.IsMatch(_lines[i]))
            {
                _lines[i] = $"{key}={value}";
                return;
            }

        // 3. Add at the end of the section, before any trailing blank lines
        _lines.Insert(LastContentLine(start, end) + 1, $"{key}={value}");
    }

    /// <summary>All active key=value pairs in a section, in file order (used for [mac]).</summary>
    public List<(string Key, string Value)> GetEntries(string section)
    {
        var result = new List<(string, string)>();
        var (start, end) = FindSection(section);
        if (start < 0) return result;
        for (int i = start + 1; i < end; i++)
            if (MatchKey(_lines[i], out var k, out var v, out _)) result.Add((k, v));
        return result;
    }

    /// <summary>Replaces every active key in a section with the given list; comments in it are kept.</summary>
    public void SetEntries(string section, IEnumerable<(string Key, string Value)> entries)
    {
        var (start, end) = FindSection(section);
        if (start < 0)
        {
            var list = entries.ToList();
            if (list.Count == 0) return;
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0) _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.AddRange(list.Select(e => $"{e.Key}={e.Value}"));
            return;
        }
        for (int i = end - 1; i > start; i--)
            if (MatchKey(_lines[i], out _, out _, out _)) _lines.RemoveAt(i);
        (start, end) = FindSection(section);
        _lines.InsertRange(LastContentLine(start, end) + 1, entries.Select(e => $"{e.Key}={e.Value}"));
    }

    int LastContentLine(int start, int end)
    {
        int last = start;
        for (int i = start + 1; i < end; i++)
            if (_lines[i].Trim().Length > 0) last = i;
        return last;
    }

    /// <summary>Line index of the [section] header and the index where the section ends (exclusive).</summary>
    (int Start, int End) FindSection(string section)
    {
        int start = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            var m = SectionRx().Match(_lines[i]);
            if (!m.Success) continue;
            if (start >= 0) return (start, i);
            if (m.Groups["name"].Value.Trim().Equals(section, StringComparison.OrdinalIgnoreCase)) start = i;
        }
        return (start, _lines.Count);
    }

    /// <summary>Splits an active "key=value ; comment" line. Returns false for comments, headers and blanks.</summary>
    static bool MatchKey(string line, out string key, out string value, out string comment)
    {
        key = value = comment = "";
        var m = KeyRx().Match(line);
        if (!m.Success) return false;
        key = m.Groups["key"].Value.Trim();
        var rest = m.Groups["rest"].Value;
        int i = rest.IndexOf(" ;", StringComparison.Ordinal);
        int j = rest.IndexOf(" #", StringComparison.Ordinal);
        int cut = (i, j) switch { (< 0, < 0) => -1, (< 0, _) => j, (_, < 0) => i, _ => Math.Min(i, j) };
        if (cut >= 0)
        {
            // Keep the whitespace before the comment so the column alignment survives edits.
            int ws = cut;
            while (ws > 0 && rest[ws - 1] == ' ') ws--;
            comment = rest[ws..];
            rest = rest[..ws];
        }
        value = rest.Trim();
        return true;
    }
}
