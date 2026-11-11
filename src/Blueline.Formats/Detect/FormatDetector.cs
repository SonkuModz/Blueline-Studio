using System.Text;
using Blueline.Formats.Eb;
using Blueline.Formats.Textures;

namespace Blueline.Formats.Detect;

public enum AssetKind { Unknown, Texture, Model, Audio, Database, Text, Scaleform, Video, Font, Archive, Image, Animation, Attribute }

/// <summary>
/// Identifies content from its first bytes. Extensions lie in NHL 19 (1,515 `.gnf` files are DDS), so the
/// extension is only a fallback for unwrapped formats that have no signature.
/// </summary>
public static class FormatDetector
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".lua", ".xml", ".csv", ".txt", ".layout", ".menu", ".vvm", ".lreq", ".nav", ".dd", ".cfg", ".ini",
        ".def", ".schema", ".scrape", ".manifest", ".pop", ".gridnav", ".exclude", ".hint", ".brk", ".overlays",
    };

    public static (AssetKind Kind, string Label) Identify(ReadOnlySpan<byte> head, string path)
    {
        var ext = Path.GetExtension(path);
        if (GnfTexture.IsGnf(head)) return (AssetKind.Texture, "GNF texture");
        if (head.Length >= 4 && head[..4].SequenceEqual("DDS "u8)) return (AssetKind.Texture, "DDS texture");
        if (PngFile.IsPng(head)) return (AssetKind.Image, "PNG image");
        if (IsJpeg(head)) return (AssetKind.Image, "JPEG image");
        if (head.Length >= 4)
        {
            var tag = Encoding.Latin1.GetString(head[..4]);
            switch (tag)
            {
                case "SBle": return (AssetKind.Audio, "Sound bank index");
                case "data" when ext.Equals(".sbs", StringComparison.OrdinalIgnoreCase): return (AssetKind.Audio, "Sound bank samples");
                case "HGle": return (AssetKind.Audio, "Sample history");
                case "RSF\0": return (AssetKind.Model, ext.Equals(".mcd", StringComparison.OrdinalIgnoreCase) ? "Model" : "RSF data");
                case "MVhd": return (AssetKind.Video, "EA VP6 video");
                case "CWS\u000e" or "CWS\u000f": return (AssetKind.Scaleform, "Flash");
                case "CFX\u000f" or "CFX\u001c": return (AssetKind.Scaleform, "Scaleform");
                case "play": return (AssetKind.Unknown, "Play data");
                case "\u001aEß£": return (AssetKind.Video, "Matroska video");
                case "ST00": return (AssetKind.Text, "Subtitles");
            }
            if (head[0] == 'D' && head[1] == 'B' && head[2] == 0 && head[3] == 8) return (AssetKind.Database, "TDB database");
            if (head[0] == 'E' && head[1] == 'B' && head[2] == 0 && head[3] == 3) return (AssetKind.Archive, "EB archive");
        }

        if (ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase)) return (AssetKind.Font, "Font");
        if (ext.Equals(".cba", StringComparison.OrdinalIgnoreCase)) return (AssetKind.Animation, "Animation (undocumented)");
        if (ext.Equals(".vlt", StringComparison.OrdinalIgnoreCase)) return (AssetKind.Attribute, "Attribute vault (undocumented)");
        if (TextExtensions.Contains(ext)) return (AssetKind.Text, "Text");
        if (LooksLikeText(head)) return (AssetKind.Text, "Text");
        return (AssetKind.Unknown, ext.Length > 0 ? $"{ext.TrimStart('.').ToUpperInvariant()} data" : "Binary data");
    }

    public static bool IsJpeg(ReadOnlySpan<byte> head) => head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;

    public static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0) return false;
        var sample = head[..Math.Min(head.Length, 512)];
        foreach (var b in sample)
        {
            if (b == 0) return false;
            if (b < 9 || (b > 13 && b < 32)) return false;
        }
        return true;
    }
}
