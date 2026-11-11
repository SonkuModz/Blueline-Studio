using System.Text.RegularExpressions;
using Blueline.Formats.Textures;

namespace Blueline.Core.Teams;

/// <summary>A jersey's three sheets: colour (`_cm`), normal (`_nm`) and coeff (`_coeff`, also called the sm map).</summary>
public sealed record JerseySheets(string Colour, string Normal, string Coeff, string Brand, string Kit)
{
    private static readonly Regex Name = new(@"/jersey_(?<brand>adidas|rbk|nineties)_(?<kit>[a-z0-9_]+)_cm\.gnf$", RegexOptions.Compiled);

    /// <summary>The sheets that go with a jersey colour sheet, or null if the path is not one.</summary>
    public static JerseySheets? FromColour(string path)
    {
        var match = Name.Match(path);
        if (!match.Success)
            return null;
        var stem = path[..^"_cm.gnf".Length];
        return new JerseySheets(path, stem + "_nm.gnf", stem + "_coeff.gnf", match.Groups["brand"].Value, match.Groups["kit"].Value);
    }

    public static bool IsColourSheet(string path) => Name.IsMatch(path);

    public string BrandLabel => Brand switch { "adidas" => "Adidas", "rbk" => "Reebok", "nineties" => "Nineties", _ => Brand };
}

/// <summary>What to raise, how high and how soft, and what the raised areas get in the coeff map.</summary>
/// <param name="Height">1 matches the depth of the game's own crests (measured on the template jersey).</param>
/// <param name="Softness">Edge blur in pixels at 2048 wide; scaled for other sizes.</param>
/// <param name="Tolerance">How far a colour may be from the main colour before it counts as raised, 0 to 255.</param>
/// <param name="CoeffValue">The flat grey the raised areas get in all three coeff channels. Stock logos use 46 to 79.</param>
/// <param name="InnerDetail">0 to 1: how much brightness changes inside a raised area show as steps, like the letters in a crest.</param>
/// <param name="ClearMargin">
/// Pixels (at 2048 wide) around the design where the base is smoothed first, so marks already pressed into it, such as
/// the adidas logo every adidas jersey shares, do not show behind a new logo. 0 leaves the base as it is.
/// </param>
/// <param name="Stitch">Dashed stitching just outside the design, like the game's crests have.</param>
/// <param name="Grain">0 to 1: fine cloth grain inside the design, so it does not look like smooth plastic.</param>
public sealed record JerseyMapOptions(double Height = 1.0, double Softness = 1.0, int Tolerance = 40, bool MarkCoeff = true, byte CoeffValue = 60,
    double InnerDetail = 0.4, double ClearMargin = 20, bool Stitch = true, double Grain = 0.25);

/// <summary>Where the design is raised (0 to 1), and the brightness inside it that gives inner steps.</summary>
public sealed record RaisedAreas(int Width, int Height, float[] Mask, float[] Brightness);

/// <summary>
/// Makes a jersey's normal and coeff maps from its colour sheet, the way the game's own are made: a fabric base with the
/// design pressed in, and the design flat grey in the coeff. Channel meaning and direction were measured on stock sheets
/// (docs/RESEARCH.md section 6.7); what the coeff channels do in the game's shader is not known.
/// </summary>
public static class JerseyMaps
{
    /// <summary>
    /// Normal offset, in stored units, per unit of height change per pixel. Fitted on `jersey_rbk_template_home_nm`
    /// against the crest mask in its coeff's blue channel: red falls as height rises to the right, green rises as height
    /// rises downward.
    /// </summary>
    public const double GameSlope = 1.7;

    /// <summary>
    /// How tall a raised area is at height 1, in the units of that slope. Fitted so that, at default options, the
    /// strongest edges of a crest made from the template's mask are as strong as the template's own (a test checks
    /// this). Matching the average lean instead left ours looking half as bold, because the game's edges are a few
    /// pixels of full slope rather than one.
    /// </summary>
    public const double CrestStep = 150;

