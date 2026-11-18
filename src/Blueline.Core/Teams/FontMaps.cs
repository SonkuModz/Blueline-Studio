using System.Text.RegularExpressions;
using BCnEncoder.Encoder;
using Blueline.Formats.Textures;

namespace Blueline.Core.Teams;

/// <summary>A number font's three sheets: colour (`_cm`), normal (`_nm`) and shine (`_sm`).</summary>
public sealed record FontSheets(string Colour, string Normal, string Sm, string Brand, string Kit)
{
    private static readonly Regex Name = new(@"/font_(?<brand>adidas|rbk|nineties)_(?<kit>[a-z0-9_]+)_cm\.gnf$", RegexOptions.Compiled);

    /// <summary>The sheets that go with a font colour sheet, or null if the path is not one.</summary>
    public static FontSheets? FromColour(string path)
    {
        var match = Name.Match(path);
        if (!match.Success)
            return null;
        var stem = path[..^"_cm.gnf".Length];
        return new FontSheets(path, stem + "_nm.gnf", stem + "_sm.gnf", match.Groups["brand"].Value, match.Groups["kit"].Value);
    }

    public static bool IsColourSheet(string path) => Name.IsMatch(path);

    public string BrandLabel => Brand switch { "adidas" => "Adidas", "rbk" => "Reebok", "nineties" => "Nineties", _ => Brand };
}

/// <summary>How the letters are raised and how they shine. Defaults are the game's most common values (docs/RESEARCH.md section 6.11).</summary>
/// <param name="Depth">1 matches the game's own fonts at the edge.</param>
/// <param name="Bevel">How far the rounding reaches into a letter, in pixels at 1024 wide. The game's fit 10, after a 1 px lip at the edge.</param>
/// <param name="LayerStep">0 to 1: how far the fill stands above the outline, as a share of the letter's own height. 0 is one flat layer.</param>
/// <param name="Tolerance">How far a colour may be from the fill colour and still count as fill, 0 to 255. The rest of a letter is outline.</param>
/// <param name="Grain">0 to 1: fine cloth grain on the letters, in both maps.</param>
/// <param name="FillValue">The `_sm` grey for the fill. The game's are 24 to 32, most often 30.</param>
/// <param name="OutlineValue">The `_sm` grey for the outline. The game's are 20 to 32, most often 24.</param>
/// <param name="BottomRowValue">The `_sm` grey for the bottom row of small numbers. Most of the game's use 50; the rest match the letters.</param>
/// <param name="BrighterBottomRow">Whether the bottom row gets its own grey, as most of the game's fonts do.</param>
/// <param name="Stitch">A beaded stitch just inside every layer's edge, the way the game's twill numbers are sewn on.</param>
public sealed record FontMapOptions(double Depth = 1.0, double Bevel = 10, double LayerStep = 0.5, int Tolerance = 30, double Grain = 0.25,
    byte FillValue = 30, byte OutlineValue = 24, byte BottomRowValue = 50, bool BrighterBottomRow = true, bool Stitch = true);

/// <summary>Where the letters are (0 to 1), which part is fill (0 to 1), and the first row of the bottom row of small numbers.</summary>
public sealed record FontLayers(int Width, int Height, float[] Letters, float[] Fill, int BottomRowStart, (byte R, byte G, byte B) FillColour);

/// <summary>
/// Makes a number font's normal and `_sm` maps from its colour sheet, the way the game's own are made: letters raised with a
/// rounded edge, the fill a step above the outline, and the `_sm` a flat grey per layer with light grain. Measured on 120
/// of the game's fonts (docs/RESEARCH.md section 6.11); what `_sm` does in the game's shader is not known.
/// </summary>
public static class FontMaps
{
    /// <summary>Letters and their fill. The colour sheet's transparency marks the letters, as in all of the game's fonts.</summary>
    public static FontLayers Layers(RgbaImage colour, int tolerance, int width, int height)
    {
        var image = colour.Width == width && colour.Height == height ? colour : TexturePipeline.Resize(colour, width, height);
        var p = image.Pixels;
        var n = width * height;
        float[] letters;
        if (TeamLogoImport.HasTransparency(image))
        {
            letters = new float[n];
            for (var i = 0; i < n; i++)
                letters[i] = p[i * 4 + 3] / 255f;
        }
        else
        {
            // No transparency: whatever is not the background colour is a letter.
            letters = JerseyMaps.FromColour(image, 40, width, height).Mask;
        }

        var fillColour = MainColour(p, letters);
        var fill = new float[n];
        const float Ramp = 4f;
        for (var i = 0; i < n; i++)
        {
            if (letters[i] <= 0)
                continue;
            var (dr, dg, db) = (p[i * 4] - fillColour.R, p[i * 4 + 1] - fillColour.G, p[i * 4 + 2] - fillColour.B);
            var distance = MathF.Sqrt((dr * dr + dg * dg + db * db) / 3f);
            fill[i] = Math.Clamp(0.5f - (distance - tolerance) / Ramp, 0f, 1f) * letters[i];
        }
        return new FontLayers(width, height, letters, fill, BottomRow(letters, width, height), fillColour);
    }

