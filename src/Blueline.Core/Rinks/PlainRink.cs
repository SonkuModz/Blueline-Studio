using Blueline.Formats.Textures;

namespace Blueline.Core.Rinks;

/// <summary>
/// A plain NHL rink in the full-ice frame, for teams with no rink picture: white ice with the standard markings
/// (NHL rule book sizes, in feet), the team's own centre logo where the stock model put it (a 10.42 m square at centre
/// ice, same orientation), and the team's own red line strip on top. With the full-ice model every team's centre logo
/// slot covers the whole rink, so a team left with its stock logo would show it stretched over the ice; this is what
/// such teams get instead.
/// </summary>
public static class PlainRink
{
    private const double M = 3.28084; // feet per metre
    private const double Thin = 0.1; // half a line: 2 in in the rule book, a little more so it survives 6 cm texels
    private const double GoalLine = 0.17; // half the goal line, drawn about 4 in so it reads at this resolution
    private static readonly double GoalFeet = FullIce.GoalLine * 3.28084; // where the game's nets stand

    private static readonly (double R, double G, double B) Ice = (238, 241, 244);
    private static readonly (double R, double G, double B) Red = (200, 32, 46);
    private static readonly (double R, double G, double B) Blue = (0, 60, 166);
    private static readonly (double R, double G, double B) Crease = (124, 190, 232);

    /// <summary>The stock logo patch: +-5.21 m around centre ice, u along +z, v along -x.</summary>
    public const double LogoHalf = 5.21;
    /// <summary>The stock red line strip: +-0.22 m across, u across (+z), v along -x over the ice width.</summary>
    public const double LineHalf = 0.22;

    /// <param name="logo">The team's centre logo texture as the game holds it (with alpha), or null.</param>
    /// <param name="centreLine">The team's <c>ice_cline</c> strip (64 x 4096), or null for a plain red line.</param>
    public static RgbaImage Draw(RgbaImage? logo, RgbaImage? centreLine, int size = FullIce.TextureSize)
    {
        var output = new byte[size * size * 4];
        const int sz = 4, sx = 2; // samples per texel along z (long) and x
        Parallel.For(0, size, j =>
        {
            for (var i = 0; i < size; i++)
            {
                double r = 0, g = 0, b = 0;
                for (var bj = 0; bj < sx; bj++)
                {
                    var v = (j + (bj + 0.5) / sx) / size;
                    var x = (0.5 - v) * FullIce.FrameWidth;
                    for (var ai = 0; ai < sz; ai++)
                    {
                        var u = (i + (ai + 0.5) / sz) / size;
                        var z = (u - 0.5) * FullIce.FrameLength;
                        var c = Sample(z, x, logo, centreLine);
                        r += c.R; g += c.G; b += c.B;
                    }
                }
                var n = sz * sx;
                var o = (j * size + i) * 4;
                // A faint frost, so the ice is not one flat colour (a perfectly flat re-encoded texture once came out
                // garbled in game, see the flat scorebug notes) and looks less like paper.
                var frost = Frost(i, j);
                output[o] = (byte)Math.Clamp(Math.Round(r / n + frost), 0, 255);
                output[o + 1] = (byte)Math.Clamp(Math.Round(g / n + frost), 0, 255);
                output[o + 2] = (byte)Math.Clamp(Math.Round(b / n + frost), 0, 255);
                output[o + 3] = 255;
            }
        });
        return new RgbaImage(size, size, output);
    }

    /// <summary>Deterministic grain of about +-2 levels plus a soft large-scale swirl.</summary>
    private static double Frost(int i, int j)
    {
        var h = (uint)(i * 73856093) ^ (uint)(j * 19349663);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        var grain = (h & 0xFF) / 255.0 * 4 - 2;
        var swirl = 1.5 * Math.Sin(i * 0.031 + Math.Sin(j * 0.047) * 2) * Math.Cos(j * 0.023 - i * 0.011);
        return grain + swirl;
    }

    /// <summary>The colour at a point on the ice, z and x in metres.</summary>
    private static (double R, double G, double B) Sample(double zm, double xm, RgbaImage? logo, RgbaImage? centreLine)
    {
        var colour = Markings(zm * M, xm * M);

        if (logo != null && Math.Abs(zm) <= LogoHalf && Math.Abs(xm) <= LogoHalf)
        {
            var p = Texel(logo, (zm + LogoHalf) / (2 * LogoHalf), (LogoHalf - xm) / (2 * LogoHalf));
            colour = Over(colour, p);
        }

        if (Math.Abs(zm) <= LineHalf && Math.Abs(xm) <= FullIce.IceWidth / 2)
        {
            if (centreLine != null)
            {
                // The game's strips are stored pale (about 206,154,156 for a red line); squaring brings them to the
                // depth of the other markings.
                var t = Texel(centreLine, (zm + LineHalf) / (2 * LineHalf), 0.5 - xm / FullIce.IceWidth);
                colour = Over(colour, (t.R * t.R / 255, t.G * t.G / 255, t.B * t.B / 255, t.A));
            }
            else if (Math.Abs(zm * M) <= 0.5)
                colour = Red;
        }
        return colour;
    }