    /// <summary>The jersey's main colour: the most common one, at 4 bits a channel, averaged over its pixels.</summary>
    public static (byte R, byte G, byte B) MainColour(RgbaImage colour)
    {
        var counts = new int[4096];
        var p = colour.Pixels;
        for (var i = 0; i < p.Length; i += 4)
            counts[(p[i] >> 4) << 8 | (p[i + 1] >> 4) << 4 | p[i + 2] >> 4]++;
        var best = Array.IndexOf(counts, counts.Max());
        long r = 0, g = 0, b = 0, n = 0;
        for (var i = 0; i < p.Length; i += 4)
            if (((p[i] >> 4) << 8 | (p[i + 1] >> 4) << 4 | p[i + 2] >> 4) == best)
            {
                r += p[i];
                g += p[i + 1];
                b += p[i + 2];
                n++;
            }
        return ((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    /// <summary>Raised where the colour is far from the main colour, flat where it is the main colour, with a short ramp between.</summary>
    public static RaisedAreas FromColour(RgbaImage colour, int tolerance, int width, int height)
    {
        var image = Fit(colour, width, height);
        var (mr, mg, mb) = MainColour(image);
        var p = image.Pixels;
        var mask = new float[width * height];
        const float Ramp = 4f; // Short, so anti-aliased edges in the sheet give a crisp step rather than a slope.
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var (dr, dg, db) = (p[i] - mr, p[i + 1] - mg, p[i + 2] - mb);
                var distance = MathF.Sqrt((dr * dr + dg * dg + db * db) / 3f);
                mask[y * width + x] = Math.Clamp((distance - tolerance) / Ramp + 0.5f, 0f, 1f);
            }
        });
        return new RaisedAreas(width, height, mask, Brightness(image));
    }

    /// <summary>From a picture: its transparency if it has any, otherwise its brightness. White or opaque is raised.</summary>
    public static RaisedAreas FromPicture(RgbaImage picture, int width, int height)
    {
        var image = Fit(picture, width, height);
        var useAlpha = TeamLogoImport.HasTransparency(image);
        var brightness = Brightness(image);
        var mask = useAlpha ? image.Pixels.Where((_, i) => i % 4 == 3).Select(a => a / 255f).ToArray() : brightness;
        return new RaisedAreas(width, height, mask, brightness);
    }

    private static float[] Brightness(RgbaImage image)
    {
        var p = image.Pixels;
        var output = new float[image.Width * image.Height];
        for (var i = 0; i < output.Length; i++)
            output[i] = (p[i * 4] * 0.299f + p[i * 4 + 1] * 0.587f + p[i * 4 + 2] * 0.114f) / 255f;
        return output;
    }

    /// <summary>The height pressed into the normal map: the mask, with some of the brightness inside it.</summary>
    public static float[] HeightOf(RaisedAreas areas, double innerDetail)
    {
        var d = (float)Math.Clamp(innerDetail, 0, 1);
        var height = new float[areas.Mask.Length];
        for (var i = 0; i < height.Length; i++)
            height[i] = areas.Mask[i] * (1 - d + d * areas.Brightness[i]);
        return height;
    }

    /// <summary>Box passes each way: three are close to a Gaussian of the given radius, one gives a straight ramp.</summary>
    public static float[] Blur(float[] mask, int width, int height, double radius, int passes = 3)
    {
        var r = (int)Math.Round(radius);
        if (r < 1)
            return mask;
        var a = (float[])mask.Clone();
        var b = new float[a.Length];
        for (var pass = 0; pass < passes; pass++)
        {
            BoxRows(a, b, width, height, r);
            BoxColumns(b, a, width, height, r);
        }
        return a;
    }

    private static void BoxRows(float[] source, float[] target, int width, int height, int r)
    {
        Parallel.For(0, height, y =>
        {
            var row = y * width;
            var sum = 0f;
            for (var x = -r; x <= r; x++)
                sum += source[row + Math.Clamp(x, 0, width - 1)];
            for (var x = 0; x < width; x++)
            {
                target[row + x] = sum / (2 * r + 1);
                sum += source[row + Math.Min(x + r + 1, width - 1)] - source[row + Math.Max(x - r, 0)];
            }
        });
    }

    private static void BoxColumns(float[] source, float[] target, int width, int height, int r)
    {
        Parallel.For(0, width, x =>
        {
            var sum = 0f;
            for (var y = -r; y <= r; y++)
                sum += source[Math.Clamp(y, 0, height - 1) * width + x];
            for (var y = 0; y < height; y++)
            {
                target[y * width + x] = sum / (2 * r + 1);
                sum += source[Math.Min(y + r + 1, height - 1) * width + x] - source[Math.Max(y - r, 0) * width + x];
            }
        });
    }

