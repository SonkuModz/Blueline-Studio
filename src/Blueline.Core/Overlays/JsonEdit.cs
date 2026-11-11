using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blueline.Core.Overlays;

/// <summary>Reads and writes the game's UI JSON. Untouched numbers keep their exact text, so 94.0 stays 94.0.</summary>
public static class JsonEdit
{
    private static readonly JsonDocumentOptions Reading = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private static readonly JsonSerializerOptions Writing = new()
    {
        WriteIndented = true,
        IndentSize = 4,
        NewLine = "\r\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonNode Parse(byte[] content) => Parse(Encoding.UTF8.GetString(content));

    /// <summary>Some of the game's layouts repeat a key inside one object. The last one is kept, in the first one's place.</summary>
    public static JsonNode Parse(string text)
    {
        using var document = JsonDocument.Parse(text.TrimStart('﻿'), Reading);
        return Copy(document.RootElement) ?? throw new InvalidDataException("The file is empty.");
    }

    private static JsonNode? Copy(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var result = new JsonObject();
                foreach (var property in element.EnumerateObject())
                    result[property.Name] = Copy(property.Value);
                return result;
            case JsonValueKind.Array:
                var list = new JsonArray();
                foreach (var item in element.EnumerateArray())
                    list.Add(Copy(item));
                return list;
            case JsonValueKind.Null:
                return null;
            default:
                return JsonValue.Create(element.Clone());
        }
    }

    /// <summary>Null when the text is fine, otherwise what is wrong with it.</summary>
    public static string? Problem(string text)
    {
        try
        {
            Parse(text);
            return null;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            return e.Message;
        }
    }

    public static byte[] ToBytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString(Writing));

    public static string ToText(JsonNode? node) => node?.ToJsonString() ?? "";

    /// <summary>Turns typed text into a JSON value of the same type as the one it replaces, so "12" stays a string where the game had a string.</summary>
    public static JsonNode? Value(string text, JsonNode? previous)
    {
        if (previous is JsonObject or JsonArray || text.StartsWith('{') || text.StartsWith('['))
            return Parse(text);

        var kind = previous?.GetValueKind();
        if (kind == JsonValueKind.String)
            return JsonValue.Create(text);

        if (text is "true" or "false")
            return JsonValue.Create(text == "true");

        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) && Problem(text) == null)
        {
            var wasDecimal = kind == JsonValueKind.Number && previous!.ToJsonString().Contains('.');
            return Parse(wasDecimal && !text.Contains('.') && !text.Contains('e', StringComparison.OrdinalIgnoreCase) ? text + ".0" : text);
        }
        return JsonValue.Create(text);
    }

    public static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
            return null;

        // Values read from a file hold doubles, but ones made in code keep the type they were made from.
        return value.TryGetValue<double>(out var number) ? number : double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string? String(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>Stores a number, written with a decimal point when it is a whole number, as the game's layouts are.</summary>
    public static JsonNode Number(double value)
    {
        var rounded = Math.Round(value, 2);
        var text = rounded.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
        return Parse(text);
    }
}
