using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Blueline.Formats.Scaleform;

/// <summary>A texture a movie loads from its own folder. The size is what the movie was authored with, and can be a little under the file's.</summary>
public sealed record GfxImage(string FileName, int Width, int Height);

/// <summary>Reads a Scaleform or Flash movie far enough to list the textures it loads. See RESEARCH section 6.9.</summary>
public sealed class GfxMovie
{
    private const int ExternalImageTag = 1009;
    private const int EndTag = 0;

    private GfxMovie(string signature, int version, IReadOnlyList<GfxImage> images)
    {
        Signature = signature;
        Version = version;
        Images = images;
    }

    public string Signature { get; }
    public int Version { get; }
    public IReadOnlyList<GfxImage> Images { get; }

    public static bool IsMovie(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && (data[..3].SequenceEqual("CFX"u8) || data[..3].SequenceEqual("GFX"u8) || data[..3].SequenceEqual("CWS"u8) || data[..3].SequenceEqual("FWS"u8));

    public static GfxMovie Parse(byte[] data)
    {
        if (!IsMovie(data))
            throw new InvalidDataException("Not a Scaleform or Flash movie.");

        return new GfxMovie(Encoding.ASCII.GetString(data, 0, 3), data[3], ReadImages(Body(data)));
    }

    /// <summary>The movie after its eight byte header, uncompressed.</summary>
    internal static byte[] Body(byte[] data)
    {
        if (!IsMovie(data))
            throw new InvalidDataException("Not a Scaleform or Flash movie.");
        return data[0] == 'C' ? Inflate(data) : data[8..];
    }

    private static byte[] Inflate(byte[] data)
    {
        using var zlib = new ZLibStream(new MemoryStream(data, 8, data.Length - 8), CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static List<GfxImage> ReadImages(byte[] body)
    {
        var images = new List<GfxImage>();
        var rectBits = 5 + (body[0] >> 3) * 4;
        var position = (rectBits + 7) / 8 + 4;
        while (position + 2 <= body.Length)
        {
            var header = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(position));
            position += 2;
            var code = header >> 6;
            var length = header & 63;
            if (length == 63)
            {
                length = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(position));
                position += 4;
            }
            if (length < 0 || position + length > body.Length)
                throw new InvalidDataException("The movie's tag list runs past its end.");
            if (code == ExternalImageTag)
                images.Add(ReadExternalImage(body.AsSpan(position, length)));
            position += length;
            if (code == EndTag)
                break;
        }
        return images;
    }

    /// <summary>u32 id, u16 format, u16 width, u16 height, then a short export name and the file name, each with a length byte.</summary>
    internal static GfxImage ReadExternalImage(ReadOnlySpan<byte> tag)
    {
        var width = BinaryPrimitives.ReadUInt16LittleEndian(tag[6..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(tag[8..]);
        var nameAt = 11 + tag[10];
        var name = Encoding.ASCII.GetString(tag.Slice(nameAt + 1, tag[nameAt]));
        return new GfxImage(name, width, height);
    }
}
