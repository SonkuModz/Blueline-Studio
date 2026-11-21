using Blueline.Formats.Textures;

namespace Blueline.Core.Teams;

/// <summary>What to put in a new slot's files: a jersey sheet, the slot whose other parts to copy, and a menu picture.</summary>
public sealed record SlotArtOptions(RgbaImage? Jersey = null, UniformSlot? CopyFrom = null, RgbaImage? MenuPicture = null, bool MakeJerseyMaps = true);

/// <summary>One file a new slot writes, or skips with the reason.</summary>
public sealed record SlotArtFile(string Path, byte[]? Content, string Note);

/// <summary>
/// Fills a kit name's files for a new slot. A borrowed name still has its old owner's art in every part, and the game draws
/// all of them (seen in game 2026-11-20: a new jersey with the donor's pants and socks), so every part the name has is
/// written: from the given jersey sheet, else from the slot copied from. Files the name does not have are skipped, since the
/// game only loads files its vault lists.
/// </summary>
public static class UniformSlotArt
{
    public const string PlayerFolder = "nhl/data/kettle/rendering/characters/player/";

    /// <summary>The parts of a kit and their maps, as they appear in file names.</summary>
    public static readonly IReadOnlyList<(string Part, string[] Maps)> Parts =
    [
        ("jersey", ["cm", "nm", "coeff"]), ("pant", ["cm", "nm", "coeff"]), ("sock", ["cm"]), ("font", ["cm", "nm", "sm"]), ("name", [""]), ("helmetlogo", ["0"]),
    ];

    public static string PlayerFile(string part, string kitName, string map) => $"{PlayerFolder}{part}_{kitName}{(map.Length > 0 ? "_" + map : "")}.gnf";

    public static IReadOnlyList<string> MenuPictures(int style, int artId, int variant) =>
        [$"nhl/ui/kettle_assets/teamjerseys/jersey_{style}_{artId}_{variant}.gnf", $"nhl/ui/kettle_assets/teamjerseysxl/jersey_{style}_{artId}_{variant}.gnf"];

    /// <summary>Every file of a kit name, in part order, with the two menu pictures last.</summary>
    public static IReadOnlyList<string> FilesOf(string kitName, int style, int artId, int variant) =>
        [.. Parts.SelectMany(p => p.Maps.Select(m => PlayerFile(p.Part, kitName, m))), .. MenuPictures(style, artId, variant)];

    /// <summary>Files are read through <paramref name="read"/> when given (the open project's copy, say), else from the game.
    /// The plain bases for made normal and coeff maps always come from the game's sheets.</summary>
    public static IReadOnlyList<SlotArtFile> Make(VirtualFileSystem vfs, FreeKit kit, SlotArtOptions options, Func<string, byte[]?>? read = null)
    {
        if (options.Jersey == null && options.CopyFrom == null && options.MenuPicture == null)
            return [];
        var source = options.CopyFrom;
        if (source != null && source.Style != kit.Style)
            throw new InvalidOperationException($"The kit to copy from is {source.BrandLabel} but {kit.Name} is {UniformSlots.BrandLabelOf(kit.Style)}; their sheets are laid out differently.");
        if (source != null && source.KitName == null)
            throw new InvalidOperationException($"The kit to copy from has no known file name.");

        byte[]? Read(string path) => read != null ? read(path) : vfs.Find(path) is { } file ? vfs.ReadContent(file) : null;
        RgbaImage? Decode(string path) => Read(path) is { } data && GameTexture.TryOpen(data) is { Blocker: null } texture ? texture.DecodeMip(0) : null;

        RgbaImage? normal = null, coeff = null;
        if (options.Jersey != null && options.MakeJerseyMaps)
        {
            var normalBase = JerseyMaps.PlainBase(vfs, kit.Brand, "nm");
            var coeffBase = JerseyMaps.PlainBase(vfs, kit.Brand, "coeff");
            var mapOptions = new JerseyMapOptions();
            var areas = JerseyMaps.FromColour(options.Jersey, mapOptions.Tolerance, normalBase.Width, normalBase.Height);
            normal = JerseyMaps.Normal(normalBase, areas, mapOptions);
            coeff = JerseyMaps.Coeff(coeffBase, areas, mapOptions);
        }

        var files = new List<SlotArtFile>();
        void Put(string target, RgbaImage? image, string? copyPath, string what)
        {
            if (Read(target) is not { } original)
            {
                files.Add(new SlotArtFile(target, null, $"skipped: {kit.Name} has no such file, and the game only loads files its vault lists"));
                return;
            }
            if (image == null && copyPath != null)
            {
                image = Decode(copyPath);
                if (image == null && Read(copyPath) is { } raw && raw.Length == original.Length)
                {
                    files.Add(new SlotArtFile(target, raw, $"copied from {Path.GetFileName(copyPath)}"));
                    return;
                }
                what = $"from {Path.GetFileName(copyPath)}";
            }
            if (image == null)
            {
                files.Add(new SlotArtFile(target, null, copyPath == null ? "left as it is" : $"left as it is: {Path.GetFileName(copyPath)} is missing or unreadable"));
                return;
            }
            if (TexturePipeline.ImportBlocker(original) is { } blocker)
            {
                files.Add(new SlotArtFile(target, null, $"left as it is: {blocker}"));
                return;
            }
            files.Add(new SlotArtFile(target, TexturePipeline.Import(original, image).Gnf, what));
        }

        foreach (var (part, maps) in Parts)
            foreach (var map in maps)
            {
                var target = PlayerFile(part, kit.Name, map);
                var copy = source == null ? null : PlayerFile(part, source.KitName!, map);
                var image = (part, map) switch
                {
                    ("jersey", "cm") => options.Jersey,
                    ("jersey", "nm") => normal,
                    ("jersey", "coeff") => coeff,
                    _ => null,
                };
                Put(target, image, image == null ? copy : null, image == null ? "" : part == "jersey" && map == "cm" ? "your jersey sheet" : "made from your jersey sheet");
            }

        var menus = MenuPictures(kit.Style, kit.ArtId, kit.Variant);
        var sourceMenus = source == null ? null : MenuPictures(source.Style, source.ArtId, source.Variant);
        for (var i = 0; i < menus.Count; i++)
            Put(menus[i], options.MenuPicture, options.MenuPicture == null ? sourceMenus?[i] : null, options.MenuPicture == null ? "" : "your menu picture");
        return files;
    }
}
