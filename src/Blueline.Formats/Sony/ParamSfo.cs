using System.Buffers.Binary;
using System.Text;

namespace Blueline.Formats.Sony;

/// <summary>Reads `sce_sys/param.sfo`, the PS4 title metadata table. Read-only.</summary>
public sealed class ParamSfo
{
    private const int HeaderSize = 20;
    private const int EntrySize = 16;
    private const ushort Utf8Text = 0x0204;
    private const ushort Integer = 0x0404;

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    private ParamSfo() { }

    public IReadOnlyDictionary<string, string> Values => _values;

    public string? this[string key] => _values.GetValueOrDefault(key);

    public static ParamSfo Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || !data[..4].SequenceEqual("\0PSF"u8))
            throw new InvalidDataException("This is not a param.sfo file.");

        var keys = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var values = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        if (keys < 0 || values < 0 || count < 0 || keys > data.Length || values > data.Length || HeaderSize + (long)count * EntrySize > data.Length)
            throw new InvalidDataException("This param.sfo is damaged: its tables do not fit in the file.");

        var result = new ParamSfo();
        for (var i = 0; i < count; i++)
        {
            var entry = data.Slice(HeaderSize + i * EntrySize, EntrySize);
            var keyStart = keys + BinaryPrimitives.ReadUInt16LittleEndian(entry);
            var format = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            var valueStart = (long)values + BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (keyStart >= data.Length || length < 0 || valueStart + length > data.Length)
                throw new InvalidDataException("This param.sfo is damaged: an entry points outside the file.");

            var rest = data[keyStart..];
            var end = rest.IndexOf((byte)0);
            if (end < 0)
                throw new InvalidDataException("This param.sfo is damaged: a key has no end.");
            var key = Encoding.ASCII.GetString(rest[..end]);
            var value = data.Slice((int)valueStart, length);

            if (format == Utf8Text)
                result._values[key] = Text(value);
            else if (format == Integer && length == 4)
                result._values[key] = BinaryPrimitives.ReadUInt32LittleEndian(value).ToString();
        }
        return result;
    }

    /// <summary>The format says UTF-8, but NHL 19's title holds a bare Latin-1 ®, so fall back when UTF-8 fails.</summary>
    private static string Text(ReadOnlySpan<byte> value)
    {
        var end = value.IndexOf((byte)0);
        var bytes = end >= 0 ? value[..end] : value;
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