    /// <summary>The most common colour among solid letter pixels, at 4 bits a channel, averaged over its pixels.</summary>
    private static (byte R, byte G, byte B) MainColour(byte[] p, float[] letters)
    {
        var counts = new int[4096];
        for (var i = 0; i < letters.Length; i++)
            if (letters[i] > 0.8f)
                counts[Key(p, i)]++;
        var best = Array.IndexOf(counts, counts.Max());
        long r = 0, g = 0, b = 0, count = 0;
        for (var i = 0; i < letters.Length; i++)
            if (letters[i] > 0.8f && Key(p, i) == best)
            {
                (r, g, b) = (r + p[i * 4], g + p[i * 4 + 1], b + p[i * 4 + 2]);
                count++;
            }
        return count == 0 ? ((byte)255, (byte)255, (byte)255) : ((byte)(r / count), (byte)(g / count), (byte)(b / count));

        static int Key(byte[] p, int i) => (p[i * 4] >> 4) << 8 | (p[i * 4 + 1] >> 4) << 4 | p[i * 4 + 2] >> 4;
    }

    /// <summary>
    /// The first row of the last band of letters: the bottom row of small numbers in the game's layout. A band is rows with
    /// letters in them, ended by a gap of empty rows. Returns the height when there are fewer than two bands.
    /// </summary>
    public static int BottomRowStart(FontLayers layers) => layers.BottomRowStart;

    private static int BottomRow(float[] letters, int width, int height)
    {
        var used = new bool[height];
        for (var y = 0; y < height; y++)
        {
            var count = 0;
            for (var x = 0; x < width; x++)
                if (letters[y * width + x] > 0.5f)
                    count++;
            used[y] = count > width / 200;
        }
        var bands = new List<int>();
        for (var y = 0; y < height; y++)
            if (used[y] && (y == 0 || !used[y - 1]))
                bands.Add(y);
        return bands.Count >= 2 ? bands[^1] : height;
    }

