using Blueline.Formats.Models;
using Blueline.Formats.Textures;
using BCnEncoder.Encoder;

namespace Blueline.Core.Rinks;

/// <summary>How a picture is placed in the full-ice frame.</summary>
public enum FullIceFit
{
    /// <summary>A rink layout (TheFaceoff.net style): its end boards are found and put on the game's end boards.</summary>
    Layout,
    /// <summary>A picture already drawn to the full-ice template (2:1, the whole frame): stretched as it is.</summary>
    Template,
}

/// <summary>Where a layout's rink was found: its centre and scale, in source pixels.</summary>
public sealed record RinkMeasure(double CentreX, double CentreY, double PixelsPerMetre, string How, bool Found = true);

/// <summary>
/// Full-ice textures for the full-ice rink model (a modded <c>stadiums/common/rink_ice_nhl.mcd</c>).
///
/// Measured from that model (2026-11-16): it keeps the stock texture coordinates and only moves vertices. The centre
/// logo patch, <c>iceSurface12Shape</c>, goes from a 10.42 m square at centre ice to a 62.56 x 31.28 m rectangle over
/// the whole rink, and every other ice shape (main ice, red line, centre and corner ads) is collapsed to nothing. The
/// patch samples the team's centre logo slot, <c>stadiums/common/ice_centerlogo_&lt;artid&gt;_cm.gnf</c> (1024 square,
/// BC3 sRGB, 11 mips), through its third UV set: u = 0.5 + z / 62.56, v = 0.5 - x / 31.28. So the texture holds a
/// 2:1 frame squeezed into a square, right = +z (along the rink), down = -x, the same way up as TheFaceoff.net's
/// layouts. The game's ice is z +-30.48 m (200 ft) by x +-12.99 m (85 ft), centred in that frame. The patch replaces
/// the ice under it, so the texture is fully opaque.
/// </summary>
public static class FullIce
{
    public const string ModelPath = "nhl/data/kettle/rendering/stadiums/common/rink_ice_nhl.mcd";
    public const double FrameLength = 62.56;   // metres along z covered by u 0..1
    public const double FrameWidth = 31.28;    // metres along x covered by v 0..1
    public const double IceLength = 60.96;     // 200 ft
    public const double IceWidth = 25.98;      // 85 ft
    public const double CornerRadius = 8.53;   // 28 ft
    // Where the stock game draws its lines, measured by painting the stock ice_lines_0_cm texture through the stock
    // main ice mesh into metres (2026-11-16): goal line centres at z +-27.165 (89.1 ft, so the nets sit on them), blue
    // lines z +-7.64..7.91, end faceoff dots z +-21.03.
    public const double GoalLine = 27.165;
    // The game's net mouth is 1.9 m post to post (net_posts_high.mcd); a rule-book crease (8 ft) reaches past both
    // posts and looked too big in game to the user, so creases are shrunk round the goal line's middle to meet the posts.
    public const double CreaseScale = 0.8;
    public const int TextureSize = 1024;

    public static string SlotPath(int artId) => $"nhl/data/kettle/rendering/stadiums/common/ice_centerlogo_{artId}_cm.gnf";
    public static string CentreLinePath(int centreLine) => $"nhl/data/kettle/rendering/stadiums/common/ice_cline_{centreLine}_cm.gnf";

    /// <summary>The frame a full-ice model gives the centre logo, from its <c>iceSurface12Shape</c> bounds.</summary>
    public static (double Length, double Width)? FrameOf(McdFile model)
    {
        var shape = model.Shapes.FirstOrDefault(s => s.Name.StartsWith("iceSurface12", StringComparison.Ordinal));
        if (shape == null)
            return null;
        var p = model.ReadPositions(model.Streams[shape.Stream]);
        return (p.Max(v => v.Z) - p.Min(v => v.Z), p.Max(v => v.X) - p.Min(v => v.X));
    }

    /// <summary>True when the model stretches the centre logo over the rink (the full-ice model), not the stock one.</summary>
    public static bool IsFullIceModel(McdFile model) => FrameOf(model) is { } f && f.Length > IceLength;

