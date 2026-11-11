using System.Buffers.Binary;
using System.Text;

namespace Blueline.Core.Overlays;

/// <summary>How a label looks once its named styles are applied.</summary>
public sealed record LabelLook(string Face, double Size, string? Colour, string? AlignH, string? AlignV, string? Resize, bool ForceCaps, double Leading, double Tracking = 0);

/// <summary>A font's line measurements as a share of its size, read from its "hhea" table.</summary>
public readonly record struct FontLine(double Ascent, double Descent)
{
    private const int TableListStart = 12;
    private const int TableEntrySize = 16;

    /// <summary>Reads a TrueType or OpenType font, or returns null when the file does not have what is needed.</summary>
    public static FontLine? Read(ReadOnlySpan<byte> font)
    {
        if (font.Length < TableListStart)
            return null;
        var tableCount = BinaryPrimitives.ReadUInt16BigEndian(font[4..]);
        int head = -1, hhea = -1;
        for (var i = 0; i < tableCount && TableListStart + (i + 1) * TableEntrySize <= font.Length; i++)
        {
            var entry = font.Slice(TableListStart + i * TableEntrySize, TableEntrySize);
            var offset = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(entry[8..]), int.MaxValue);
            var name = Encoding.ASCII.GetString(entry[..4]);
            if (name == "head")
                head = offset;
            else if (name == "hhea")
                hhea = offset;
        }
        if (head < 0 || hhea < 0 || head > font.Length - 20 || hhea > font.Length - 8)
            return null;
        var unitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(font[(head + 18)..]);
        if (unitsPerEm == 0)
            return null;
        return new FontLine(BinaryPrimitives.ReadInt16BigEndian(font[(hhea + 4)..]) / (double)unitsPerEm,
            Math.Abs(BinaryPrimitives.ReadInt16BigEndian(font[(hhea + 6)..])) / (double)unitsPerEm);
    }
}

/// <summary>
/// Where the game's text engine puts a line inside a label's box. Flash text fields keep a 2 pixel gutter inside the
/// box and measure lines with the font's "hhea" values. Top alignment is measured against an in-game score clock
/// (RESEARCH section 6.9). Centre and bottom follow the same rules but have not been compared with the game.
/// </summary>
public static class LabelPlacement
{
    public const double Gutter = 2;

    /// <summary>Distance from the top of the box to the first line's baseline.</summary>
    public static double Baseline(FontLine line, double size, double boxHeight, string? alignV, int lines = 1, double leading = 0)
    {
        var rise = line.Ascent * size;
        var block = BlockHeight(line, size, lines, leading);
        return alignV switch
        {
            "BOTTOM" => boxHeight - Gutter - block + rise,
            "CENTER" => (boxHeight - block) / 2 + rise,
            _ => Gutter + rise,
        };
    }

    public static double BlockHeight(FontLine line, double size, int lines = 1, double leading = 0) =>
        lines * (line.Ascent + line.Descent) * size + (lines - 1) * leading;
}
