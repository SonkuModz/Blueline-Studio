using BCnEncoder.Encoder;

namespace Blueline.Formats.Textures;

public sealed record RgbaImage(int Width, int Height, byte[] Pixels);

public sealed record TextureImportResult(byte[] Gnf, bool Resized, TimeSpan Elapsed);

/// <summary>
/// One texture, from whichever container it was stored in. NHL 19 keeps DDS files under `.gnf` names,
/// so this is what callers use instead of parsing a GNF directly.
/// </summary>
public abstract class GameTexture
{
    public abstract string Container { get; }
    public abstract int Width { get; }
    public abstract int Height { get; }
    public abstract int MipCount { get; }
    public abstract int Faces { get; }
    public abstract string Summary { get; }

    /// <summary>Why it cannot be decoded, or null when it can.</summary>
    public abstract string? Blocker { get; }

    public abstract RgbaImage DecodeMip(int mip, int face = 0);

    public static GameTexture? TryOpen(byte[] data)
    {
        if (GnfTexture.IsGnf(data))
            return new GnfGameTexture(GnfTexture.Parse(data));
        return DdsFile.IsDds(data) ? new DdsGameTexture(DdsFile.Parse(data)) : null;
    }
}

internal sealed class GnfGameTexture(GnfTexture gnf) : GameTexture
{
    public GnfTexture Gnf { get; } = gnf;
    public override string Container => "GNF";
    public override int Width => Gnf.Info.Width;
    public override int Height => Gnf.Info.Height;
    public override int MipCount => Gnf.Info.MipCount;
    public override int Faces => Gnf.Info.Faces;
    public override string Summary => Gnf.Info.Summary + $", {Gnf.Info.TileModeName}";
    public override string? Blocker => Gnf.DecodeBlocker();

    public override RgbaImage DecodeMip(int mip, int face = 0)
    {
        var (w, h) = (Gnf.MipWidth(mip), Gnf.MipHeight(mip));
        var blocks = Gnf.DetiledSurface(mip, face);
        return new RgbaImage(w, h, BlockCodec.Decode(blocks, w, h, Gnf.Info.Codec, Gnf.Info.IsSigned));
    }
}

internal sealed class DdsGameTexture(DdsFile dds) : GameTexture
{
    public override string Container => "DDS";
    public override int Width => dds.Width;
    public override int Height => dds.Height;
    public override int MipCount => dds.MipCount;
    public override int Faces => dds.Layers;
    public override string Summary => $"{dds.Width}x{dds.Height}, {dds.Codec.ToString().ToUpperInvariant()}, {dds.MipCount} mips{(dds.Layers > 1 ? $", {dds.Layers} {(dds.IsVolume ? "slices" : "faces")}" : "")} (DDS)";
    public override string? Blocker => dds.Codec == TextureCodec.Unsupported ? "This DDS pixel format is not implemented." : null;

    public override RgbaImage DecodeMip(int mip, int face = 0)
    {
        var (w, h) = (Math.Max(1, dds.Width >> mip), Math.Max(1, dds.Height >> mip));
        return new RgbaImage(w, h, BlockCodec.Decode(dds.Mip(mip, face), w, h, dds.Codec, signed: false));
    }
}

/// <summary>Turns a source image into a replacement file with the original's exact shape.</summary>
public static class TexturePipeline
{
    /// <summary>Why this file cannot be a replacement target, or null if it can.</summary>
    public static string? ImportBlocker(byte[] original)
    {
        if (DdsFile.IsDds(original))
        {
            var dds = DdsFile.Parse(original);
            if (dds.Layers != 1)
                return "Cubemaps and volume textures can be exported but not replaced yet.";
            if (dds.Codec == TextureCodec.Rgba32F)
                return "This is a 32-bit float lookup table. An 8-bit picture cannot replace it without losing precision.";
            return BlockCodec.CanEncode(dds.Codec) ? null : $"{dds.Codec} cannot be encoded by this build.";
        }
        if (!GnfTexture.IsGnf(original))
            return "Not a texture.";

        var gnf = GnfTexture.Parse(original);
        if (gnf.DecodeBlocker() is { } blocker)
            return blocker;
        if (gnf.Info.Faces != 1)
            return "Cubemaps and texture arrays can be exported but not replaced yet.";
        if (!BlockCodec.CanEncode(gnf.Info.Codec))
            return $"{gnf.Info.Codec} cannot be encoded by this build. Lightmaps and cubemaps use it.";
        return null;
    }

    public static TextureImportResult Import(byte[] original, RgbaImage source, CompressionQuality quality = CompressionQuality.Balanced)
    {
        if (ImportBlocker(original) is { } blocker)
            throw new NotSupportedException(blocker);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (DdsFile.IsDds(original))
        {
            var dds = DdsFile.Parse(original);
            var (fitted, resized) = Fit(source, dds.Width, dds.Height);
            var mips = EncodeChain(fitted, dds.MipCount, dds.Codec, false, quality);
            return new TextureImportResult(DdsFile.Rebuild(dds, mips), resized, clock.Elapsed);
        }

        var gnf = GnfTexture.Parse(original);
        var (image, wasResized) = Fit(source, gnf.Info.Width, gnf.Info.Height);
        var blocks = EncodeChain(image, gnf.Info.MipCount, gnf.Info.Codec, gnf.Info.IsSigned, quality);
        return new TextureImportResult(GnfTexture.Rebuild(gnf, blocks), wasResized, clock.Elapsed);
    }

    private static (RgbaImage, bool) Fit(RgbaImage source, int width, int height) =>
        source.Width == width && source.Height == height ? (source, false) : (Resize(source, width, height), true);

    private static List<byte[]> EncodeChain(RgbaImage top, int mipCount, TextureCodec codec, bool signed, CompressionQuality quality)
    {
        var mips = new List<byte[]>(mipCount);
        var current = top;
        for (var mip = 0; mip < mipCount; mip++)
        {
            mips.Add(BlockCodec.Encode(current.Pixels, current.Width, current.Height, codec, signed, quality));
            if (mip + 1 < mipCount)
                current = Resize(current, Math.Max(1, current.Width / 2), Math.Max(1, current.Height / 2));
        }
        return mips;
    }

    /// <summary>Area-averaging resample. Averages in stored space, as the DCC tools commonly do.</summary>
    public static RgbaImage Resize(RgbaImage source, int width, int height)
    {
        var output = new byte[width * height * 4];
        Parallel.For(0, height, y =>
        {
            var y0 = y * source.Height / height;
            var y1 = Math.Max(y0 + 1, (y + 1) * source.Height / height);
            Span<int> sum = stackalloc int[4];
            for (var x = 0; x < width; x++)
            {
                var x0 = x * source.Width / width;
                var x1 = Math.Max(x0 + 1, (x + 1) * source.Width / width);
                sum.Clear();
                var count = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var p = (sy * source.Width + sx) * 4;
                        for (var c = 0; c < 4; c++)
                            sum[c] += source.Pixels[p + c];
                        count++;
                    }
                }
                var o = (y * width + x) * 4;
                for (var c = 0; c < 4; c++)
                    output[o + c] = (byte)((sum[c] + count / 2) / count);
            }
        });
        return new RgbaImage(width, height, output);
    }
}