    /// <summary>Distance in pixels from each pixel inside the mask to the nearest pixel outside it (two-pass chamfer, 3-4).</summary>
    public static float[] InsideDistance(float[] mask, int w, int h)
    {
        const float Far = 1e6f;
        var d = new float[w * h];
        for (var i = 0; i < d.Length; i++)
            d[i] = mask[i] > 0.5f ? Far : 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (d[i] == 0)
                    continue;
                var v = d[i];
                if (x > 0) v = Math.Min(v, d[i - 1] + 3);
                if (y > 0)
                {
                    v = Math.Min(v, d[i - w] + 3);
                    if (x > 0) v = Math.Min(v, d[i - w - 1] + 4);
                    if (x < w - 1) v = Math.Min(v, d[i - w + 1] + 4);
                }
                d[i] = v;
            }
        for (var y = h - 1; y >= 0; y--)
            for (var x = w - 1; x >= 0; x--)
            {
                var i = y * w + x;
                if (d[i] == 0)
                    continue;
                var v = d[i];
                if (x < w - 1) v = Math.Min(v, d[i + 1] + 3);
                if (y < h - 1)
                {
                    v = Math.Min(v, d[i + w] + 3);
                    if (x < w - 1) v = Math.Min(v, d[i + w + 1] + 4);
                    if (x > 0) v = Math.Min(v, d[i + w - 1] + 4);
                }
                d[i] = v;
            }
        for (var i = 0; i < d.Length; i++)
            d[i] = d[i] >= Far ? w + h : d[i] / 3f;
        return d;
    }

    /// <summary>How much of a letter's height is the sharp lip at its edge; the rest is the rounding. Fitted on the game's fonts.</summary>
    public const float LipShare = 0.4f;

    /// <summary>
    /// The edge shape measured on the game's fonts: a 1 px lip at the edge, then a rounding that eases to flat
    /// <paramref name="bevel"/> pixels in. Their slope one to five pixels in is 86, 47, 38, 28 and 21 stored; this gives
    /// 82, 53, 33, 29 and 25. The mask's own soft edge places the step between pixels.
    /// </summary>
    public static float[] Pillow(float[] mask, int w, int h, double bevel) => Pillow(mask, InsideDistance(mask, w, h), w, bevel);

    private static float[] Pillow(float[] mask, float[] distance, int w, double bevel)
    {
        var lip = (float)(w / 1024.0);
        var reach = (float)Math.Max(bevel, 0.5);
        var output = new float[mask.Length];
        for (var i = 0; i < output.Length; i++)
        {
            // The distance is to the first pixel outside; the mask's coverage on the edge pixel says how much of it is in.
            var d = distance[i] > 0 ? distance[i] - 0.5f : mask[i] - 0.5f;
            if (d <= -0.5f)
                continue;
            output[i] = LipShare * Ease((d + 0.5f) / lip) + (1 - LipShare) * Ease((d + 0.5f) / reach);
        }
        return output;
    }

    private static float Ease(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return 1 - (1 - t) * (1 - t);
    }

    /// <summary>
    /// Normal offset per unit of height change per pixel, in the units JerseyMaps uses. Fitted so that, at default options,
    /// the slopes just inside an edge match the game's median (a test checks this on stock fonts).
    /// </summary>
    public const double EdgeSlope = 3.0;

    /// <summary>The height pressed into the normal map: the letters, the fill a step above them, and grain on top.</summary>
    public static float[] HeightOf(FontLayers layers, FontMapOptions options)
    {
        var (w, h) = (layers.Width, layers.Height);
        var bevel = options.Bevel * w / 1024.0;
        var lettersDistance = InsideDistance(layers.Letters, w, h);
        var fillDistance = InsideDistance(layers.Fill, w, h);
        var letters = Pillow(layers.Letters, lettersDistance, w, bevel);
        var fill = options.LayerStep > 0 ? Pillow(layers.Fill, fillDistance, w, bevel) : new float[letters.Length];
        var height = new float[letters.Length];
        var step = (float)options.LayerStep;
        var grain = (float)(options.Grain * 0.12);
        var scale = (float)(w / 1024.0);
        var period = StitchPeriod * scale;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                height[i] = letters[i] + step * fill[i];
                if (layers.Letters[i] <= 0)
                    continue;
                if (grain > 0)
                    height[i] += (Noise(i) - 0.5f) * grain * layers.Letters[i];
                if (options.Stitch)
                {
                    // The stitch runs about 2 px inside each layer's edge: beads across the line, on a diagonal so they
                    // read as a zig-zag along any edge.
                    var line = MathF.Max(Band(lettersDistance[i] / scale), layers.Fill[i] > 0.5f ? Band(fillDistance[i] / scale) : 0);
                    if (line > 0)
                        height[i] += StitchHeight * line * (0.5f + 0.5f * MathF.Cos(2 * MathF.PI * (x + y) / period));
                }
            }
        return height;
    }

    /// <summary>The stitch's bead spacing in pixels at 1024 wide, and its height as a share of a letter's. Matched by eye to the game's fonts.</summary>
    public const float StitchPeriod = 4.5f, StitchHeight = 0.6f;

    /// <summary>1 on the stitch line, just under 2 px in from an edge, falling to 0 about a pixel and a half either side.</summary>
    private static float Band(float distance) => Math.Clamp(1 - MathF.Abs(distance - 1.8f) / 1.5f, 0f, 1f);

    /// <summary>The normal map, in the game's stored layout: red and green, blue 0. Flat where there are no letters.</summary>
    public static RgbaImage Normal(FontLayers layers, FontMapOptions options)
    {
        var (w, h) = (layers.Width, layers.Height);
        var height = HeightOf(layers, options);
        // Slopes are per pixel at 1024 wide, so a 2048 sheet with the same letters gets the same look.
        var scale = EdgeSlope * options.Depth * w / 1024.0;
        var output = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            var (up, down) = (Math.Max(y - 1, 0), Math.Min(y + 1, h - 1));
            for (var x = 0; x < w; x++)
            {
                var (left, right) = (Math.Max(x - 1, 0), Math.Min(x + 1, w - 1));
                var gx = (height[y * w + right] - height[y * w + left]) / 2.0;
                var gy = (height[down * w + x] - height[up * w + x]) / 2.0;
                // Same direction as the game: red falls as height rises to the right, green rises as height rises downward.
                var (nx, ny) = (-scale * gx, scale * gy);
                var length = Math.Sqrt(nx * nx + ny * ny + 1);
                var o = (y * w + x) * 4;
                output[o] = ToStored(nx / length);
                output[o + 1] = ToStored(ny / length);
                output[o + 3] = 255;
            }
        });
        return new RgbaImage(w, h, output);
    }

    /// <summary>The `_sm` map: black off the letters, a flat grey per layer with light grain on them.</summary>
    public static RgbaImage Sm(FontLayers layers, FontMapOptions options)
    {
        var (w, h) = (layers.Width, layers.Height);
        var grain = (float)(options.Grain * 36);
        var noise = new float[w * h];
        for (var i = 0; i < noise.Length; i++)
            noise[i] = Noise(i * 7 + 3) - 0.5f;
        // Per-pixel speckle, like the game's felt; the outline gets less of it than the fill.
        var output = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var o = i * 4;
                output[o + 3] = 255;
                var coverage = layers.Letters[i];
                if (coverage <= 0)
                    continue;
                double value;
                if (options.BrighterBottomRow && y >= layers.BottomRowStart)
                    value = options.BottomRowValue;
                else
                {
                    var fill = coverage > 0 ? Math.Clamp(layers.Fill[i] / coverage, 0f, 1f) : 0f;
                    value = options.OutlineValue + (options.FillValue - options.OutlineValue) * fill;
                    value += noise[i] * grain * (0.4 + 0.6 * fill);
                }
                if (options.BrighterBottomRow && y >= layers.BottomRowStart)
                    value += noise[i] * grain * 0.4;
                value *= coverage;
                output[o] = output[o + 1] = output[o + 2] = (byte)Math.Clamp(Math.Round(value), 0, 255);
            }
        return new RgbaImage(w, h, output);
    }

    /// <summary>
    /// Makes and encodes a font's normal and `_sm` maps from its colour sheet, each in its game file's size and format. A map
    /// the game does not have for this font (a few have no `_nm`) is skipped.
    /// </summary>
    /// <param name="readOriginal">Reads a game file as the game has it, for its size and format.</param>
    public static IReadOnlyList<(string Path, byte[] Content, string Description)> Encode(FontSheets sheets, RgbaImage colour,
        Func<string, byte[]?> readOriginal, FontMapOptions options, CompressionQuality quality)
    {
        var output = new List<(string, byte[], string)>();
        var made = new Dictionary<(int, int), FontLayers>();
        foreach (var (path, isNormal) in new[] { (sheets.Normal, true), (sheets.Sm, false) })
        {
            if (readOriginal(path) is not { } original)
                continue;
            if (TexturePipeline.ImportBlocker(original) is { } blocker)
                throw new NotSupportedException($"{Path.GetFileName(path)}: {blocker}");
            var texture = GameTexture.TryOpen(original) ?? throw new InvalidDataException($"{path} is not a texture.");
            var size = (texture.Width, texture.Height);
            if (!made.TryGetValue(size, out var layers))
                made[size] = layers = Layers(colour, options.Tolerance, texture.Width, texture.Height);
            var image = isNormal ? Normal(layers, options) : Sm(layers, options);
            output.Add((path, TexturePipeline.Import(original, image, quality).Gnf, isNormal ? "Font normal map" : "Font sm map"));
        }
        return output;
    }

    /// <summary>The fill drawn as a picture for preview: outline grey, fill white, background black.</summary>
    public static RgbaImage LayersImage(FontLayers layers)
    {
        var pixels = new byte[layers.Width * layers.Height * 4];
        for (var i = 0; i < layers.Letters.Length; i++)
        {
            var v = (byte)Math.Round(Math.Clamp(layers.Letters[i] * 110 + layers.Fill[i] * 145, 0, 255));
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = v;
            pixels[i * 4 + 3] = 255;
        }
        return new RgbaImage(layers.Width, layers.Height, pixels);
    }

    /// <summary>Fixed noise in 0 to 1, so the same options always give the same maps.</summary>
    private static float Noise(int i)
    {
        var n = (uint)i * 2654435761u;
        n ^= n >> 15;
        n *= 2246822519u;
        n ^= n >> 13;
        return (n & 0xFFFF) / 65535f;
    }

    private static byte ToStored(double v) => (byte)Math.Clamp(Math.Round(128 + v * 127), 1, 255);
}
