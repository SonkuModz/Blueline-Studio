using System.Buffers.Binary;
using System.IO.Compression;

namespace Blueline.Formats.Textures;

/// <summary>
/// Small PNG codec with no UI dependency, so exports and batch imports work the same in the CLI, tests
/// and app. Reads 8-bit non-interlaced PNG, which covers every tool's default output; anything else is
/// refused with a clear message (the desktop app can still load those through Windows' imaging).
/// </summary>
public static class PngFile
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length > 8 && data[..8].SequenceEqual(Signature);

    public static byte[] Write(RgbaImage image, CompressionLevel level = CompressionLevel.Fastest)
    {
        using var output = new MemoryStream(image.Pixels.Length / 2 + 128);
        output.Write(Signature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), image.Height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(output, "IHDR", ihdr);

        using (var packed = new MemoryStream())
        {
            using (var z = new ZLibStream(packed, level, leaveOpen: true))
            {
                var stride = image.Width * 4;
                for (var y = 0; y < image.Height; y++)
                {
                    z.WriteByte(0);
                    z.Write(image.Pixels, y * stride, stride);
                }
            }
            WriteChunk(output, "IDAT", packed.ToArray());
        }
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    public static RgbaImage Read(byte[] data)
    {
        if (!IsPng(data))
            throw new InvalidDataException("Not a PNG file.");

        int width = 0, height = 0, type = 0;
        byte[]? palette = null, alpha = null;
        using var idat = new MemoryStream();
        var p = 8;
        while (p + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(p));
            var name = System.Text.Encoding.ASCII.GetString(data, p + 4, 4);
            var body = data.AsSpan(p + 8, length);
            switch (name)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    type = body[9];
                    if (body[8] != 8)
                        throw new InvalidDataException($"{body[8]}-bit PNGs are not supported; save as 8 bits per channel.");
                    if (body[12] != 0)
                        throw new InvalidDataException("Interlaced PNGs are not supported.");
                    break;
                case "PLTE": palette = body.ToArray(); break;
                case "tRNS": alpha = body.ToArray(); break;
                case "IDAT": idat.Write(body); break;
            }
            p += 12 + length;
        }

        var channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"PNG colour type {type} is not supported.") };
        var stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.ReadExactly(raw);

        Unfilter(raw, stride, height, channels);

        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var row = y * (stride + 1) + 1;
            for (var x = 0; x < width; x++)
            {
                var s = row + x * channels;
                var d = (y * width + x) * 4;
                switch (type)
                {
                    case 0: pixels[d] = pixels[d + 1] = pixels[d + 2] = raw[s]; pixels[d + 3] = 255; break;
                    case 2: pixels[d] = raw[s]; pixels[d + 1] = raw[s + 1]; pixels[d + 2] = raw[s + 2]; pixels[d + 3] = 255; break;
                    case 3:
                        var i = raw[s];
                        pixels[d] = palette![i * 3]; pixels[d + 1] = palette[i * 3 + 1]; pixels[d + 2] = palette[i * 3 + 2];
                        pixels[d + 3] = alpha != null && i < alpha.Length ? alpha[i] : (byte)255;
                        break;
                    case 4: pixels[d] = pixels[d + 1] = pixels[d + 2] = raw[s]; pixels[d + 3] = raw[s + 1]; break;
                    default: raw.AsSpan(s, 4).CopyTo(pixels.AsSpan(d)); break;
                }
            }
        }
        return new RgbaImage(width, height, pixels);
    }

    private static void Unfilter(byte[] raw, int stride, int height, int bpp)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * (stride + 1);
            var filter = raw[row];
            var cur = row + 1;
            var up = cur - (stride + 1);
            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? raw[cur + x - bpp] : 0;
                int b = y > 0 ? raw[up + x] : 0;
                int c = x >= bpp && y > 0 ? raw[up + x - bpp] : 0;
                raw[cur + x] += filter switch
                {
                    0 => (byte)0,
                    1 => (byte)a,
                    2 => (byte)b,
                    3 => (byte)((a + b) / 2),
                    4 => (byte)Paeth(a, b, c),
                    _ => throw new InvalidDataException($"Unknown PNG filter {filter}."),
                };
            }
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var (pa, pb, pc) = (Math.Abs(p - a), Math.Abs(p - b), Math.Abs(p - c));
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream output, string name, byte[] body)
    {
        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(field, body.Length);
        output.Write(field);
        var tag = System.Text.Encoding.ASCII.GetBytes(name);
        output.Write(tag);
        output.Write(body);
        var crc = PngCrc.Compute(tag, body);
        BinaryPrimitives.WriteUInt32BigEndian(field, crc);
        output.Write(field);
    }
}
