using System.Buffers.Binary;
using System.IO.Compression;

namespace Blueline.Formats.Flash;

/// <summary>One tag of a Flash file: its code and its body, without the tag header.</summary>
public sealed class SwfTag(int code, byte[] body)
{
    public const int DoAbc = 82;
    public const int End = 0;

    public int Code { get; } = code;
    public byte[] Body { get; set; } = body;
}

/// <summary>
/// A Flash movie (FWS plain or CWS zlib), split into its tags so one can be replaced. Everything else is kept as read:
/// the signature, version, the frame rectangle, rate and count, and the order and bodies of the other tags. Tag headers are
/// written in the short form when the body fits (under 63 bytes) and the long form otherwise, except where the original used
/// the long form for a short body, which is kept.
/// </summary>
public sealed class SwfFile
{
    private readonly byte[] _frameHeader;
    private readonly List<bool> _longHeaders;

    private SwfFile(char signature, byte version, byte[] frameHeader, List<SwfTag> tags, List<bool> longHeaders)
    {
        Signature = signature;
        Version = version;
        _frameHeader = frameHeader;
        Tags = tags;
        _longHeaders = longHeaders;
    }

    /// <summary>'F' for an uncompressed movie, 'C' for zlib.</summary>
    public char Signature { get; }
    public byte Version { get; }
    public List<SwfTag> Tags { get; }

    public static bool IsSwf(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && (data[0] == 'F' || data[0] == 'C') && data[1] == 'W' && data[2] == 'S';

    public static SwfFile Parse(byte[] data)
    {
        if (!IsSwf(data))
            throw new InvalidDataException("Not a FWS or CWS Flash file.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        byte[] body;
        if (data[0] == 'C')
        {
            using var input = new ZLibStream(new MemoryStream(data, 8, data.Length - 8), CompressionMode.Decompress);
            using var output = new MemoryStream((int)length);
            input.CopyTo(output);
            body = output.ToArray();
        }
        else
            body = data[8..];
        if (body.Length + 8 != length)
            throw new InvalidDataException($"The Flash file says it is {length:N0} bytes but holds {body.Length + 8:N0}.");

        // The frame rectangle is a bit field: 5 bits give the size of each of its four values. Then rate and count, 2 bytes each.
        var bits = body[0] >> 3;
        var headerLength = (5 + 4 * bits + 7) / 8 + 4;
        var tags = new List<SwfTag>();
        var longHeaders = new List<bool>();
        var at = headerLength;
        while (at < body.Length)
        {
            var head = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at));
            at += 2;
            int code = head >> 6, size = head & 63;
            var isLong = size == 63;
            if (isLong)
            {
                size = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(at));
                at += 4;
            }
            tags.Add(new SwfTag(code, body[at..(at + size)]));
            longHeaders.Add(isLong);
            at += size;
            if (code == SwfTag.End)
                break;
        }
        if (at != body.Length)
            throw new InvalidDataException($"The Flash file has {body.Length - at} bytes after its end tag.");
        return new SwfFile((char)data[0], data[3], body[..headerLength], tags, longHeaders);
    }

    /// <summary>The movie after the 8 byte file header, as it is before compression.</summary>
    public byte[] Body()
    {
        using var output = new MemoryStream();
        output.Write(_frameHeader);
        for (var i = 0; i < Tags.Count; i++)
        {
            var tag = Tags[i];
            var isLong = tag.Body.Length >= 63 || (i < _longHeaders.Count && _longHeaders[i]);
            Span<byte> head = stackalloc byte[6];
            BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)(tag.Code << 6 | (isLong ? 63 : tag.Body.Length)));
            if (isLong)
            {
                BinaryPrimitives.WriteInt32LittleEndian(head[2..], tag.Body.Length);
                output.Write(head);
            }
            else
                output.Write(head[..2]);
            output.Write(tag.Body);
        }
        return output.ToArray();
    }

    public byte[] ToBytes()
    {
        var body = Body();
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        header[0] = (byte)Signature;
        header[1] = (byte)'W';
        header[2] = (byte)'S';
        header[3] = Version;
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(body.Length + 8));
        output.Write(header);
        if (Signature == 'C')
        {
            using var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true);
            zlib.Write(body);
        }
        else
            output.Write(body);
        return output.ToArray();
    }

    /// <summary>The ABC bytecode block of a DoABC tag: after its 4 byte flags and its null terminated name.</summary>
    public static (int Start, string Name) AbcStart(byte[] doAbcBody)
    {
        var end = Array.IndexOf(doAbcBody, (byte)0, 4);
        if (end < 0)
            throw new InvalidDataException("A DoABC tag has no name.");
        return (end + 1, System.Text.Encoding.UTF8.GetString(doAbcBody, 4, end - 4));
    }
}
