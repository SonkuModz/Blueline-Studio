using Blueline.Formats.Textures;

namespace Blueline.Core.Overlays;

/// <summary>
/// Takes the shading out of the stock score clock by replacing its shading textures, and nothing else. The layout is not touched, so
/// nothing moves. Textures under kettle_assets are the part of an overlay that a patch archive is known to replace in game, where a layout
/// edit was not (RESEARCH 6.9).
/// </summary>
public static class FlatScorebug
{
    /// <summary>Gradient overlays, glows and metal edges that only add depth. They become fully transparent, which also removes them from add and multiply blends.</summary>
    private static readonly string[] Cleared =
    [
        "scoreclock_colour_base_top_hl",
        "scoreclock_colour_base_bottom_hl",
        "scoreclock_colour_base_dark_grad",
        "scoreclock_color_gradient_cover",
        "scoreclock_logo_cover_gradient_cover",
        "scoreclock_logo_fade",
        "scoreclock_score_divider",
        "scoreclock_glow",
        "scoreclock_penalty_grad",
        "scoreclock_pp_bar_grad",
        "scoreclock_background_end",
    ];

    /// <summary>A texture, the one colour it becomes, and whether its soft edges become solid. Its outline is kept.</summary>
    private sealed record Flat(string Asset, uint Colour, bool Solid);

    private static readonly Flat[] Flattened =
    [
        new("scoreclock_background_mid", 0xE6E8EB, true),
        new("scoreclock_time_base", 0xF2F3F5, false),
        new("scoreclock_ad_background", 0xE6E8EB, true),
        new("scoreclock_right_cap_bg", 0xE6E8EB, true),
        new("scoreclock_left_cap", 0xE6E8EB, false),
        new("scoreclock_right_cap", 0xE6E8EB, false),
        new("scoreclock_top_message_bg", 0x1B2430, true),
    ];

    private const int SolidFrom = 64;

    public static IReadOnlyList<string> Assets => [.. Cleared, .. Flattened.Select(f => f.Asset)];

    /// <summary>The game paths that are replaced, for the textures this game has.</summary>
    public static IReadOnlyList<string> Paths(OverlayCatalog catalog) => Assets.Select(asset => catalog.AssetPath(asset)).OfType<string>().ToList();

    /// <summary>The assets that become transparent. These are the ones confirmed in game (2026-11-09).</summary>
    public static IReadOnlyList<string> ClearedAssets => Cleared;

    /// <summary>
    /// The replacement textures, ready to put in a project. Each keeps its size and format.
    /// The flat colour ones are left out unless asked for: in game the clock cell's came out as noise, cause not found (RESEARCH 6.9).
    /// <paramref name="only"/> limits it to the named assets, for testing one at a time.
    /// </summary>
    public static IReadOnlyList<(string Path, byte[] Content, string Description)> Build(OverlayCatalog catalog, Func<string, byte[]?> read, bool includeFlat = false, IReadOnlyCollection<string>? only = null)
    {
        var items = new List<(string, byte[], string)>();
        foreach (var asset in includeFlat || only != null ? Assets : Cleared)
        {
            if (only != null && !only.Contains(asset))
                continue;
            if (catalog.AssetPath(asset) is not { } path || read(path) is not { } original)
                continue;
            var texture = GameTexture.TryOpen(original) ?? throw new InvalidDataException($"{path} is not a texture this tool can read.");
            var flat = Flattened.FirstOrDefault(f => f.Asset == asset);
            var image = Make(texture.DecodeMip(0), flat);
            // The highest setting, so a flat colour is not stored in the single simplest block mode the first attempt used.
            items.Add((path, TexturePipeline.Import(original, image, BCnEncoder.Encoder.CompressionQuality.BestQuality).Gnf, flat == null ? "Overlay texture: shading removed" : "Overlay texture: made flat"));
        }
        return items;
    }

    private static RgbaImage Make(RgbaImage source, Flat? flat)
    {
        var pixels = new byte[source.Pixels.Length];
        if (flat == null)
            return source with { Pixels = pixels };

        for (var i = 0; i < source.Width * source.Height; i++)
        {
            var alpha = source.Pixels[i * 4 + 3];
            pixels[i * 4] = (byte)(flat.Colour >> 16);
            pixels[i * 4 + 1] = (byte)(flat.Colour >> 8);
            pixels[i * 4 + 2] = (byte)flat.Colour;
            pixels[i * 4 + 3] = flat.Solid && alpha >= SolidFrom ? (byte)255 : alpha;
        }
        return source with { Pixels = pixels };
    }
}
