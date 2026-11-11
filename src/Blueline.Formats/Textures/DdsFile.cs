using System.Buffers.Binary;

namespace Blueline.Formats.Textures;

/// <summary>
/// Minimal DDS reader/writer for the shapes NHL 19 ships under `.gnf` names, and for exporting
/// compressed mips losslessly. Mip data is raster order with the standard DDS packing.
/// </summary>
public sealed class DdsFile
{
    private const int DdsHeader = 128;
    private const int Dx10Header = 20;

    private const uint CubemapCaps = 0x200;
    private const uint VolumeCaps = 0x200000;
    private const uint LuminanceFlag = 0x20000;
    private const int MiscTextureCube = 4;

    private DdsFile(byte[] data, int dataStart, int width, int height, int mipCount, TextureCodec codec, int layers, bool isVolume)
    {
        Data = data;
        DataStart = dataStart;
        Width = width;
        Height = height;
        MipCount = mipCount;
        Codec = codec;
        Layers = layers;
        IsVolume = isVolume;
    }

    public byte[] Data { get; }
    public int DataStart { get; }
    public int Width { get; }
    public int Height { get; }
    public int MipCount { get; }
    public TextureCodec Codec { get; }

    /// <summary>Cubemap faces, array entries or volume slices. One for a plain 2D texture.</summary>
    public int Layers { get; }
    public bool IsVolume { get; }

    public static bool IsDds(ReadOnlySpan<byte> data) => data.Length >= DdsHeader && data[..4].SequenceEqual("DDS "u8);

