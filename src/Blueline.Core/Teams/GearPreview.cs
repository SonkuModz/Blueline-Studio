using Blueline.Formats.Textures;

namespace Blueline.Core.Teams;

/// <summary>The glove and helmet models the game has textures for, and the paths of their meshes and maps.</summary>
public sealed record GearModel(GearPart Part, string Name)
{
    private const string Folder = "nhl/data/kettle/rendering/characters/player/";

    public string Label => Name.Replace('_', ' ');

    /// <summary>All gloves share one mesh per hand; the model is in the textures. Each helmet has its own mesh.</summary>
    public IReadOnlyList<(string Mesh, string Prefix)> Pieces => Part == GearPart.Gloves
        ? [($"{Folder}glove_left_high.mcd", $"{Folder}glove_left_{Name}_"), ($"{Folder}glove_right_high.mcd", $"{Folder}glove_right_{Name}_")]
        : [($"{Folder}helmet_{Name}_high.mcd", $"{Folder}helmet_current_{Name}_")];

    /// <summary>The untinted layer: palm, stitching and decals in colour, with alpha where they cover the tint.</summary>
    public string DetailMap(string prefix) => prefix + (Part == GearPart.Gloves ? "dm.gnf" : "nhl_dm.gnf");

    public static string AmbientMap(string prefix) => prefix + "am.gnf";

    /// <summary>Tint masks: tm1 holds zones 1 to 3 in red, green and blue, tm2 zones 4 to 6, and so on.</summary>
    public static string MaskMap(string prefix, int number) => $"{prefix}tm{number}.gnf";

    public override string ToString() => Label;
}

/// <summary>
/// Colours a glove or helmet the way its zone colours suggest, for the 3D preview. Each tint mask channel is one zone (checked
/// by eye: zone 7 of a CCM glove is its logo, as seen in game; helmet zone 1 is the shell). The game's own shader is not
/// known, so this is an approximation: the zone colours under the detail layer, shaded by the ambient map.
/// </summary>
public static class GearPreview
{
    private const string Folder = "nhl/data/kettle/rendering/characters/player/";
    public const string DefaultGloves = "ccm_4rollpro";
    public const string DefaultHelmet = "bauer_reakt100";

    public static IReadOnlyList<GearModel> Models(GearPart part, IEnumerable<string> paths)
    {
        var set = paths as IReadOnlySet<string> ?? paths.ToHashSet(StringComparer.Ordinal);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in set)
        {
            if (!path.StartsWith(Folder, StringComparison.Ordinal))
                continue;
            var name = path[Folder.Length..];
            if (part == GearPart.Gloves && name.StartsWith("glove_left_", StringComparison.Ordinal) && name.EndsWith("_tm1.gnf", StringComparison.Ordinal))
                names.Add(name["glove_left_".Length..^"_tm1.gnf".Length]);
            else if (part == GearPart.Helmet && name.StartsWith("helmet_current_", StringComparison.Ordinal) && name.EndsWith("_tm1.gnf", StringComparison.Ordinal))
                names.Add(name["helmet_current_".Length..^"_tm1.gnf".Length]);
        }
        return names.Select(n => new GearModel(part, n))
            .Where(m => m.Pieces.All(p => set.Contains(p.Mesh) && set.Contains(m.DetailMap(p.Prefix))))
            .ToList();
    }

    /// <summary>
    /// Tints one piece. Maps that are missing are skipped; masks are sampled at the detail map's size. With a highlighted
    /// zone (and its map), the rest of the piece turns a flat dim grey and the zone gets an outline and light stripes in the
    /// outline colour, so it can be found on the model whatever its colour (a black zone on a black glove included).
    /// </summary>
    public static RgbaImage Tint(RgbaImage detail, RgbaImage? ambient, IReadOnlyList<RgbaImage?> masks, IReadOnlyList<int> zones,
        int highlight = 0, GearZoneMap? map = null, int outline = 0x00E5FF)
    {
        var stripe = Math.Max(4, detail.Width / 40);
        const int Base = 0x808080;
        var (w, h) = (detail.Width, detail.Height);
        var output = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                double r = 0, g = 0, b = 0, cover = 0;
                for (var m = 0; m < masks.Count; m++)
                {
                    if (masks[m] is not { } mask)
                        continue;
                    var p = Sample(mask, x, y, w, h);
                    for (var c = 0; c < 3; c++)
                    {
                        var zone = m * 3 + c;
                        if (zone >= zones.Count)
                            break;
                        var weight = mask.Pixels[p + c] / 255.0;
                        if (weight <= 0)
                            continue;
                        r += weight * (zones[zone] >> 16 & 0xFF);
                        g += weight * (zones[zone] >> 8 & 0xFF);
                        b += weight * (zones[zone] & 0xFF);
                        cover += weight;
                    }
                }
                if (cover > 1)
                    (r, g, b, cover) = (r / cover, g / cover, b / cover, 1);
                r += (1 - cover) * (Base >> 16 & 0xFF);
                g += (1 - cover) * (Base >> 8 & 0xFF);
                b += (1 - cover) * (Base & 0xFF);

                var d = (y * w + x) * 4;
                var alpha = detail.Pixels[d + 3] / 255.0;
                r = r * (1 - alpha) + detail.Pixels[d] * alpha;
                g = g * (1 - alpha) + detail.Pixels[d + 1] * alpha;
                b = b * (1 - alpha) + detail.Pixels[d + 2] * alpha;

                if (ambient != null)
                {
                    // The ambient map sits near 0.7 on open areas; scale it so they keep their colour.
                    var shade = Math.Min(1.0, ambient.Pixels[Sample(ambient, x, y, w, h)] / 180.0);
                    (r, g, b) = (r * shade, g * shade, b * shade);
                }
                if (highlight > 0 && map != null)
                {
                    var (or, og, ob) = (outline >> 16 & 0xFF, outline >> 8 & 0xFF, outline & 0xFF);
                    if (map.ZoneAtPixel(x, y, w, h) != highlight)
                    {
                        // Everything else goes to one flat grey with a trace of its shading, so the chosen zone stands alone.
                        var grey = 70 + (r * 0.3 + g * 0.59 + b * 0.11) * 0.15;
                        (r, g, b) = (grey, grey, grey + 6);
                    }
                    else if (map.IsEdge(x, y, w, h, highlight))
                        (r, g, b) = (or, og, ob);
                    else if ((x + y) / stripe % 3 == 0)
                        (r, g, b) = (r * 0.55 + or * 0.45, g * 0.55 + og * 0.45, b * 0.55 + ob * 0.45);
                }
                output[d] = (byte)Math.Clamp(r, 0, 255);
                output[d + 1] = (byte)Math.Clamp(g, 0, 255);
                output[d + 2] = (byte)Math.Clamp(b, 0, 255);
                output[d + 3] = 255;
            }
        return new RgbaImage(w, h, output);
    }

    private static int Sample(RgbaImage image, int x, int y, int w, int h) =>
        ((y * image.Height / h) * image.Width + x * image.Width / w) * 4;
}

