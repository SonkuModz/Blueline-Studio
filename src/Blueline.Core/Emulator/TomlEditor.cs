using System.Text;
using System.Text.RegularExpressions;

namespace Blueline.Core.Emulator;

/// <summary>
/// Changes single values in a TOML file and leaves every other line exactly as it was, so settings this tool
/// knows nothing about survive. Handles what shadPS4 writes: `[Section]` headers and `key = value` lines.
/// </summary>
public sealed class TomlEditor : IConfigEditor
{
    private static readonly Regex Header = new(@"^\s*\[(?<name>[^\[\]]+)\]\s*(#.*)?$", RegexOptions.Compiled);
    private static readonly Regex Entry = new(@"^\s*(?<key>[A-Za-z0-9_-]+)\s*=\s*(?<value>.*?)\s*$", RegexOptions.Compiled);

    private readonly List<string> _lines;
    private readonly string _newline;

    public TomlEditor(string text)
    {
        _newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        _lines = [.. text.Split('\n').Select(l => l.TrimEnd('\r'))];
    }

    public static TomlEditor Load(string path) => new(File.ReadAllText(path));

    /// <summary>The value as written in the file, with quotes, or null when absent.</summary>
    public string? GetRaw(string section, string key)
    {
        var index = Find(section, key);
        return index < 0 ? null : Entry.Match(_lines[index]).Groups["value"].Value;
    }

    public void SetRaw(string section, string key, string value)
    {
        var index = Find(section, key);
        if (index >= 0)
        {
            _lines[index] = $"{key} = {value}";
            return;
        }

        var header = _lines.FindIndex(l => Header.Match(l) is { Success: true } m && m.Groups["name"].Value == section);
        if (header < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Length == 0)
                _lines.RemoveAt(_lines.Count - 1);
            _lines.AddRange(["", $"[{section}]", $"{key} = {value}", ""]);
            return;
        }

        // After the section's last setting, ahead of any blank lines that separate it from the next section.
        var next = header + 1;
        while (next < _lines.Count && !Header.IsMatch(_lines[next]))
            next++;
        while (next > header + 1 && _lines[next - 1].Trim().Length == 0)
            next--;
        _lines.Insert(next, $"{key} = {value}");
    }

    public override string ToString() => string.Join(_newline, _lines);

    private int Find(string section, string key)
    {
        string? current = null;
        for (var i = 0; i < _lines.Count; i++)
        {
            var header = Header.Match(_lines[i]);
            if (header.Success)
            {
                current = header.Groups["name"].Value;
                continue;
            }
            if (current == section && Entry.Match(_lines[i]) is { Success: true } entry && entry.Groups["key"].Value == key)
                return i;
        }
        return -1;
    }

    /// <summary>Writes through a temporary file, so a failure cannot leave half a config behind.</summary>
    public void Save(string path)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, ToString(), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