    /// <summary>
    /// Finds the rink in a layout picture: the inner edge of the board outline (TheFaceoff.net draws a dark line
    /// round white ice on a white page) at the two ends, along rows where the end boards are straight. The rink is
    /// centred between them and 60.96 m long. Then the goal lines are found near where that puts them and the scale is
    /// set so they land on the game's (where its nets stand); the side boards are measured
    /// too, as a check. Falls back to the picture's own width when no outline is found.
    /// </summary>
    public static RinkMeasure Measure(RgbaImage image)
    {
        var w = image.Width;
        var h = image.Height;
        var px = image.Pixels;
        int Sum(int x, int y)
        {
            var o = (y * w + x) * 4;
            return px[o + 3] < 128 ? 765 : px[o] + px[o + 1] + px[o + 2];
        }

        // From outside in: the first dark run (the outline), then the first light pixel after it (the ice).
        int? Inner(int count, int start, int step, Func<int, int> at)
        {
            var seenDark = false;
            for (int i = start, n = 0; n < count / 3; i += step, n++)
            {
                var s = at(i);
                if (s < 3 * 110)
                    seenDark = true;
                else if (seenDark && s > 3 * 170)
                    return i;
            }
            return null;
        }

        static double? Median(IEnumerable<int?> values)
        {
            var list = values.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
            return list.Count >= 3 ? list[list.Count / 2] : null;
        }

        // The straight part of the end boards is within +-4.5 m of the middle; skip the very middle (nets, creases).
        var rows = new[] { 0.40, 0.43, 0.45, 0.55, 0.57, 0.60 }.Select(f => (int)(f * h)).ToArray();
        var left = Median(rows.Select(y => Inner(w, 0, 1, x => Sum(x, y))));
        var right = Median(rows.Select(y => Inner(w, w - 1, -1, x => Sum(x, y))));
        if (left is { } l && right is { } r && r - l > 0.85 * w)
        {
            var length = r - l + 1;
            var scale = length / IceLength;
            // Check: the side boards, measured along columns in the neutral zone.
            var columns = new[] { 0.42, 0.45, 0.55, 0.58 }.Select(f => (int)(l + f * length)).ToArray();
            var top = Median(columns.Select(x => Inner(h, 0, 1, y => Sum(x, y))));
            var bottom = Median(columns.Select(x => Inner(h, h - 1, -1, y => Sum(x, y))));
            var centreY = top is { } t && bottom is { } b ? (t + b + 1) / 2.0 : h / 2.0;
            var widthOk = top is { } t1 && bottom is { } b1 && Math.Abs((b1 - t1 + 1) / scale - IceWidth) < 2.5;
            var check = top is { } t2 && bottom is { } b2 ? $", side boards {(b2 - t2 + 1) / scale:F2} m apart (game 25.98)" : "";
            var centreX = (l + r + 1) / 2.0;
            var how = $"end boards at x {l} and {r}{check}";

            // The goal lines decide: the game's nets stand on its goal lines, and drawings differ by a few centimetres
            // in how far those are from the boards. Look for the red line near where the boards put it, on rows clear
            // of the creases, trapezoid and faceoff circles, and scale so the two land on the game's.
            bool Red(int x, int y)
            {
                var o = (y * w + x) * 4;
                return px[o + 3] >= 128 && px[o] - px[o + 1] > 60 && px[o] - px[o + 2] > 40;
            }
            double? GoalLineAt(int side, int y)
            {
                var expected = centreX + side * GoalLine * scale;
                var reach = (int)(0.8 * scale);
                double? best = null;
                for (var x = (int)(expected - reach); x <= expected + reach; x++)
                {
                    if (x < 0 || x >= w || !Red(x, y) || (x > 0 && Red(x - 1, y)))
                        continue;
                    var end = x;
                    while (end + 1 < w && Red(end + 1, y))
                        end++;
                    var mid = (x + end) / 2.0;
                    if (end - x < 0.25 * scale && (best == null || Math.Abs(mid - expected) < Math.Abs(best.Value - expected)))
                        best = mid;
                }
                return best;
            }
            static double? MedianOf(List<double> values)
            {
                values.Sort();
                return values.Count >= 3 ? values[values.Count / 2] : null;
            }
            var goalRows = new[] { -10.0, -9.0, -2.1, 2.1, 9.0, 10.0 }.Select(m => (int)Math.Round(centreY + m * scale)).Where(y => y >= 0 && y < h).ToList();
            var leftGoal = MedianOf(goalRows.Select(y => GoalLineAt(-1, y)).Where(v => v.HasValue).Select(v => v!.Value).ToList());
            var rightGoal = MedianOf(goalRows.Select(y => GoalLineAt(1, y)).Where(v => v.HasValue).Select(v => v!.Value).ToList());
            if (leftGoal is { } lg && rightGoal is { } rg)
            {
                var goalScale = (rg - lg) / (2 * GoalLine);
                if (Math.Abs(goalScale / scale - 1) < 0.03)
                {
                    var boards = (r - l + 1) / goalScale / 2;
                    return new RinkMeasure((lg + rg) / 2.0, centreY, goalScale,
                        $"goal lines at x {lg:F0} and {rg:F0} (boards then at +-{boards:F2} m){check}", widthOk);
                }
            }
            return new RinkMeasure(centreX, centreY, scale, how + ", goal lines not found", widthOk);
        }
        return new RinkMeasure(w / 2.0, h / 2.0, w / IceLength, "no board outline found; the picture's width is taken as the rink", Found: false);
    }

