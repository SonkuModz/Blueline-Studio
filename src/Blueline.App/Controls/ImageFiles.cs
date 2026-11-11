using System.Windows.Media;
using System.Windows.Media.Imaging;
using Blueline.Core;
using Blueline.Formats.Detect;
using Blueline.Formats.Textures;

namespace Blueline.App.Controls;

/// <summary>Reads source images with Windows' codecs, so PNG, JPEG, BMP and TIFF all work.</summary>
public static class ImageFiles
{
    private const int JpegQuality = 92;

    public const string OpenFilter = "Images (*.png;*.dds;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff)|*.png;*.dds;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff";

    public static bool IsSupported(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".dds" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff";

    public static RgbaImage Load(string path)
    {
        if (Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase))
            return ImageLoader.Load(path);

        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>True for a plain PNG or JPEG, as opposed to a GNF or DDS texture.</summary>
    public static bool IsPlain(ReadOnlySpan<byte> content) => PngFile.IsPng(content) || FormatDetector.IsJpeg(content);

    public static RgbaImage Decode(byte[] content)
    {
        using var stream = new MemoryStream(content);
        return Read(stream);
    }

    /// <summary>The top level of a GNF or DDS texture, or a plain PNG or JPEG. Null for anything else.</summary>
    public static RgbaImage? DecodeAny(byte[] content) =>
        GameTexture.TryOpen(content) is { } texture ? texture.DecodeMip(0) : IsPlain(content) ? Decode(content) : null;

    /// <summary>A new PNG or JPEG in the same format and size as <paramref name="original"/>.</summary>
    public static byte[] EncodeLike(byte[] original, RgbaImage image, out bool resized)
    {
        var stock = Decode(original);
        resized = image.Width != stock.Width || image.Height != stock.Height;
        var fitted = resized ? TexturePipeline.Resize(image, stock.Width, stock.Height) : image;
        return PngFile.IsPng(original) ? PngFile.Write(fitted) : WriteJpeg(fitted);
    }

    private static byte[] WriteJpeg(RgbaImage image)
    {
        var pixels = (byte[])image.Pixels.Clone();
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        var source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, pixels, image.Width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0)));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static RgbaImage Read(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var (width, height) = (converted.PixelWidth, converted.PixelHeight);
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        return new RgbaImage(width, height, pixels);
    }
}