    public static DdsFile Parse(byte[] data)
    {
        if (!IsDds(data))
            throw new InvalidDataException("Not a DDS file.");

        var height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16));
        var mips = Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(28)));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(80));
        var fourCc = System.Text.Encoding.ASCII.GetString(data, 84, 4);
        var bits = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(88));
        var caps2 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(112));
        var isVolume = (caps2 & VolumeCaps) != 0;
        var isCube = (caps2 & CubemapCaps) != 0;
        var arraySize = 1;

        var start = DdsHeader;
        TextureCodec codec;
        if ((flags & 4) != 0 && fourCc == "DX10")
        {
            start += Dx10Header;
            isCube |= (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(DdsHeader + 8)) & MiscTextureCube) != 0;
            arraySize = Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(DdsHeader + 12)));
            codec = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(DdsHeader)) switch
            {
                71 or 72 => TextureCodec.Bc1,
                74 or 75 => TextureCodec.Bc2,
                77 or 78 => TextureCodec.Bc3,
                80 or 81 => TextureCodec.Bc4,
                83 or 84 => TextureCodec.Bc5,
                95 or 96 => TextureCodec.Bc6H,
                98 or 99 => TextureCodec.Bc7,
                2 => TextureCodec.Rgba32F,
                28 or 29 => TextureCodec.Rgba8,
                87 or 91 => TextureCodec.Bgra8,
                _ => TextureCodec.Unsupported,
            };
        }
        else if ((flags & 4) != 0)
        {
            codec = fourCc switch
            {
                "DXT1" => TextureCodec.Bc1,
                "DXT3" => TextureCodec.Bc2,
                "DXT5" => TextureCodec.Bc3,
                "ATI1" or "BC4U" => TextureCodec.Bc4,
                "ATI2" or "BC5U" => TextureCodec.Bc5,
                _ => TextureCodec.Unsupported,
            };
        }
        else
        {
            // Jersey name plates are BGRA. The post effects' noise and grain are 8-bit luminance.
            var redMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(92));
            codec = (bits, redMask) switch
            {
                (8, 0xFF) when (flags & LuminanceFlag) != 0 => TextureCodec.L8,
                (32, 0xFF) => TextureCodec.Rgba8,
                (32, 0xFF0000) => TextureCodec.Bgra8,
                _ => TextureCodec.Unsupported,
            };
        }

        var depth = Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24)));
        var layers = isVolume ? depth : (isCube ? 6 : 1) * arraySize;
        return new DdsFile(data, start, width, height, mips, codec, layers, isVolume);
    }

    private static long MipBytes(int width, int height, int mip, TextureCodec codec)
    {
        var w = Math.Max(1, width >> mip);
        var h = Math.Max(1, height >> mip);
        return codec switch
        {
            TextureCodec.L8 => (long)w * h,
            TextureCodec.Rgba8 or TextureCodec.Bgra8 => (long)w * h * 4,
            TextureCodec.Rgba32F => (long)w * h * 16,
            _ => (long)((w + 3) / 4) * ((h + 3) / 4) * GnmTiling.ElementBytes(codec),
        };
    }

    private int SlicesAt(int mip) => IsVolume ? Math.Max(1, Layers >> mip) : 1;

    /// <summary>One mip of one layer. Faces and array entries each hold a full mip chain; a volume stores all of a mip's slices together.</summary>
    public byte[] Mip(int mip, int layer = 0)
    {
        long offset = DataStart;
        if (!IsVolume)
        {
            for (var m = 0; m < MipCount; m++)
                offset += layer * MipBytes(Width, Height, m, Codec);
        }
        for (var m = 0; m < mip; m++)
            offset += MipBytes(Width, Height, m, Codec) * SlicesAt(m);

        var length = MipBytes(Width, Height, mip, Codec);
        if (IsVolume)
            offset += length * Math.Min(layer, SlicesAt(mip) - 1);
        if (offset + length > Data.Length)
            throw new InvalidDataException($"DDS is truncated: mip {mip} needs bytes up to {offset + length}, file is {Data.Length}.");
        return Data.AsSpan((int)offset, (int)length).ToArray();
    }

    /// <summary>Same header as <paramref name="template"/>, new mip data.</summary>
    public static byte[] Rebuild(DdsFile template, IReadOnlyList<byte[]> mips)
    {
        var output = new byte[template.DataStart + mips.Sum(m => m.Length)];
        template.Data.AsSpan(0, template.DataStart).CopyTo(output);
        var position = template.DataStart;
        foreach (var mip in mips)
        {
            mip.CopyTo(output, position);
            position += mip.Length;
        }
        return output;
    }

    /// <summary>A standalone DX10 DDS. The blocks are written exactly as given, so no quality is lost.</summary>
    public static byte[] Create(int width, int height, IReadOnlyList<byte[]> mips, TextureCodec codec, bool srgb, bool signed)
    {
        var dxgi = codec switch
        {
            TextureCodec.Bc1 => srgb ? 72 : 71,
            TextureCodec.Bc2 => srgb ? 75 : 74,
            TextureCodec.Bc3 => srgb ? 78 : 77,
            TextureCodec.Bc4 => signed ? 81 : 80,
            TextureCodec.Bc5 => signed ? 84 : 83,
            TextureCodec.Bc6H => 95,
            TextureCodec.Bc7 => srgb ? 99 : 98,
            TextureCodec.Rgba8 => srgb ? 29 : 28,
            TextureCodec.Bgra8 => srgb ? 91 : 87,
            _ => throw new NotSupportedException(codec.ToString()),
        };

        var output = new byte[DdsHeader + Dx10Header + mips.Sum(m => m.Length)];
        var span = output.AsSpan();
        "DDS "u8.CopyTo(span);
        void Put(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(offset), value);
        Put(4, 124);
        Put(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000);
        Put(12, height);
        Put(16, width);
        Put(28, mips.Count);
        Put(76, 32);
        Put(80, 4);
        "DX10"u8.CopyTo(span[84..]);
        Put(108, 0x1000 | 0x400000 | 0x8);
        Put(DdsHeader, dxgi);
        Put(DdsHeader + 4, 3);
        Put(DdsHeader + 12, 1);

        var position = DdsHeader + Dx10Header;
        foreach (var mip in mips)
        {
            mip.CopyTo(output, position);
            position += mip.Length;
        }
        return output;
    }
}