    /// <summary>Makes the square texture: the frame, sampled from the picture with area filtering, fully opaque.</summary>
    public static RgbaImage Fit(RgbaImage source, FullIceFit fit, int size = TextureSize, RinkMeasure? measure = null)
    {
        var output = new byte[size * size * 4];
        // Source pixels per texel step in each direction.
        double cx, cy, stepX, stepY;
        if (fit == FullIceFit.Template)
        {
            cx = source.Width / 2.0;
            cy = source.Height / 2.0;
            stepX = (double)source.Width / size;
            stepY = (double)source.Height / size;
        }
        else
        {
            measure ??= Measure(source);
            cx = measure.CentreX;
            cy = measure.CentreY;
            stepX = FrameLength / size * measure.PixelsPerMetre;
            stepY = FrameWidth / size * measure.PixelsPerMetre;
        }

        var samplesX = Math.Clamp((int)Math.Ceiling(stepX), 1, 8);
        var samplesY = Math.Clamp((int)Math.Ceiling(stepY), 1, 8);
        Parallel.For(0, size, j =>
        {
            Span<double> sum = stackalloc double[3];
            for (var i = 0; i < size; i++)
            {
                sum.Clear();
                for (var b = 0; b < samplesY; b++)
                {
                    var sy = cy + (j - size / 2.0 + (b + 0.5) / samplesY) * stepY;
                    for (var a = 0; a < samplesX; a++)
                    {
                        var sx = cx + (i - size / 2.0 + (a + 0.5) / samplesX) * stepX;
                        Bilinear(source, sx - 0.5, sy - 0.5, sum);
                    }
                }
                var o = (j * size + i) * 4;
                var n = samplesX * samplesY;
                for (var c = 0; c < 3; c++)
                    output[o + c] = (byte)Math.Clamp(Math.Round(sum[c] / n), 0, 255);
                output[o + 3] = 255;
            }
        });
        if (fit == FullIceFit.Layout)
        {
            HideDrawnNets(output, size);
            ShrinkCreases(output, size, CreaseScale);
        }
        return new RgbaImage(size, size, output);
    }

