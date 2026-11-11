using System.Collections.Concurrent;
using BCnEncoder.Encoder;
using Blueline.Formats.Textures;

namespace Blueline.Core.Teams;

public enum LogoOutcome { Imported, Skipped, Failed }

public sealed record LogoSetItem(string Path, LogoOutcome Outcome, string Detail);

public sealed record LogoSetResult(IReadOnlyList<LogoSetItem> Items)
{
    public int Imported => Items.Count(i => i.Outcome == LogoOutcome.Imported);
    public int Skipped => Items.Count(i => i.Outcome == LogoOutcome.Skipped);
    public int Failed => Items.Count(i => i.Outcome == LogoOutcome.Failed);
}

/// <summary>
/// Puts one picture into all of a team's square logos as one undo step. Each target keeps its own size and format.
/// Mono logos are a flat silhouette of the picture in the colour the original used, because that is how the game
/// draws them, and creation zone logos are grey. Non-square files and special HUT cards have a design around the
/// logo, so they are left alone and reported.
/// </summary>
public static class TeamLogoImport
{
    private static readonly string[] SpecialCardFolders = ["/hutcardrare/", "/hutcardplayoffs/", "/hutcardfreeagency/", "/hutcardheritage/", "/hutcardstadium/"];

    public static LogoSetResult Run(IReadOnlyList<TeamArt> art, RgbaImage source, Func<string, byte[]?> readStock, ModProject project, string teamName,
        CompressionQuality quality, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        var targets = art.Where(a => a.Category == TeamArtCategory.Logos).ToList();
        var results = new LogoSetItem[targets.Count];
        var staged = new ConcurrentBag<(string, byte[], string)>();
        var hasTransparency = HasTransparency(source);
        var done = 0;

        Parallel.For(0, targets.Count, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, i =>
        {
            results[i] = ImportOne(targets[i], source, hasTransparency, readStock, staged, quality);
            progress?.Report((Interlocked.Increment(ref done), targets.Count));
        });

        if (!staged.IsEmpty)
            project.SetMany(staged.ToList(), $"Logo set for {teamName}");
        return new LogoSetResult(results);
    }

    private static LogoSetItem ImportOne(TeamArt target, RgbaImage source, bool hasTransparency, Func<string, byte[]?> readStock,
        ConcurrentBag<(string, byte[], string)> staged, CompressionQuality quality)
    {
        try
        {
            var original = readStock(target.Path);
            if (original == null)
                return new LogoSetItem(target.Path, LogoOutcome.Failed, "The original could not be read");
            if (TexturePipeline.ImportBlocker(original) is { } blocker)
                return new LogoSetItem(target.Path, LogoOutcome.Skipped, blocker);
            var texture = GameTexture.TryOpen(original);
            if (texture == null)
                return new LogoSetItem(target.Path, LogoOutcome.Failed, "Not a texture");
            if (texture.Width != texture.Height)
                return new LogoSetItem(target.Path, LogoOutcome.Skipped, "Not square: this is card art with a design around the logo");
            if (SpecialCardFolders.Any(f => target.Path.Contains(f, StringComparison.Ordinal)))
                return new LogoSetItem(target.Path, LogoOutcome.Skipped, "Special card art, which may be styled for its tier or event");
            if (target.Path.Contains("/customlogos/", StringComparison.Ordinal))
                return new LogoSetItem(target.Path, LogoOutcome.Skipped, "A layer of the custom logo system, not a picture of the logo");

            var isMono = IsMono(target.Path);
            if (isMono && !hasTransparency)
                return new LogoSetItem(target.Path, LogoOutcome.Skipped, "Mono logos come from the picture's transparency, and this picture has none");

            var fitted = Contain(source, texture.Width, texture.Height);
            var picture = isMono ? Silhouette(fitted, AverageColor(texture.DecodeMip(0)))
                : target.Path.Contains("/creationzone", StringComparison.Ordinal) ? Grey(fitted) : fitted;
            var imported = TexturePipeline.Import(original, picture, quality);
            staged.Add((target.Path, imported.Gnf, "Team logo set"));
            return new LogoSetItem(target.Path, LogoOutcome.Imported, isMono ? "Silhouette" : "Imported");
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or InvalidOperationException)
        {
            return new LogoSetItem(target.Path, LogoOutcome.Failed, e.Message);
        }
    }

    public static bool HasTransparency(RgbaImage image)
    {
        var clear = 0L;
        for (var i = 3; i < image.Pixels.Length; i += 4)
            if (image.Pixels[i] < 128)
                clear++;
        return clear * 4 * 100 >= image.Pixels.Length; // At least 1% of the picture.
    }

    private static bool IsMono(string path) => path.Contains("mono/", StringComparison.Ordinal);

    /// <summary>Scales to fit without changing the picture's shape, leaving the rest transparent.</summary>
    public static RgbaImage Contain(RgbaImage source, int width, int height)
    {
        var scale = Math.Min(width / (double)source.Width, height / (double)source.Height);
        var (w, h) = (Math.Clamp((int)Math.Round(source.Width * scale), 1, width), Math.Clamp((int)Math.Round(source.Height * scale), 1, height));
        var scaled = w == source.Width && h == source.Height ? source : TexturePipeline.Resize(source, w, h);
        if (w == width && h == height)
            return scaled;

        var pixels = new byte[width * height * 4];
        var (left, top) = ((width - w) / 2, (height - h) / 2);
        for (var y = 0; y < h; y++)
            Array.Copy(scaled.Pixels, y * w * 4, pixels, ((top + y) * width + left) * 4, w * 4);
        return new RgbaImage(width, height, pixels);
    }

    private static (byte R, byte G, byte B) AverageColor(RgbaImage image)
    {
        var (r, g, b, weight) = (0L, 0L, 0L, 0L);
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            var a = image.Pixels[i + 3];
            r += image.Pixels[i] * a;
            g += image.Pixels[i + 1] * a;
            b += image.Pixels[i + 2] * a;
            weight += a;
        }
        return weight == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)(r / weight), (byte)(g / weight), (byte)(b / weight));
    }

    private static RgbaImage Silhouette(RgbaImage image, (byte R, byte G, byte B) color)
    {
        var pixels = new byte[image.Pixels.Length];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.R;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.B;
            pixels[i + 3] = image.Pixels[i + 3];
        }
        return image with { Pixels = pixels };
    }

    private static RgbaImage Grey(RgbaImage image)
    {
        var pixels = new byte[image.Pixels.Length];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var v = (byte)((image.Pixels[i] * 77 + image.Pixels[i + 1] * 150 + image.Pixels[i + 2] * 29) >> 8);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
            pixels[i + 3] = image.Pixels[i + 3];
        }
        return image with { Pixels = pixels };
    }
}
