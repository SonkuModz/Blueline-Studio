using System.Text.RegularExpressions;

namespace Blueline.Core.Teams;

public enum UniformSize { Large, Small }

public enum UniformTuck { None, Left, Right, Full }

public enum UniformPants { Long, Short }

/// <summary>A team's uniform sheets for one kit. The jersey is always there; the game may have no pants or socks for it.</summary>
public sealed record UniformKit(string Brand, string Slug, string Kit, string Jersey, string? Pant, string? Sock)
{
    public string Key => $"{Brand}_{Slug}_{Kit}";

    public string Label
    {
        get
        {
            var brand = Brand switch { "adidas" => "Adidas", "rbk" => "Reebok", "nineties" => "Nineties", _ => Brand };
            var kit = Kit switch { "home" => "Home", "away" => "Away", "two" => "Alternate 1", "three" => "Alternate 2", _ => char.ToUpperInvariant(Kit[0]) + Kit[1..] };
            return $"{brand}, {kit}";
        }
    }
}

/// <summary>The meshes a skater's uniform is drawn on. All sit in one shared pose, so they fit together as they are.</summary>
public sealed record UniformModels(string Jersey, string JerseyTie, string Pant, string Sock, IReadOnlyList<string> Gear);

/// <summary>
/// Links uniform sheets to meshes by name: `jersey_adidas_toronto_home_cm` is drawn on `jersey_skater_adidas_*`. The game's
/// own material link is unknown, so this is matched by naming and checked by eye (docs/RESEARCH.md section 6.6).
/// </summary>
public static class UniformKits
{
    private const string Folder = "nhl/data/kettle/rendering/characters/player/";

    private static readonly Regex Sheet = new(@"^(?<part>jersey|pant|sock|font|name)_(?<brand>adidas|rbk|nineties)_(?<slug>[a-z0-9_]+)_(?<kit>[a-z0-9]+)$", RegexOptions.Compiled);
    private static readonly Regex MapSuffix = new("_(?:cm|nm|coeff|sm)$", RegexOptions.Compiled);

    private static string Stem(string path) => Path.GetFileNameWithoutExtension(path);

    /// <summary>Reads a sheet's name once its map suffix is off, so the kit is its last word.</summary>
    private static Match Read(string path) => Sheet.Match(MapSuffix.Replace(Stem(path), ""));

    private static (string Part, string Key)? Parse(string path)
    {
        var match = Read(path);
        return match.Success ? (match.Groups["part"].Value, $"{match.Groups["brand"].Value}_{match.Groups["slug"].Value}_{match.Groups["kit"].Value}") : null;
    }

    /// <summary>The kits in a team's art that have a jersey to draw.</summary>
    public static IReadOnlyList<UniformKit> From(IEnumerable<TeamArt> art)
    {
        var colour = art.Where(a => a.Category == TeamArtCategory.Uniforms && Stem(a.Path).EndsWith("_cm", StringComparison.Ordinal))
            .Select(a => (Art: a, Match: Read(a.Path)))
            .Where(s => s.Match.Success)
            .ToList();
        return colour
            .GroupBy(s => (Brand: s.Match.Groups["brand"].Value, Slug: s.Match.Groups["slug"].Value, Kit: s.Match.Groups["kit"].Value))
            .Select(group =>
            {
                string? Part(string part) => group.FirstOrDefault(s => s.Match.Groups["part"].Value == part).Art?.Path;
                return Part("jersey") is { } jersey ? new UniformKit(group.Key.Brand, group.Key.Slug, group.Key.Kit, jersey, Part("pant"), Part("sock")) : null;
            })
            .OfType<UniformKit>()
            .ToList();
    }

    /// <summary>The kit a sheet belongs to, whichever of its sheets this is.</summary>
    public static UniformKit? KitOf(IEnumerable<UniformKit> kits, string path) =>
        Parse(path) is { } sheet ? kits.FirstOrDefault(k => k.Key == sheet.Key) : null;

    public static UniformModels ModelsFor(string brand, UniformSize size, UniformTuck tuck, UniformPants pants)
    {
        var (jerseyTuck, sockTuck, skateTuck) = tuck switch
        {
            UniformTuck.Left => ("lefttuck", "fronttuck", "fronttuck"),
            UniformTuck.Right => ("righttuck", "backtuck", "backtuck"),
            UniformTuck.Full => ("fulltuck", "fulltuck", "fulltuck"),
            _ => ("notuck", "notuck", "notuck"),
        };
        return new UniformModels(
            $"{Folder}jersey_skater_{brand}_{jerseyTuck}_{size.ToString().ToLowerInvariant()}_high.mcd",
            $"{Folder}jerseytie_skater_{brand}_high.mcd",
            $"{Folder}pant_player_{pants.ToString().ToLowerInvariant()}_high.mcd",
            $"{Folder}sock_player_{sockTuck}_high.mcd",
            [$"{Folder}skate_bauer_{skateTuck}_high.mcd", $"{Folder}glove_left_high.mcd", $"{Folder}glove_right_high.mcd"]);
    }
}