    /// <summary>
    /// Shrinks the area in front of each goal line (the crease and a margin of ice) by <paramref name="scale"/> round
    /// the middle of the goal line, and fills what it no longer covers with the ice colour around it.
    /// </summary>
    public static void ShrinkCreases(byte[] output, int size, double scale)
    {
        if (scale >= 0.999)
            return;
        const double depth = 2.1, half = 1.45, keep = 0.06; // the box, and the goal line's half-width left alone
        var copy = (byte[])output.Clone();
        double ZOf(int i) => ((i + 0.5) / size - 0.5) * FrameLength;
        double XOf(int j) => (0.5 - (j + 0.5) / size) * FrameWidth;
        int I(double z) => Math.Clamp((int)((0.5 + z / FrameLength) * size), 0, size - 1);
        int J(double x) => Math.Clamp((int)((0.5 - x / FrameWidth) * size), 0, size - 1);
        foreach (var side in new[] { -1, 1 })
        {
            // The ice colour: light texels in a strip beside the box.
            double r = 0, g = 0, b = 0;
            var n = 0;
            for (var j = J(half + 0.4); j <= J(half + 0.1); j++)
                for (var i = Math.Min(I(side * (GoalLine - depth)), I(side * (GoalLine - 0.3))); i <= Math.Max(I(side * (GoalLine - depth)), I(side * (GoalLine - 0.3))); i++)
                {
                    var o = (j * size + i) * 4;
                    if (copy[o] + copy[o + 1] + copy[o + 2] < 600)
                        continue;
                    r += copy[o]; g += copy[o + 1]; b += copy[o + 2]; n++;
                }
            if (n == 0)
                continue;
            int i0 = Math.Min(I(side * (GoalLine - depth)), I(side * (GoalLine - keep))), i1 = Math.Max(I(side * (GoalLine - depth)), I(side * (GoalLine - keep)));
            for (var j = J(half); j <= J(-half); j++)
                for (var i = i0; i <= i1; i++)
                {
                    var front = side * GoalLine - ZOf(i); // metres in front of the goal line, signed toward centre
                    var d = side * front;
                    if (d < keep || d > depth)
                        continue;
                    var sd = d / scale;
                    var sx = XOf(j) / scale;
                    var o = (j * size + i) * 4;
                    if (sd > depth || Math.Abs(sx) > half)
                    {
                        output[o] = (byte)(r / n); output[o + 1] = (byte)(g / n); output[o + 2] = (byte)(b / n);
                        continue;
                    }
                    var so = (J(sx) * size + I(side * (GoalLine - sd))) * 4;
                    output[o] = copy[so]; output[o + 1] = copy[so + 1]; output[o + 2] = copy[so + 2];
                }
        }
    }

    /// <summary>
    /// TheFaceoff.net draws the nets, grey, behind the goal lines; under the game's own nets they show as a ghost. The
    /// grey (unsaturated, darker than ice) texels in a box behind each goal line become the ice colour just past it.
    /// </summary>
    private static void HideDrawnNets(byte[] output, int size)
    {
        int U(double z) => (int)Math.Round((0.5 + z / FrameLength) * size);
        int V(double x) => (int)Math.Round((0.5 - x / FrameWidth) * size);
        foreach (var side in new[] { -1, 1 })
        {
            int u0 = U(side * (GoalLine + 0.06)), u1 = U(side * (GoalLine + 1.5));
            int iceU0 = U(side * (GoalLine + 1.7)), iceU1 = U(side * (GoalLine + 2.0));
            if (u0 > u1) (u0, u1) = (u1, u0);
            if (iceU0 > iceU1) (iceU0, iceU1) = (iceU1, iceU0);
            int v0 = V(1.3), v1 = V(-1.3);
            double r = 0, g = 0, b = 0;
            var n = 0;
            for (var j = v0; j <= v1; j++)
                for (var i = iceU0; i <= iceU1; i++)
                {
                    var o = (j * size + i) * 4;
                    if (output[o] + output[o + 1] + output[o + 2] < 600)
                        continue;
                    r += output[o]; g += output[o + 1]; b += output[o + 2]; n++;
                }
            if (n == 0)
                continue;
            for (var j = v0; j <= v1; j++)
                for (var i = u0; i <= u1; i++)
                {
                    var o = (j * size + i) * 4;
                    int cr = output[o], cg = output[o + 1], cb = output[o + 2];
                    var spread = Math.Max(cr, Math.Max(cg, cb)) - Math.Min(cr, Math.Min(cg, cb));
                    if (spread < 30 && cr + cg + cb < (r + g + b) / n - 12)
                    {
                        output[o] = (byte)(r / n); output[o + 1] = (byte)(g / n); output[o + 2] = (byte)(b / n);
                    }
                }
        }
    }

