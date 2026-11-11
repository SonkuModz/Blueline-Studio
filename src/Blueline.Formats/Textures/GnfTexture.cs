using System.Buffers.Binary;

namespace Blueline.Formats.Textures;

public enum TextureCodec { Bc1, Bc2, Bc3, Bc4, Bc5, Bc6H, Bc7, Rgba8, Bgra8, L8, Rgba32F, Unsupported }

public enum GnfDimension { Texture2D, Cube, Array2D, Other }

/// <summary>The fields of a GNF's Gnm texture descriptor that NHL 19 actually uses.</summary>
public sealed record GnfInfo(
    int Width, int Height, int Depth, int Pitch, int MipCount, int Faces,
    int DataFormat, int NumberFormat, int TileMode, GnfDimension Dimension, int PixelBytes)
{
    public TextureCodec Codec => DataFormat switch
    {
        35 => TextureCodec.Bc1,
        36 => TextureCodec.Bc2,
        37 => TextureCodec.Bc3,
        38 => TextureCodec.Bc4,
        39 => TextureCodec.Bc5,
        40 => TextureCodec.Bc6H,
        41 => TextureCodec.Bc7,
        10 => TextureCodec.Rgba8,
        _ => TextureCodec.Unsupported,
    };

    public bool IsSrgb => NumberFormat == 9;
    public bool IsSigned => NumberFormat == 1;

    public string NumberFormatName => NumberFormat switch { 0 => "UNorm", 1 => "SNorm", 9 => "sRGB", _ => $"#{NumberFormat}" };

    public string TileModeName => TileMode == GnmTiling.Thin1D ? "Thin 1D" : $"#{TileMode}";

    /// <summary>"BC7 sRGB".</summary>
    public string FormatName => $"{Codec.ToString().ToUpperInvariant()} {NumberFormatName}";

    public string Summary => $"{Width}x{Height}, {FormatName}, {MipCount} mip{(MipCount == 1 ? "" : "s")}"
        + (Faces > 1 ? $", {Faces} {(Dimension == GnfDimension.Cube ? "faces" : "slices")}" : "");
}

/// <summary>
/// A GNF file: 0x100-byte header followed by tiled pixel data. See docs/RESEARCH.md section 5.
/// </summary>
public sealed class GnfTexture
{
    public const int HeaderSize = 0x100;
    private const int DescriptorOffset = 0x10;

    private GnfTexture(byte[] file, GnfInfo info)
    {
        File = file;
        Info = info;
    }

    public byte[] File { get; }
    public GnfInfo Info { get; }

    public static bool IsGnf(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual("GNF "u8);

    public static GnfInfo ReadInfo(ReadOnlySpan<byte> head)
    {
        if (!IsGnf(head) || head.Length < DescriptorOffset + 32)
            throw new InvalidDataException("Not a GNF file.");

        var contents = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
        var count = head[9];
        if (contents != 248 || count != 1)
            throw new InvalidDataException($"Unexpected GNF layout: contents size {contents}, {count} textures.");

        var word = head[DescriptorOffset..];
        var w1 = BinaryPrimitives.ReadUInt32LittleEndian(word[4..]);
        var w2 = BinaryPrimitives.ReadUInt32LittleEndian(word[8..]);
        var w3 = BinaryPrimitives.ReadUInt32LittleEndian(word[12..]);
        var w4 = BinaryPrimitives.ReadUInt32LittleEndian(word[16..]);
        var w5 = BinaryPrimitives.ReadUInt32LittleEndian(word[20..]);
        var w7 = BinaryPrimitives.ReadUInt32LittleEndian(word[28..]);
        var baseMip = (int)(w3 >> 12) & 0xF;
        var lastMip = (int)(w3 >> 16) & 0xF;
        var type = (int)(w3 >> 28) & 0xF;
        var depth = (int)(w4 & 0x1FFF) + 1;
        var pitch = (int)((w4 >> 13) & 0x3FFF) + 1;
        var lastSlice = (int)(w5 >> 13) & 0x1FFF;

        var dimension = type switch { 9 => GnfDimension.Texture2D, 11 => GnfDimension.Cube, 13 => GnfDimension.Array2D, _ => GnfDimension.Other };
        var faces = dimension switch { GnfDimension.Cube => 6, GnfDimension.Array2D => lastSlice + 1, _ => 1 };

        return new GnfInfo(
            (int)(w2 & 0x3FFF) + 1,
            (int)((w2 >> 14) & 0x3FFF) + 1,
            depth, pitch, lastMip - baseMip + 1, faces,
            (int)(w1 >> 20) & 0x3F,
            (int)(w1 >> 26) & 0xF,
            (int)(w3 >> 20) & 0x1F,
            dimension,
            (int)w7);
    }

    public static GnfTexture Parse(byte[] file)
    {
        var info = ReadInfo(file);
        return new GnfTexture(file, info);
    }

    /// <summary>
    /// Why this texture cannot be decoded, or null if it can. Unlike a decode failure, this is checked
    /// up front and reported in the UI.
    /// </summary>
    public string? DecodeBlocker()
    {
        if (Info.TileMode != GnmTiling.Thin1D)
            return $"Tile mode {Info.TileMode} has not been seen in NHL 19 and is not implemented.";
        if (Info.Codec == TextureCodec.Unsupported)
            return $"Data format {Info.DataFormat} is not implemented.";
        if (Info.Dimension == GnfDimension.Other)
            return "This texture type is not implemented.";
        var needed = GnmTiling.TotalBytes(Info);
        if (File.Length < HeaderSize + needed)
            return $"The file holds {File.Length - HeaderSize:N0} bytes of pixels but its descriptor needs {needed:N0}.";
        return null;
    }

    /// <summary>Compressed blocks in raster order for one mip and face.</summary>
    public byte[] DetiledSurface(int mip, int face = 0)
    {
        var offset = HeaderSize + GnmTiling.SurfaceOffset(Info, mip, face);
        return GnmTiling.Detile(File.AsSpan((int)offset), Info, mip);
    }

    public int MipWidth(int mip) => Math.Max(1, Info.Width >> mip);
    public int MipHeight(int mip) => Math.Max(1, Info.Height >> mip);

    /// <summary>
    /// A new GNF identical in header to <paramref name="template"/> with new pixels. Each entry in
    /// <paramref name="mips"/> is one mip of compressed blocks in raster order.
    /// </summary>
    public static byte[] Rebuild(GnfTexture template, IReadOnlyList<byte[]> mips)
    {
        var info = template.Info;
        if (info.Faces != 1)
            throw new NotSupportedException("Only single-face textures can be rebuilt.");
        if (mips.Count != info.MipCount)
            throw new ArgumentException($"Expected {info.MipCount} mips, got {mips.Count}.", nameof(mips));

        var pixels = GnmTiling.TotalBytes(info);
        var output = new byte[HeaderSize + pixels];
        template.File.AsSpan(0, HeaderSize).CopyTo(output);
        for (var mip = 0; mip < mips.Count; mip++)
        {
            var destination = output.AsSpan((int)(HeaderSize + GnmTiling.SurfaceOffset(info, mip, 0)));
            GnmTiling.Tile(mips[mip], destination, info, mip);
        }
        return output;
    }
}
