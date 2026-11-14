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

    /// <summary>Tints one piece. Maps that are missing are skipped; masks are sampled at the detail map's size.</summary>
    public static RgbaImage Tint(RgbaImage detail, RgbaImage? ambient, IReadOnlyList<RgbaImage?> masks, IReadOnlyList<int> zones)
    {
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
