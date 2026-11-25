using System.Text;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

/// <summary>Edits selected Mercury INI keys without rewriting other settings.</summary>
public sealed class MercurySettingsFile
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly byte[] Bom = [0xef, 0xbb, 0xbf];
    private static readonly Regex Section = new(@"^\s*\[([^\]]+)\]\s*(?:[;#].*)?$");
    private static readonly Regex Entry = new(@"^(\s*)([a-zA-Z0-9_]+)\s*=\s*(""(?:[^""]*)""|'(?:[^']*)'|[^;#]*?)(\s*(?:[;#].*)?)$");
    private readonly byte[]? _original;
    private readonly List<string> _lines;
    private readonly string _newline;
    private readonly bool _hasBom;
    private readonly bool _finalNewline;
    public string Path { get; }

    private MercurySettingsFile(string path, byte[]? original)
    {
        Path = path;
        _original = original;
        _hasBom = original is not null && original.AsSpan().StartsWith(Bom);
        string text = original is null ? "" : Utf8.GetString(original.AsSpan(_hasBom ? Bom.Length : 0));
        if (text.Contains('\0')) throw new InvalidDataException("Choose a Mercury UTF-8 INI file.");
        _newline = text.Contains("\r\n") ? "\r\n" : "\n";
        _finalNewline = text.EndsWith('\n') || original is null;
        _lines = text.Length == 0 ? [] : text.Replace("\r\n", "\n").Split('\n').ToList();
        if (_finalNewline && _lines.Count > 0) _lines.RemoveAt(_lines.Count - 1);
    }

    public static MercurySettingsFile Open(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!System.IO.Path.GetExtension(path).Equals(".ini", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a Mercury .ini file.");
        return new MercurySettingsFile(path, ReadCurrent(path));
    }

    public string Get(string section, string key, string fallback = "")
    {
        int index = Find(section, key, out _);
        if (index < 0) return fallback;
        return Entry.Match(_lines[index]).Groups[3].Value.Trim().Trim('"', '\'');
    }

    public MercurySettingsFile BeginEdit() => new(Path, _original);

    public void Set(string section, string key, string value)
    {
        if (!Regex.IsMatch(section, @"\A[a-z][a-z0-9_]*\z") || !Regex.IsMatch(key, @"\A[a-z][a-z0-9_]*\z") ||
            value.Length > 512 || value.Any(char.IsControl) || value.Contains('"'))
            throw new ArgumentException("Mercury setting contains unsupported characters.");
        string encoded = value.Any(c => char.IsWhiteSpace(c) || c is ';' or '#' or '=' or '\'') ? $"\"{value}\"" : value;
        int index = Find(section, key, out int end);
        if (index >= 0)
        {
            var original = Entry.Match(_lines[index]);
            _lines[index] = $"{original.Groups[1].Value}{original.Groups[2].Value} = {encoded}{original.Groups[4].Value}";
        }
        else
        {
            if (end < 0)
            {
                if (_lines.Count > 0 && _lines[^1].Length != 0) _lines.Add("");
                _lines.Add($"[{section}]");
                end = _lines.Count;
            }
            _lines.Insert(end, $"{key} = {encoded}");
        }
    }

    private int Find(string section, string key, out int end)
    {
        bool inSection = false;
        int index = -1;
        end = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            var header = Section.Match(_lines[i]);
            if (header.Success) inSection = header.Groups[1].Value.Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
            if (!inSection) continue;
            end = i + 1;
            var entry = Entry.Match(_lines[i]);
            if (entry.Success && entry.Groups[2].Value.Equals(key, StringComparison.OrdinalIgnoreCase)) index = i;
        }
        return index;
    }

    public string? Save()
    {
        byte[] text = Utf8.GetBytes(string.Join(_newline, _lines) + (_finalNewline ? _newline : ""));
        if (_hasBom) text = [.. Bom, .. text];
        if (_original is not null && _original.AsSpan().SequenceEqual(text)) return null;
        if (!StillCurrent()) throw new IOException("Mercury's config changed while the editor was open. Reopen it before saving.");
        string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                file.Write(text);
                file.Flush(flushToDisk: true);
            }
            if (_original is null)
            {
                File.Move(temporary, Path);
                return null;
            }
            if (!StillCurrent()) throw new IOException("Mercury's config changed while the editor was open. Reopen it before saving.");
            string backup = Path + "." + Guid.NewGuid().ToString("N")[..8] + ".bak";
            File.Replace(temporary, Path, backup);
            return backup;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private bool StillCurrent()
    {
        byte[]? bytes = ReadCurrent(Path);
        return _original is null ? bytes is null : bytes is not null && bytes.AsSpan().SequenceEqual(_original);
    }

    private static byte[]? ReadCurrent(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > 256 * 1024) throw new InvalidDataException("Mercury configuration exceeds the 256 KB limit.");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
    }
}