/// <summary>
/// Which zone covers each point of a piece: the strongest tint mask channel where it is clearly set, else none. Used to list
/// the zones a model really has, to find a zone from a click on the model, and to highlight one.
/// </summary>
public sealed class GearZoneMap
{
    private const int Threshold = 96;
    private static readonly (int, int)[] Offsets = [(-3, 0), (3, 0), (0, -3), (0, 3), (-2, -2), (2, 2), (-2, 2), (2, -2)];
    private readonly byte[] _zones;

    private GearZoneMap(int width, int height, byte[] zones, IReadOnlyDictionary<int, double> coverage)
    {
        Width = width;
        Height = height;
        _zones = zones;
        Coverage = coverage;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Zone (1 to 12) to the share of the texture it covers, for zones that cover any of it.</summary>
    public IReadOnlyDictionary<int, double> Coverage { get; }

    public static GearZoneMap Build(IReadOnlyList<RgbaImage?> masks)
    {
        var first = masks.OfType<RgbaImage>().FirstOrDefault();
        var (w, h) = first == null ? (1, 1) : (first.Width, first.Height);
        var zones = new byte[w * h];
        var counts = new int[masks.Count * 3 + 1];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (best, zone) = (Threshold, 0);
                for (var m = 0; m < masks.Count; m++)
                {
                    if (masks[m] is not { } mask)
                        continue;
                    var p = ((y * mask.Height / h) * mask.Width + x * mask.Width / w) * 4;
                    for (var c = 0; c < 3; c++)
                        if (mask.Pixels[p + c] > best)
                            (best, zone) = (mask.Pixels[p + c], m * 3 + c + 1);
                }
                zones[y * w + x] = (byte)zone;
                counts[zone]++;
            }
        var coverage = new Dictionary<int, double>();
        for (var z = 1; z < counts.Length; z++)
            if (counts[z] > 0)
                coverage[z] = counts[z] / (double)(w * h);
        return new GearZoneMap(w, h, zones, coverage);
    }

    /// <summary>The zone at a texture coordinate. Coordinates outside 0 to 1 wrap, as the game's do.</summary>
    public int ZoneAt(double u, double v)
    {
        u -= Math.Floor(u);
        v -= Math.Floor(v);
        var x = Math.Min(Width - 1, (int)(u * Width));
        var y = Math.Min(Height - 1, (int)(v * Height));
        return _zones[y * Width + x];
    }

    /// <summary>The zone at a pixel of an image of another size laid over the same texture.</summary>
    public int ZoneAtPixel(int x, int y, int width, int height) => _zones[(y * Height / height) * Width + x * Width / width];

    /// <summary>True on the border of a zone, about three pixels wide at the given image size.</summary>
    public bool IsEdge(int x, int y, int width, int height, int zone)
    {
        foreach (var (dx, dy) in Offsets)
        {
            var (nx, ny) = (x + dx, y + dy);
            if (nx < 0 || ny < 0 || nx >= width || ny >= height || ZoneAtPixel(nx, ny, width, height) != zone)
                return true;
        }
        return false;
    }
}