    private static (double R, double G, double B) Over((double R, double G, double B) under, (double R, double G, double B, double A) over) =>
        (under.R + (over.R - under.R) * over.A, under.G + (over.G - under.G) * over.A, under.B + (over.B - under.B) * over.A);

    private static (double R, double G, double B, double A) Texel(RgbaImage image, double u, double v)
    {
        var x = Math.Clamp((int)(u * image.Width), 0, image.Width - 1);
        var y = Math.Clamp((int)(v * image.Height), 0, image.Height - 1);
        var o = (y * image.Width + x) * 4;
        return (image.Pixels[o], image.Pixels[o + 1], image.Pixels[o + 2], image.Pixels[o + 3] / 255.0);
    }

    /// <summary>True inside the 200 x 85 ft rink with 28 ft corners.</summary>
    public static bool InRink(double z, double x)
    {
        z = Math.Abs(z);
        x = Math.Abs(x);
        if (z > 100 || x > 42.5)
            return false;
        if (z > 72 && x > 14.5)
            return (z - 72) * (z - 72) + (x - 14.5) * (x - 14.5) <= 28 * 28;
        return true;
    }

    /// <summary>NHL markings, z along the rink and x across, both in feet from centre ice.</summary>
    private static (double R, double G, double B) Markings(double z, double x)
    {
        if (!InRink(z, x))
            return Ice;
        var az = Math.Abs(z);
        var ax = Math.Abs(x);

        // Blue: the blue lines (inner edges 25 ft from centre), the centre circle and dot.
        if (az >= 25 && az <= 26)
            return Blue;
        var rc = Math.Sqrt(z * z + x * x);
        if (Math.Abs(rc - 15) <= Thin || rc <= 0.5)
            return Blue;

        // The goal crease: 6 ft arc, 4 ft sides, in front of the goal line (the game's, 89.1 ft from centre), shrunk
        // like the TheFaceoff.net ones so its sides meet the game's posts.
        var d = GoalFeet - az; // distance in front of the goal line
        var side = 4 * FullIce.CreaseScale;
        var arc = 6 * FullIce.CreaseScale;
        if (d >= 0 && ax <= side)
        {
            var rr = Math.Sqrt(d * d + x * x);
            if (rr <= arc)
            {
                var outline = ax >= side - 2 * Thin || rr >= arc - 2 * Thin;
                return outline ? Red : Crease;
            }
        }

        // Goal lines and the trapezoid behind them (11 ft wide at the goal line, 14 ft at the boards).
        if (Math.Abs(az - GoalFeet) <= GoalLine)
            return Red;
        if (az > GoalFeet && Math.Abs(ax - (11 + 3 * (az - GoalFeet) / 11)) <= Thin)
            return Red;

        // Neutral zone dots: 5 ft inside the blue lines, 22 ft either side.
        if (Dot(az - 20, ax - 22, 1))
            return Red;

        // End zone circles, 20 ft out from the goal lines and 22 ft either side.
        var cz = az - 69;
        var cx = ax - 22;
        var r = Math.Sqrt(cz * cz + cx * cx);
        if (Math.Abs(r - 15) <= Thin || r <= 1)
            return Red;
        // Hash marks: 2 ft long, outside the circle, 5 ft 11 in apart.
        if (Math.Abs(Math.Abs(cz) - 2.96) <= Thin && Math.Abs(cx) >= 14.9 && Math.Abs(cx) <= 17)
            return Red;
        // The L marks round the dot: 4 ft along the rink and 3 ft across, starting 2 ft and 9 in from the dot.
        if (Math.Abs(Math.Abs(cx) - 0.75) <= Thin && Math.Abs(cz) >= 2 && Math.Abs(cz) <= 6)
            return Red;
        if (Math.Abs(Math.Abs(cz) - 2) <= Thin && Math.Abs(cx) >= 0.75 && Math.Abs(cx) <= 3.75)
            return Red;

        // The referee's crease: a 10 ft half circle at the side boards (bottom of the picture, -x).
        var rx = x + 42.5;
        var rref = Math.Sqrt(z * z + rx * rx);
        if (Math.Abs(rref - 10) <= Thin)
            return Red;

        return Ice;
    }

    private static bool Dot(double dz, double dx, double radius) => dz * dz + dx * dx <= radius * radius;
}