    /// <summary>Presses the raised areas into a base normal map. Output is in the game's stored layout: red and green, blue 0.</summary>
    public static RgbaImage Normal(RgbaImage baseNormal, RaisedAreas areas, JerseyMapOptions options)
    {
        var fitted = Fit(areas, baseNormal.Width, baseNormal.Height);
        return Normal(Cleared(baseNormal, fitted.Mask, options.ClearMargin), HeightOf(fitted, options.InnerDetail), options);
    }

    /// <summary>
    /// The base with sharp detail smoothed away under the design and a margin around it. Broad folds survive the smoothing;
    /// small pressed marks and weave do not.
    /// </summary>
    public static RgbaImage Cleared(RgbaImage baseImage, float[] mask, double margin)
    {
        var (w, h) = (baseImage.Width, baseImage.Height);
        var scale = w / 2048.0;
        if (margin <= 0)
            return baseImage;
        var spread = Blur(mask, w, h, margin * scale);
        var weight = new float[w * h];
        for (var i = 0; i < weight.Length; i++)
            weight[i] = Math.Clamp(spread[i] * 3f, 0f, 1f);

        // Weave for a cleared pixel is the average of the clean fabric at the same place in the weave's repeat, over a
        // window around it. Averaging keeps the weave's local alignment and washes out seams and marks among the samples.
        var period = Math.Max(2, (int)Math.Round(WeavePeriod * scale));
        const int Reach = 10;
        var output = (byte[])baseImage.Pixels.Clone();
        for (var c = 0; c < 3; c++)
        {
            var channel = new float[w * h];
            var flat = true;
            for (var i = 0; i < channel.Length; i++)
            {
                channel[i] = baseImage.Pixels[i * 4 + c];
                flat &= channel[i] == channel[0];
            }
            if (flat)
                continue; // A normal map's empty blue channel: nothing to clear.
            var smooth = Blur(channel, w, h, 10 * scale);
            var softened = Blur(channel, w, h, Math.Max(1, 2 * scale));

            // Sum of clean weave samples, and their count, at whole repeats across, then down: two short passes rather
            // than one wide window per pixel.
            var (rowSum, rowCount) = (new float[w * h], new float[w * h]);
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    var (sum, count) = (0f, 0f);
                    for (var k = -Reach; k <= Reach; k++)
                    {
                        var sx = x + k * period;
                        if (sx < 0 || sx >= w)
                            continue;
                        var s = y * w + sx;
                        if (weight[s] < 0.02f)
                        {
                            sum += channel[s] - softened[s];
                            count++;
                        }
                    }
                    (rowSum[y * w + x], rowCount[y * w + x]) = (sum, count);
                }
            });
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    var t = weight[i];
                    if (t <= 0)
                        continue;
                    // Broad folds from the smoothed base; outside the design, weave borrowed from clean fabric.
                    var keepWeave = 1 - Math.Clamp(mask[i] * 2, 0f, 1f);
                    var weave = 0f;
                    if (keepWeave > 0)
                    {
                        var (sum, count) = (0f, 0f);
                        for (var j = -Reach; j <= Reach; j++)
                        {
                            var sy = y + j * period;
                            if (sy < 0 || sy >= h)
                                continue;
                            sum += rowSum[sy * w + x];
                            count += rowCount[sy * w + x];
                        }
                        weave = count > 0 ? sum / count : 0f;
                    }
                    var value = smooth[i] + weave * keepWeave;
                    output[i * 4 + c] = (byte)Math.Clamp(Math.Round(channel[i] + (value - channel[i]) * t), 0, 255);
                }
            });
        }
        return baseImage with { Pixels = output };
    }

    /// <summary>The jersey fabric's weave repeats every 6 px at 2048 wide, across and down (measured on the adidas base).</summary>
    public const int WeavePeriod = 6;

    /// <summary>Presses a height field (0 to 1, one value a pixel at the base's size) into a base normal map.</summary>
    public static RgbaImage Normal(RgbaImage baseNormal, float[] mask, JerseyMapOptions options)
    {
        var (w, h) = (baseNormal.Width, baseNormal.Height);
        if (mask.Length != w * h)
            throw new ArgumentException("The height field is not the base's size.");
        var shaped = options.Stitch ? WithStitch(mask, w, h) : (float[])mask.Clone();
        AddGrain(shaped, mask, w, options.Grain);
        // One box pass, not a blur: it makes a straight bevel with hard corners, which reads as crisp like the game's.
        var height = Blur(shaped, w, h, options.Softness * w / 2048.0, passes: 1);
        var scale = GameSlope * options.Height * CrestStep / 127.0;
        var b = baseNormal.Pixels;
        var output = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            var (up, down) = (Math.Max(y - 1, 0), Math.Min(y + 1, h - 1));
            for (var x = 0; x < w; x++)
            {
                var (left, right) = (Math.Max(x - 1, 0), Math.Min(x + 1, w - 1));
                var gx = (height[y * w + right] - height[y * w + left]) / 2.0;
                var gy = (height[down * w + x] - height[up * w + x]) / 2.0;

                // Detail normal in stored axes, then combined with the base so the fabric keeps its folds.
                var (dx, dy) = (-scale * gx, scale * gy);
                var dLength = Math.Sqrt(dx * dx + dy * dy + 1);
                var o = (y * w + x) * 4;
                var bx = (b[o] - 128) / 127.0;
                var by = (b[o + 1] - 128) / 127.0;
                var bz = Math.Sqrt(Math.Max(0, 1 - bx * bx - by * by));
                var (nx, ny, nz) = (bx + dx / dLength, by + dy / dLength, bz / dLength);
                var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                output[o] = ToStored(nx / length);
                output[o + 1] = ToStored(ny / length);
                output[o + 3] = 255;
            }
        });
        return new RgbaImage(w, h, output);
    }

    /// <summary>
    /// Adds dashed stitching about 2 px outside every raised edge (at 2048 wide), the way the game's crests are sewn on.
    /// The line is where the design, spread out, falls to a fixed low level, so it follows the outline at a steady
    /// distance; diagonal bands cut it into stitches.
    /// </summary>
    private static float[] WithStitch(float[] height, int w, int h)
    {
        var scale = w / 2048.0;
        var inside = new float[height.Length];
        for (var i = 0; i < inside.Length; i++)
            inside[i] = height[i] > 0.25f ? 1f : 0f;
        var spread = Blur(inside, w, h, 2 * scale);
        var output = (float[])height.Clone();
        var period = Math.Max(2, (int)Math.Round(4 * scale));
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (inside[i] > 0)
                    continue;
                var line = Math.Clamp(1 - Math.Abs(spread[i] - 0.18f) / 0.12f, 0f, 1f);
                var stitch = (x + y) / period % 2 == 0 ? 1f : 0.2f;
                output[i] = Math.Max(output[i], line * stitch * 0.85f);
            }
        });
        return output;
    }

    /// <summary>
    /// Fine grain inside raised areas, so a crest reads as sewn-on cloth rather than smooth plastic. Fixed noise, so the
    /// same options always give the same map.
    /// </summary>
    private static void AddGrain(float[] height, float[] mask, int w, double amount)
    {
        if (amount <= 0)
            return;
        for (var i = 0; i < height.Length; i++)
        {
            if (mask[i] <= 0)
                continue;
            var n = (uint)i * 2654435761u;
            n ^= n >> 15;
            n *= 2246822519u;
            n ^= n >> 13;
            height[i] += (float)(((n & 0xFFFF) / 65535.0 - 0.5) * amount * mask[i]);
        }
    }

    private static byte ToStored(double v) => (byte)Math.Clamp(Math.Round(128 + v * 127), 1, 255);

    /// <summary>The base coeff map with the raised areas painted flat grey.</summary>
    public static RgbaImage Coeff(RgbaImage baseCoeff, RaisedAreas areas, JerseyMapOptions options)
    {
        var mask = Fit(areas, baseCoeff.Width, baseCoeff.Height).Mask;
        baseCoeff = Cleared(baseCoeff, mask, options.ClearMargin);
        var output = (byte[])baseCoeff.Pixels.Clone();
        if (!options.MarkCoeff)
            return baseCoeff with { Pixels = output };
        var soft = Blur(mask, baseCoeff.Width, baseCoeff.Height, 0.75 * baseCoeff.Width / 2048.0);
        for (var i = 0; i < soft.Length; i++)
        {
            var t = soft[i];
            if (t <= 0)
                continue;
            var o = i * 4;
            for (var c = 0; c < 3; c++)
                output[o + c] = (byte)Math.Round(output[o + c] + (options.CoeffValue - output[o + c]) * t);
        }
        return baseCoeff with { Pixels = output };
    }

    /// <summary>
    /// The per-pixel median of several sheets. Each team's crest sits on its own pixels, so the median keeps what the
    /// sheets share (folds, weave, seams) and drops the crests.
    /// </summary>
    public static RgbaImage Median(IReadOnlyList<RgbaImage> sheets)
    {
        if (sheets.Count == 0)
            throw new ArgumentException("No sheets to combine.");
        var (w, h) = (sheets[0].Width, sheets[0].Height);
        var fitted = sheets.Select(s => Fit(s, w, h).Pixels).ToArray();
        var output = new byte[w * h * 4];
        Parallel.For(0, h, () => new byte[fitted.Length], (y, _, values) =>
        {
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    for (var s = 0; s < fitted.Length; s++)
                        values[s] = fitted[s][o + c];
                    Array.Sort(values);
                    output[o + c] = values[values.Length / 2];
                }
            }
            return values;
        }, _ => { });
        return new RgbaImage(w, h, output);
    }

    /// <summary>
    /// Up to <paramref name="count"/> of a brand's sheets with one suffix, at most one per stored size, so near-identical
    /// copies do not outvote the rest. Spread evenly through the list.
    /// </summary>
    public static IReadOnlyList<GameFile> BaseSources(VirtualFileSystem vfs, string brand, string suffix, int count = 15)
    {
        var prefix = $"nhl/data/kettle/rendering/characters/player/jersey_{brand}_";
        var all = vfs.Files.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal) && f.Path.EndsWith($"_{suffix}.gnf", StringComparison.Ordinal)
                && !f.Path.Contains("_template_", StringComparison.Ordinal))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        var bySize = all.GroupBy(f => f.StoredSize).Select(g => g.First()).ToList();
        if (bySize.Count <= count)
            return bySize;
        return Enumerable.Range(0, count).Select(i => bySize[i * bySize.Count / count]).ToList();
    }

    /// <summary>A plain base for a brand: the median of its sheets with that suffix, decoded at full size.</summary>
    public static RgbaImage PlainBase(VirtualFileSystem vfs, string brand, string suffix, CancellationToken cancel = default)
    {
        var sources = BaseSources(vfs, brand, suffix);
        var sheets = new RgbaImage[sources.Count];
        Parallel.For(0, sources.Count, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, i =>
        {
            var texture = GameTexture.TryOpen(vfs.ReadContent(sources[i])) ?? throw new InvalidDataException($"{sources[i].Path} is not a texture.");
            sheets[i] = texture.DecodeMip(0);
        });
        var size = sheets.GroupBy(s => (s.Width, s.Height)).OrderByDescending(g => g.Count()).First().Key;
        return Median(sheets.Where(s => (s.Width, s.Height) == size).ToList());
    }

    /// <summary>A mask drawn as a picture, for preview.</summary>
    public static RgbaImage MaskImage(float[] mask, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < mask.Length; i++)
        {
            var v = (byte)Math.Round(Math.Clamp(mask[i], 0f, 1f) * 255);
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = v;
            pixels[i * 4 + 3] = 255;
        }
        return new RgbaImage(width, height, pixels);
    }

    private static RgbaImage Fit(RgbaImage image, int width, int height) =>
        image.Width == width && image.Height == height ? image : TexturePipeline.Resize(image, width, height);

    /// <summary>Resamples raised areas to another sheet size, so a 1024 coeff can share a 2048 normal map's areas.</summary>
    public static RaisedAreas Fit(RaisedAreas areas, int width, int height)
    {
        if (areas.Width == width && areas.Height == height)
            return areas;
        float[] Sample(float[] source)
        {
            var output = new float[width * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    output[y * width + x] = source[(y * areas.Height / height) * areas.Width + x * areas.Width / width];
            return output;
        }
        return new RaisedAreas(width, height, Sample(areas.Mask), Sample(areas.Brightness));
    }
}