    private static void Bilinear(RgbaImage image, double x, double y, Span<double> sum)
    {
        x = Math.Clamp(x, 0, image.Width - 1);
        y = Math.Clamp(y, 0, image.Height - 1);
        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, image.Width - 1);
        var y1 = Math.Min(y0 + 1, image.Height - 1);
        var fx = x - x0;
        var fy = y - y0;
        var p = image.Pixels;
        for (var c = 0; c < 3; c++)
        {
            var top = p[(y0 * image.Width + x0) * 4 + c] * (1 - fx) + p[(y0 * image.Width + x1) * 4 + c] * fx;
            var bottom = p[(y1 * image.Width + x0) * 4 + c] * (1 - fx) + p[(y1 * image.Width + x1) * 4 + c] * fx;
            sum[c] += top * (1 - fy) + bottom * fy;
        }
    }

    /// <summary>Encodes the fitted texture in the slot's own format (BC3 slots are written as BC7, see TexturePipeline).</summary>
    public static byte[] Encode(byte[] stockSlot, RgbaImage fitted, CompressionQuality quality = CompressionQuality.Balanced) =>
        TexturePipeline.Import(stockSlot, fitted, quality).Gnf;

    /// <summary>
    /// A review picture: the source on top, and below it the texture as the game will lay it on the ice (un-squeezed
    /// to 2:1), with the game's ice outline drawn in magenta. Pass the decoded texture to check the encoded result.
    /// </summary>
    public static RgbaImage Preview(RgbaImage? source, RgbaImage texture, int width = 2048)
    {
        var gap = source == null ? 0 : 24;
        var topHeight = source == null ? 0 : (int)Math.Round((double)source.Height * width / source.Width);
        var bottomHeight = width / 2;
        var height = topHeight + gap + bottomHeight;
        var output = new byte[width * height * 4];
        for (var i = 0; i < output.Length; i += 4)
        {
            output[i] = 40; output[i + 1] = 40; output[i + 2] = 46; output[i + 3] = 255;
        }

        if (source != null)
            Blit(TexturePipeline.Resize(source, width, topHeight), output, width, 0);
        var flat = TexturePipeline.Resize(texture, width, bottomHeight);
        Blit(flat, output, width, topHeight + gap);

        // Ice outline: the rounded rectangle +-30.48 by +-12.99 m in frame coordinates.
        var mx = width / FrameLength;
        var my = bottomHeight / FrameWidth;
        void Dot(double fx, double fy)
        {
            var x = (int)Math.Round(width / 2.0 + fx * mx);
            var y = (int)Math.Round(topHeight + gap + bottomHeight / 2.0 + fy * my);
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var xx = x + dx; var yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= width || yy >= height)
                        continue;
                    var o = (yy * width + xx) * 4;
                    output[o] = 255; output[o + 1] = 0; output[o + 2] = 255;
                }
        }
        double hl = IceLength / 2, hw = IceWidth / 2, r = CornerRadius;
        for (var t = 0.0; t <= 1.0; t += 0.0005)
        {
            var z = -hl + r + t * (2 * hl - 2 * r);
            Dot(z, -hw); Dot(z, hw);
            var x = -hw + r + t * (2 * hw - 2 * r);
            Dot(-hl, x); Dot(hl, x);
            var angle = t * Math.PI / 2;
            Dot(hl - r + r * Math.Cos(angle), hw - r + r * Math.Sin(angle));
            Dot(-hl + r - r * Math.Cos(angle), hw - r + r * Math.Sin(angle));
            Dot(hl - r + r * Math.Cos(angle), -hw + r - r * Math.Sin(angle));
            Dot(-hl + r - r * Math.Cos(angle), -hw + r - r * Math.Sin(angle));
        }
        return new RgbaImage(width, height, output);
    }

    private static void Blit(RgbaImage image, byte[] output, int width, int top)
    {
        for (var y = 0; y < image.Height; y++)
            Buffer.BlockCopy(image.Pixels, y * image.Width * 4, output, ((top + y) * width) * 4, image.Width * 4);
    }
}
