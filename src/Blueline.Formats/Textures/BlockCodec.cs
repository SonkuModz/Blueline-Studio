using System.Buffers.Binary;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;

namespace Blueline.Formats.Textures;

/// <summary>Converts between BC blocks in raster order and RGBA8 pixels.</summary>
public static class BlockCodec
{
    /// <summary>Whether an import may target this codec.</summary>
    public static bool CanEncode(TextureCodec codec) => codec is TextureCodec.Bc1 or TextureCodec.Bc3
        or TextureCodec.Bc4 or TextureCodec.Bc5 or TextureCodec.Bc7 or TextureCodec.Rgba8 or TextureCodec.Bgra8 or TextureCodec.L8;

    private static byte[] SwapRedBlue(ReadOnlySpan<byte> pixels)
    {
        var output = pixels.ToArray();
        for (var i = 0; i + 4 <= output.Length; i += 4)
            (output[i], output[i + 2]) = (output[i + 2], output[i]);
        return output;
    }

    private static byte[] ExpandLuminance(ReadOnlySpan<byte> values)
    {
        var output = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            output[i * 4] = output[i * 4 + 1] = output[i * 4 + 2] = values[i];
            output[i * 4 + 3] = 255;
        }
        return output;
    }

    /// <summary>The weights add up to 256, so a grey picture comes back with exactly the same values.</summary>
    private static byte[] ToLuminance(byte[] rgba)
    {
        var output = new byte[rgba.Length / 4];
        for (var i = 0; i < output.Length; i++)
            output[i] = (byte)((rgba[i * 4] * 77 + rgba[i * 4 + 1] * 150 + rgba[i * 4 + 2] * 29 + 128) >> 8);
        return output;
    }

    /// <summary>For viewing only. These are lookup data rather than HDR colour, so the values are clamped and not tone mapped.</summary>
    private static byte[] NarrowFloats(ReadOnlySpan<byte> floats)
    {
        var output = new byte[floats.Length / 4];
        for (var i = 0; i < output.Length; i++)
            output[i] = (byte)Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(floats[(i * 4)..]) * 255 + 0.5f, 0, 255);
        return output;
    }

    public static byte[] Decode(ReadOnlySpan<byte> blocks, int width, int height, TextureCodec codec, bool signed)
    {
        if (codec == TextureCodec.Rgba8)
            return blocks[..(width * height * 4)].ToArray();
        if (codec == TextureCodec.Bgra8)
            return SwapRedBlue(blocks[..(width * height * 4)]);
        if (codec == TextureCodec.L8)
            return ExpandLuminance(blocks[..(width * height)]);
        if (codec == TextureCodec.Rgba32F)
            return NarrowFloats(blocks[..(width * height * 16)]);

        var input = blocks.ToArray();
        if (signed)
            FlipSignedEndpoints(input, codec);

        var decoder = new BcDecoder();
        var output = new byte[width * height * 4];

        if (codec == TextureCodec.Bc6H)
        {
            // Signed vs unsigned BC6H: NHL 19 only ships number format 0 for BC6H, so unsigned is used.
            var hdr = decoder.DecodeRawHdr(input, width, height, CompressionFormat.Bc6U);
            for (var i = 0; i < hdr.Length; i++)
            {
                output[i * 4] = ToneMap(hdr[i].r);
                output[i * 4 + 1] = ToneMap(hdr[i].g);
                output[i * 4 + 2] = ToneMap(hdr[i].b);
                output[i * 4 + 3] = 255;
            }
            return output;
        }

        var pixels = decoder.DecodeRaw(input, width, height, Map(codec));
        for (var i = 0; i < pixels.Length; i++)
        {
            output[i * 4] = pixels[i].r;
            output[i * 4 + 1] = pixels[i].g;
            output[i * 4 + 2] = pixels[i].b;
            output[i * 4 + 3] = pixels[i].a;
        }

        if (codec == TextureCodec.Bc4)
        {
            for (var i = 0; i < pixels.Length; i++)
            {
                output[i * 4 + 1] = output[i * 4];
                output[i * 4 + 2] = output[i * 4];
                output[i * 4 + 3] = 255;
            }
        }
        return output;
    }

    /// <summary>Encodes one mip to blocks in raster order.</summary>
    public static byte[] Encode(byte[] rgba, int width, int height, TextureCodec codec, bool signed, CompressionQuality quality)
    {
        if (!CanEncode(codec))
            throw new NotSupportedException($"{codec} cannot be encoded by this build.");
        if (codec == TextureCodec.Rgba8)
            return rgba;
        if (codec == TextureCodec.Bgra8)
            return SwapRedBlue(rgba);
        if (codec == TextureCodec.L8)
            return ToLuminance(rgba);

        var encoder = new BcEncoder();
        encoder.OutputOptions.Format = Map(codec);
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = quality;
        encoder.Options.IsParallel = true;

        var mips = encoder.EncodeToRawBytes(rgba, width, height, PixelFormat.Rgba32);
        var blocks = mips[0];
        if (signed)
            FlipSignedEndpoints(blocks, codec);
        return blocks;
    }

    /// <summary>
    /// Signed BC4/BC5 stores its two endpoints as int8. Offsetting by 128 (flipping the top bit) gives
    /// the equivalent unsigned block and preserves the endpoint order that selects the block's mode.
    /// </summary>
    private static void FlipSignedEndpoints(byte[] blocks, TextureCodec codec)
    {
        if (codec is not (TextureCodec.Bc4 or TextureCodec.Bc5))
            return;
        for (var i = 0; i + 8 <= blocks.Length; i += 8)
        {
            blocks[i] ^= 0x80;
            blocks[i + 1] ^= 0x80;
        }
    }

    private static byte ToneMap(float value)
    {
        var mapped = value / (1 + value);
        return (byte)Math.Clamp(MathF.Pow(mapped, 1 / 2.2f) * 255 + 0.5f, 0, 255);
    }

    private static CompressionFormat Map(TextureCodec codec) => codec switch
    {
        TextureCodec.Bc1 => CompressionFormat.Bc1,
        TextureCodec.Bc2 => CompressionFormat.Bc2,
        TextureCodec.Bc3 => CompressionFormat.Bc3,
        TextureCodec.Bc4 => CompressionFormat.Bc4,
        TextureCodec.Bc5 => CompressionFormat.Bc5,
        TextureCodec.Bc7 => CompressionFormat.Bc7,
        _ => throw new NotSupportedException(codec.ToString()),
    };
}
