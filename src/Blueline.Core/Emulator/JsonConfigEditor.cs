using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blueline.Core.Emulator;

/// <summary>A settings file read and changed by section and key. Values are raw literals: `true`, `720`, `"Mailbox"`.</summary>
public interface IConfigEditor
{
    /// <summary>The value as written in the file, with quotes, or null when absent.</summary>
    string? GetRaw(string section, string key);
    void SetRaw(string section, string key, string value);
    void Save(string path);
}

/// <summary>
/// Changes single values in a shadPS4 JSON file and keeps everything else, including key order, number text,
/// line endings and settings this tool knows nothing about.
/// </summary>
public sealed class JsonConfigEditor : IConfigEditor
{
    private readonly JsonObject _root;
    private readonly string _newline;
    private readonly string _ending;
    private readonly char _indentCharacter = ' ';
    private readonly int _indentSize = 2;

    public JsonConfigEditor(string text)
    {
        _newline = text.Contains("\r\n", StringComparison.Ordinal) || !text.Contains('\n') ? "\r\n" : "\n";
        var trimmed = text.TrimEnd('\r', '\n');
        _ending = text[trimmed.Length..];
        var indent = System.Text.RegularExpressions.Regex.Match(text, "\\n([ \\t]+)\"");
        if (indent.Success)
        {
            _indentCharacter = indent.Groups[1].Value[0];
            _indentSize = indent.Groups[1].Length;
        }
        try
        {
            _root = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("This settings file does not hold a JSON object.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"This settings file is not valid JSON: {e.Message}", e);
        }
    }

    public static JsonConfigEditor Load(string path) => new(File.ReadAllText(path));

    public string? GetRaw(string section, string key) =>
        _root[section] is JsonObject values && values.TryGetPropertyValue(key, out var value) ? value?.ToJsonString() : null;

    public void SetRaw(string section, string key, string value)
    {
        if (_root[section] is not JsonObject values)
        {
            values = [];
            _root[section] = values;
        }
        values[key] = JsonNode.Parse(value);
    }

    public override string ToString()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            IndentCharacter = _indentCharacter,
            IndentSize = _indentSize,
            NewLine = _newline,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
            _root.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.ToArray()) + _ending;
    }

    /// <summary>Writes through a temporary file, so a failure cannot leave half a config behind.</summary>
    public void Save(string path)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, ToString(), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Reads as the emulator does: the per-game file first, then the shared file for anything it leaves out.</summary>
public sealed class LayeredConfig(IConfigEditor top, IConfigEditor bottom) : IConfigEditor
{
    public string? GetRaw(string section, string key) => top.GetRaw(section, key) ?? bottom.GetRaw(section, key);
    public void SetRaw(string section, string key, string value) => top.SetRaw(section, key, value);
    public void Save(string path) => top.Save(path);
}
