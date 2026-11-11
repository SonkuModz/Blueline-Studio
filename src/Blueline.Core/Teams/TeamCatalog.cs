using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Blueline.Formats.Tdb;

namespace Blueline.Core.Teams;

public enum TeamArtCategory { Logos, CenterIce, Uniforms, Gear, Pucks, ArenaArt, ArenaBuilding, Crowd, MascotStaff, MenuArt, Models, Other }

/// <summary>One file that belongs to a team. Technical maps are the normal, specular and similar layers.</summary>
public sealed record TeamArt(string Path, TeamArtCategory Category, string Section, bool IsTechnical, int Order = 50)
{
    public string Name { get; } = Path[(Path.LastIndexOf('/') + 1)..];
    public bool IsTexture { get; } = Category != TeamArtCategory.Models;
}

public sealed record TeamInfo(int Index, int ArtId, string Name, string City, string Abbreviation, int LeagueId, string League,
    string? Slug, int CenterLine, string? ArenaName, string? ArenaCity, int Capacity, int YearOpened, IReadOnlyList<int> Colors)
{
    public override string ToString() => Name;
}

/// <summary>
/// Joins the game's team table to its art. Numbered art (logos, center ice, menu art) is found through the team's
/// art id and center line, which the table holds. Named art (uniforms, gear, crowd, arena) is found through a
/// name the game uses in file names, which the table does not hold, so it is matched from the team's name and the
/// matches are checked by tests. The rules come from reading NHL 19's paths and looking at the pictures.
/// </summary>
public sealed class TeamCatalog
{
    public const string DatabasePath = "nhl/data/common/gamemodes/database/t3db/nhl_game.db";
    public const string MetaPath = "nhl/data/common/gamemodes/database/t3db/nhl_game-meta.xml";

    public static readonly IReadOnlyDictionary<TeamArtCategory, string> CategoryNames = new Dictionary<TeamArtCategory, string>
    {
        [TeamArtCategory.Logos] = "Logos",
        [TeamArtCategory.CenterIce] = "Center ice",
        [TeamArtCategory.Uniforms] = "Uniforms",
        [TeamArtCategory.Gear] = "Gear",
        [TeamArtCategory.Pucks] = "Pucks",
        [TeamArtCategory.ArenaArt] = "Arena art",
        [TeamArtCategory.ArenaBuilding] = "Arena building",
        [TeamArtCategory.Crowd] = "Crowd",
        [TeamArtCategory.MascotStaff] = "Mascot and staff",
        [TeamArtCategory.MenuArt] = "Menu art",
        [TeamArtCategory.Models] = "Models",
        [TeamArtCategory.Other] = "Other",
    };

    private static readonly Dictionary<int, string> LeagueNames = new()
    {
        [0] = "NHL", [1] = "AHL", [2] = "SHL", [3] = "Liiga", [4] = "DEL", [5] = "Czech Extraliga", [6] = "Swiss League", [7] = "EBEL",
        [8] = "Allsvenskan", [9] = "Champions League", [10] = "International", [11] = "OHL", [12] = "QMJHL", [13] = "WHL", [14] = "Prospects",
        [15] = "ECHL", [16] = "Custom", [17] = "EASHL", [18] = "Mascots", [19] = "3v3", [20] = "Spengler Cup", [21] = "NHL Alumni",
        [22] = "NHL Alumni", [23] = "Pro-Am",
    };

    // Made-up sides whose names only look like team names ("Notes", "EAS"), so their names are never matched to art.
    private const int EashlLeague = 17;
    private const int ThreesLeague = 19;
    private const int ProAmLeague = 23;

    /// <summary>Names in font file names that are not teams.</summary>
    private static readonly HashSet<string> GenericSlugs = new(StringComparer.Ordinal)
        { "east", "west", "central", "atlantic", "pacific", "metropolitan", "frontend", "template", "legends" };

    /// <summary>Teams whose file name cannot be worked out from their own name. Null means the team has none.</summary>
    private static readonly Dictionary<string, string?> SlugOverrides = new(StringComparer.Ordinal)
    {
        ["sanjosebarracuda"] = "barracuda",
        ["quebecnordiques"] = null,
        ["quebecnordiquesalumni"] = null,
        ["hartfordwhalersalumni"] = null,
        ["minnesotanorthstarsalumni"] = null,
        ["clevelandmonsters"] = "lake_erie",
        ["wilkesbarrescrantonpenguins"] = "scranton",
        ["kalpakuopio"] = "kalpa_hockey_oy",
        ["vaasansport"] = "sport_vaasan",
        ["nurnbergicetigers"] = "thomas_sabo_ice_tigers",
        ["hcplzen1929"] = "hc_skoda_plzen",
        ["scltigers"] = "sc_langnau",
        ["northbaybattalion"] = "brampton_battalion",
        ["eckac"] = "ec_kac_klagenfurt",
    };

    /// <summary>
    /// NHL clubs' file names by art id. Mods rename teams but keep their files: a modded Utah Mammoth (22) still
    /// wears the arizona files, so a club is never matched by name. 10499 is a stock EASHL side that a mod can turn
    /// into a club, as seen with the Seattle Kraken wearing the pre_order kits; it only counts in the NHL league.
    /// </summary>
    private static readonly Dictionary<int, string> ClubSlugs = new()
    {
        [0] = "anaheim", [1] = "winnipeg", [2] = "boston", [3] = "buffalo", [4] = "calgary", [5] = "carolina", [6] = "chicago", [7] = "colorado",
        [8] = "columbus", [9] = "dallas", [10] = "detroit", [11] = "edmonton", [12] = "florida", [13] = "losangeles", [14] = "minnesota",
        [15] = "montreal", [16] = "nashville", [17] = "newjersey", [18] = "nyi", [19] = "nyr", [20] = "ottawa", [21] = "philadelphia",
        [22] = "arizona", [23] = "pittsburgh", [24] = "stlouis", [25] = "sanjose", [26] = "tampabay", [27] = "toronto", [28] = "vancouver",
        [29] = "washington", [32] = "vegas", [10499] = "pre_order",
    };

    /// <summary>Stadium folders that are not named after the team's file name.</summary>
    private static readonly Dictionary<string, string> StadiumFolders = new(StringComparer.Ordinal)
    {
        ["nyislanders"] = "nyi",
        ["nyrangers"] = "nyr",
        ["tampa"] = "tampabay",
        ["euroswissdavos"] = "hc_davos",
        ["euroswissresega"] = "hc_lugano",
        ["euroswissscbern"] = "sc_bern",
        ["euroswisszurich"] = "zsc_lions",
        ["euroswissbossard"] = "ev_zug",
    };

    private static readonly (string Folder, string Section, int Order)[] LogoFolders =
    [
        ("large", "Large", 0), ("medium", "Medium", 1), ("mediumglossy", "Medium glossy", 2), ("smallglossy", "Small glossy", 3),
        ("extrasmall", "Extra small", 4), ("largemono", "Large mono", 5), ("extralargemono", "Extra large mono", 6),
        ("extrasmallmono", "Extra small mono", 7), ("hutcardmedium", "HUT card, medium", 20), ("hutcardsmalllogo", "HUT card, small", 21),
        ("hutcardbw", "HUT card, black and white", 22), ("hutcardrare", "HUT card, rare tiers", 23), ("hutcardplayoffs", "HUT card, playoffs", 24),
        ("hutcardfreeagency", "HUT card, free agency", 25), ("hutcardheritage", "HUT card, heritage", 26), ("hutcardstadium", "HUT card, stadium", 27),
    ];

    private static readonly Regex FontSlug = new(@"^font_(?:adidas|rbk|nineties)_([a-z0-9_]+?)_(?:home|away|two|three)_cm$", RegexOptions.Compiled);
    private static readonly Regex TeamLogo = new(@"^nhl/ui/kettle_assets/teamlogos/([a-z]+)/t(?:_[a-z]+_)?(\d+)(?:_[a-z]{2})?\.(?:gnf|dds)$", RegexOptions.Compiled);
    private static readonly Regex NumberedArt = new(
        @"^nhl/(?:ui/kettle_assets/(?<zone>creationzone(?:_xl)?/logos|ultimateteam/collectionfolderlogos|ultimateteam/themedcards/[^/]+/logos|ultimateteam/collectiongridthumbnails|bgs/teambackground|widgets/mainmenu/seasontile|widgets/mainmenu/eashltile)/t?(?<art>\d+)" +
        @"|ui/kettle_assets/(?<jersey>teamjerseys(?:xl)?)/jersey_\d+_(?<art>\d+)_\d+" +
        @"|data/kettle/rendering/stadiums/common/(?<ring>powerring)_(?<art>\d+)_\d+" +
        @"|data/kettle/rendering/stadiums/common/(?<ice>ice_centerlogo|icesurface)_(?<art>\d+)_(?:cm|bm))(?:_[a-z]{2})?\.(?:gnf|dds)$",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);
    private static readonly Regex CenterLine = new(
        @"^nhl/(?:data/kettle/rendering/stadiums/common/ice_cline_(?<line>\d+)_cm|ui/kettle_assets/creationzone(?:_xl)?/arena/centericeline/(?<thumb>\d+))\.(?:gnf|dds)$",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    private readonly Dictionary<string, List<TeamArt>> _artByOwner = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TeamInfo>> _teamsByOwner = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _ownerByPath = new(StringComparer.Ordinal);

    private TeamCatalog(IReadOnlyList<TeamInfo> teams)
    {
        Teams = teams;
        foreach (var team in teams)
            foreach (var owner in OwnersOf(team))
            {
                if (!_teamsByOwner.TryGetValue(owner, out var list))
                    _teamsByOwner[owner] = list = [];
                list.Add(team);
            }
    }

    public IReadOnlyList<TeamInfo> Teams { get; }

    /// <summary>League names in a sensible order: the NHL first, then the rest by name.</summary>
    public IReadOnlyList<string> Leagues => Teams.Select(t => t.League).Distinct(StringComparer.Ordinal)
        .OrderBy(l => l == "NHL" ? 0 : l == "Pro-Am" ? 2 : 1).ThenBy(l => l, StringComparer.Ordinal).ToList();

    public static TeamCatalog Load(Func<string, byte[]?> read, IEnumerable<string> paths)
    {
        var data = read(DatabasePath) ?? throw new InvalidDataException($"The team database is missing: {DatabasePath}");
        var database = TdbFile.Parse(data);
        if (read(MetaPath) is { } meta)
            database.ApplyMeta(Encoding.UTF8.GetString(meta));
        var list = paths as IReadOnlyCollection<string> ?? paths.ToList();
        return Build(database, list);
    }

    public IReadOnlyList<TeamArt> ArtOf(TeamInfo team) =>
        OwnersOf(team).SelectMany(o => _artByOwner.GetValueOrDefault(o) ?? []).Distinct()
            .OrderBy(a => a.Category).ThenBy(a => a.Order).ThenBy(a => a.Section, StringComparer.Ordinal).ThenBy(a => a.Path, StringComparer.Ordinal).ToList();

    /// <summary>Every team that shows this file. More than one means an edit changes them all.</summary>
    public IReadOnlyList<TeamInfo> TeamsUsing(string path) =>
        _ownerByPath.TryGetValue(path, out var owner) ? _teamsByOwner.GetValueOrDefault(owner) ?? [] : [];

    /// <summary>The picture that stands for the team: its large logo, or the nearest it has.</summary>
    public string? MainLogo(TeamInfo team)
    {
        foreach (var folder in (string[])["large", "medium", "mediumglossy", "smallglossy", "extrasmall"])
        {
            var path = $"nhl/ui/kettle_assets/teamlogos/{folder}/t{team.ArtId}.gnf";
            if (_ownerByPath.ContainsKey(path))
                return path;
        }
        return null;
    }

    public TeamInfo? Find(string text)
    {
        var wanted = Normalize(text);
        return Teams.FirstOrDefault(t => Normalize(t.Name) == wanted)
            ?? Teams.FirstOrDefault(t => t.LeagueId == 0 && t.Abbreviation.Equals(text, StringComparison.OrdinalIgnoreCase))
            ?? Teams.FirstOrDefault(t => t.Abbreviation.Equals(text, StringComparison.OrdinalIgnoreCase))
            ?? Teams.FirstOrDefault(t => Normalize(t.Name).Contains(wanted, StringComparison.Ordinal));
    }

    private static IEnumerable<string> OwnersOf(TeamInfo team)
    {
        yield return "art:" + team.ArtId;
        yield return "line:" + team.CenterLine;
        if (team.Slug != null)
            yield return "slug:" + team.Slug;
    }

    private static TeamCatalog Build(TdbFile database, IReadOnlyCollection<string> paths)
    {
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var match = FontSlug.Match(Stem(path));
            if (match.Success)
                slugs.Add(match.Groups[1].Value);
        }

        var catalog = new TeamCatalog(ReadTeams(database, slugs));
        var longest = slugs.Count == 0 ? 0 : slugs.Max(s => s.Count(c => c == '_') + 1);
        foreach (var path in paths)
            if (Classify(path, slugs, longest) is var (owner, art) && catalog._teamsByOwner.ContainsKey(owner))
            {
                if (!catalog._artByOwner.TryGetValue(owner, out var list))
                    catalog._artByOwner[owner] = list = [];
                list.Add(art);
                catalog._ownerByPath[path] = owner;
            }
        return catalog;
    }

    private static List<TeamInfo> ReadTeams(TdbFile database, HashSet<string> slugs)
    {
        var table = database.Tables.FirstOrDefault(t => t.Label == "exhibitionteams")
            ?? throw new InvalidDataException("The team database has no exhibitionteams table.");
        var fields = table.Fields.ToDictionary(f => f.Label, StringComparer.Ordinal);
        var arenas = ReadArenas(database);
        var byNormal = slugs.Where(s => !GenericSlugs.Contains(s)).GroupBy(s => Normalize(s)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        string Text(int row, string name) => fields.TryGetValue(name, out var f) ? Clean(table.Read(row, f)?.ToString()) : "";
        int Number(int row, string name) =>
            fields.TryGetValue(name, out var f) && long.TryParse(table.Read(row, f)?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? (int)value : 0;

        var teams = new List<TeamInfo>(table.Count);
        for (var row = 0; row < table.Count; row++)
        {
            var name = Text(row, "teamname");
            if (name.Length == 0)
                continue;
            var (city, abbr, league) = (Text(row, "cityname"), Text(row, "artabbr"), Number(row, "league"));
            arenas.TryGetValue(Number(row, "arenaid"), out var arena);
            teams.Add(new TeamInfo(Number(row, "index"), Number(row, "artid"), name, city, abbr, league,
                LeagueNames.GetValueOrDefault(league, $"League {league}"),
                league is ProAmLeague or EashlLeague or ThreesLeague ? null
                    : ClubSlugs.TryGetValue(Number(row, "artid"), out var club) && slugs.Contains(club) ? club
                    : MatchSlug(name, city, abbr, byNormal),
                Number(row, "centerline"), arena?.Name, arena?.City, arena?.Capacity ?? 0, arena?.Year ?? 0,
                [Number(row, "feprimarycolor"), Number(row, "fesecondarycolor"), Number(row, "fetertiarycolor")]));
        }

        // Alumni and similar sides reuse a parent team's art id, so they wear that team's named art too.
        var slugByArt = teams.Where(t => t.Slug != null).GroupBy(t => t.ArtId).ToDictionary(g => g.Key, g => g.First().Slug);
        for (var i = 0; i < teams.Count; i++)
            if (teams[i].Slug == null && teams[i].LeagueId != ProAmLeague && !SlugOverrides.ContainsKey(Normalize(teams[i].Name)) && slugByArt.TryGetValue(teams[i].ArtId, out var slug))
                teams[i] = teams[i] with { Slug = slug };
        return teams.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private sealed record Arena(string Name, string City, int Capacity, int Year);

    private static Dictionary<int, Arena> ReadArenas(TdbFile database)
    {
        var arenas = new Dictionary<int, Arena>();
        var table = database.Tables.FirstOrDefault(t => t.Label == "exhibitionarena");
        if (table == null)
            return arenas;
        var fields = table.Fields.ToDictionary(f => f.Label, StringComparer.Ordinal);
        string Read(int row, string name) => fields.TryGetValue(name, out var f) ? Clean(table.Read(row, f)?.ToString()) : "";
        for (var row = 0; row < table.Count; row++)
        {
            if (!int.TryParse(Read(row, "index"), out var index))
                continue;
            _ = int.TryParse(Read(row, "maxattendance"), out var capacity);
            _ = int.TryParse(Read(row, "yearopened"), out var year);
            arenas[index] = new Arena(Read(row, "arenaname"), Read(row, "cityname"), capacity, year);
        }
        return arenas;
    }

    private static string? MatchSlug(string name, string city, string abbreviation, Dictionary<string, string> byNormal)
    {
        var (normal, normalCity, abbr) = (Normalize(name), Normalize(city), abbreviation.ToLowerInvariant());
        if (SlugOverrides.TryGetValue(normal, out var forced))
            return forced;
        if (byNormal.TryGetValue(normal, out var exact))
            return exact;
        var prefix = byNormal.Where(p => p.Key.Length >= 4 && normal.StartsWith(p.Key, StringComparison.Ordinal)).OrderByDescending(p => p.Key.Length).FirstOrDefault().Value;
        if (prefix != null)
            return prefix;
        if (abbr is "nyi" or "nyr" && byNormal.TryGetValue(abbr, out var byAbbr))
            return byAbbr;
        if (normalCity.Length >= 4)
        {
            var byCity = byNormal.Where(p => p.Key.StartsWith(normalCity, StringComparison.Ordinal)).OrderBy(p => p.Key.Length).FirstOrDefault().Value;
            if (byCity != null)
                return byCity;
        }
        var best = byNormal.Select(p => (p.Value, Score: Similarity(normal, p.Key))).OrderByDescending(p => p.Score).FirstOrDefault();
        return best.Score >= 0.72 ? best.Value : null;
    }

    /// <summary>Works out which team key a path belongs to and how to file it, or null when it has none.</summary>
    private static (string Owner, TeamArt Art)? Classify(string path, HashSet<string> slugs, int longest)
    {
        var category = AssetCategories.Of(path);
        if (category is not (AssetCategory.Texture or AssetCategory.Model))
            return null;

        if (category == AssetCategory.Texture)
        {
            var logo = TeamLogo.Match(path);
            if (logo.Success)
            {
                var folder = logo.Groups[1].Value;
                var kind = LogoFolders.FirstOrDefault(f => f.Folder == folder);
                return kind.Section == null ? null : ("art:" + logo.Groups[2].Value, new TeamArt(path, TeamArtCategory.Logos, kind.Section, false, kind.Order));
            }
            var numbered = NumberedArt.Match(path);
            if (numbered.Success)
                return ("art:" + numbered.Groups["art"].Value, NumberedAsset(path, numbered));
            var line = CenterLine.Match(path);
            if (line.Success)
                return line.Groups["line"].Success
                    ? ("line:" + line.Groups["line"].Value, new TeamArt(path, TeamArtCategory.CenterIce, "Center line", false, 2))
                    : ("line:" + line.Groups["thumb"].Value, new TeamArt(path, TeamArtCategory.CenterIce,
                        path.Contains("/creationzone_xl/", StringComparison.Ordinal) ? "Center line menu picture, large" : "Center line menu picture", false, 3));
        }

        var stadium = StadiumFolderOf(path);
        if (stadium == "common" || stadium == null)
        {
            var slug = SlugIn(Stem(path), slugs, longest);
            return slug == null ? null : ("slug:" + slug, FileNamedArt(path, category));
        }

        var isCustom = stadium.StartsWith("custom_", StringComparison.Ordinal);
        var plain = isCustom ? stadium["custom_".Length..] : stadium;
        var owner = StadiumFolders.GetValueOrDefault(plain, plain);
        if (!slugs.Contains(owner))
            return null;
        var section = isCustom ? "Custom arena" : "Stock arena";
        return ("slug:" + owner, category == AssetCategory.Model
            ? new TeamArt(path, TeamArtCategory.Models, section, false)
            : new TeamArt(path, TeamArtCategory.ArenaBuilding, section, IsTechnicalMap(Stem(path))));
    }

    private static TeamArt NumberedAsset(string path, Match match)
    {
        if (match.Groups["ring"].Success)
            return new TeamArt(path, TeamArtCategory.ArenaArt, "Power ring", false);
        if (match.Groups["ice"].Success)
            return match.Groups["ice"].Value == "ice_centerlogo"
                ? new TeamArt(path, TeamArtCategory.CenterIce, "Center ice logo", false, 0)
                : new TeamArt(path, TeamArtCategory.CenterIce, "Whole rink", false, 1);
        if (match.Groups["jersey"].Success)
            return new TeamArt(path, TeamArtCategory.Uniforms, "Menu jersey pictures", false, 90);

        var zone = match.Groups["zone"].Value;
        if (zone.StartsWith("creationzone", StringComparison.Ordinal))
            return new TeamArt(path, TeamArtCategory.Logos, zone.StartsWith("creationzone_xl", StringComparison.Ordinal) ? "Creation zone, large" : "Creation zone", false, 10);
        if (zone.EndsWith("collectionfolderlogos", StringComparison.Ordinal))
            return new TeamArt(path, TeamArtCategory.Logos, "HUT collection folder", false, 30);
        if (zone.Contains("themedcards", StringComparison.Ordinal))
            return new TeamArt(path, TeamArtCategory.Logos, "HUT themed cards", false, 31);
        var section = zone switch
        {
            "bgs/teambackground" => "Team background",
            "widgets/mainmenu/seasontile" => "Season tile",
            "widgets/mainmenu/eashltile" => "EASHL tile",
            _ => "HUT collection thumbnail",
        };
        return new TeamArt(path, TeamArtCategory.MenuArt, section, false);
    }

    private static TeamArt FileNamedArt(string path, AssetCategory category)
    {
        var stem = Stem(path);
        var folder = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        var technical = IsTechnicalMap(stem);
        if (category == AssetCategory.Model)
            return new TeamArt(path, TeamArtCategory.Models, Title(folder[(folder.LastIndexOf('/') + 1)..]), false);

        if (folder.EndsWith("/customlogos", StringComparison.Ordinal))
            return new TeamArt(path, TeamArtCategory.Logos, "Custom logo layers", technical, 40);
        if (folder.EndsWith("/characters/player", StringComparison.Ordinal))
        {
            if (stem.StartsWith("helmetlogo_", StringComparison.Ordinal))
                return new TeamArt(path, TeamArtCategory.Gear, "Helmet logos", technical, 0);
            if (stem.StartsWith("goaliehelmet_", StringComparison.Ordinal))
                return new TeamArt(path, TeamArtCategory.Gear, "Goalie helmets", technical, 1);
            if (stem.StartsWith("outdoor", StringComparison.Ordinal))
                return new TeamArt(path, TeamArtCategory.Gear, "Outdoor gear", technical, 2);
            if (stem.StartsWith("jersey_", StringComparison.Ordinal) || stem.StartsWith("font_", StringComparison.Ordinal)
                || stem.StartsWith("name_", StringComparison.Ordinal) || stem.StartsWith("pant_", StringComparison.Ordinal)
                || stem.StartsWith("sock_", StringComparison.Ordinal))
                return new TeamArt(path, TeamArtCategory.Uniforms, KitOf(stem), technical);
            return new TeamArt(path, TeamArtCategory.Gear, "Other gear", technical, 9);
        }
        if (folder.EndsWith("/characters/mascot", StringComparison.Ordinal))
            return new TeamArt(path, TeamArtCategory.MascotStaff, "Mascot", technical, 0);
        if (folder.EndsWith("/characters/trainer", StringComparison.Ordinal))
            return new TeamArt(path, TeamArtCategory.MascotStaff, "Trainer", technical, 1);
        if (folder.EndsWith("/characters/crowd", StringComparison.Ordinal))
            return new TeamArt(path, TeamArtCategory.Crowd, CrowdSection(stem), technical);
        if (folder.EndsWith("/characters/common", StringComparison.Ordinal))
            return new TeamArt(path, TeamArtCategory.ArenaArt, "Reflections", true, 90);
        if (folder.EndsWith("/stadiums/common", StringComparison.Ordinal))
        {
            if (stem.StartsWith("puck_", StringComparison.Ordinal) || stem.StartsWith("promopuck_", StringComparison.Ordinal))
                return new TeamArt(path, TeamArtCategory.Pucks, stem.StartsWith("promo", StringComparison.Ordinal) ? "Promo puck" : "Game puck", technical);
            if (stem.StartsWith("lm_", StringComparison.Ordinal))
                return new TeamArt(path, TeamArtCategory.ArenaBuilding, "Custom arena lighting", technical);
            return new TeamArt(path, TeamArtCategory.ArenaArt, ArenaSection(stem), technical);
        }
        return new TeamArt(path, TeamArtCategory.Other, Title(folder[(folder.LastIndexOf('/') + 1)..]), technical);
    }

    private static string KitOf(string stem)
    {
        var tokens = stem.Split('_');
        var brand = tokens.Contains("adidas") ? "Adidas" : tokens.Contains("rbk") ? "Reebok" : tokens.Contains("nineties") ? "Nineties" : null;
        var kit = tokens.Contains("home") ? "Home" : tokens.Contains("away") ? "Away" : tokens.Contains("two") ? "Alternate 1" : tokens.Contains("three") ? "Alternate 2" : null;
        return (brand, kit) switch
        {
            (null, null) => "Shared",
            (null, _) => kit!,
            (_, null) => brand,
            _ => $"{brand}, {kit}",
        };
    }

    private static string CrowdSection(string stem)
    {
        if (stem.StartsWith("sign_", StringComparison.Ordinal))
            return "Signs";
        if (stem.StartsWith("prop_", StringComparison.Ordinal) || stem.StartsWith("scarf", StringComparison.Ordinal))
            return "Props";
        if (stem.StartsWith("crowdchair", StringComparison.Ordinal))
            return "Seats";
        return "Fans";
    }

    private static string ArenaSection(string stem)
    {
        if (stem.StartsWith("banner_", StringComparison.Ordinal))
            return "Banners";
        if (stem.StartsWith("powerring", StringComparison.Ordinal))
            return "Power ring";
        if (stem.StartsWith("scoreboard", StringComparison.Ordinal))
            return "Scoreboard";
        if (stem.StartsWith("crowd_flag", StringComparison.Ordinal))
            return "Flags";
        if (stem.StartsWith("plastic_blimp", StringComparison.Ordinal))
            return "Blimp";
        if (stem.StartsWith("boards_", StringComparison.Ordinal))
            return "Boards and glass";
        if (stem.StartsWith("tunnel", StringComparison.Ordinal))
            return "Tunnel";
        return "Other arena art";
    }

    private static string? StadiumFolderOf(string path)
    {
        const string Marker = "/rendering/stadiums/";
        var at = path.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0)
            return null;
        var start = at + Marker.Length;
        var end = path.IndexOf('/', start);
        return end < 0 ? null : path[start..end];
    }

    /// <summary>The longest run of whole words in a file name that is a team name, so calgary_hitmen never reads as calgary.</summary>
    private static string? SlugIn(string stem, HashSet<string> slugs, int longest)
    {
        var tokens = stem.Split('_');
        for (var length = Math.Min(longest, tokens.Length); length > 0; length--)
            for (var start = 0; start + length <= tokens.Length; start++)
            {
                var candidate = string.Join('_', tokens, start, length);
                if (slugs.Contains(candidate))
                    return GenericSlugs.Contains(candidate) ? null : candidate;
            }
        return null;
    }

    private static bool IsTechnicalMap(string stem)
    {
        var tokens = stem.Split('_');
        for (var i = Math.Max(0, tokens.Length - 2); i < tokens.Length; i++)
            if (tokens[i] is "nm" or "norm" or "normal" or "sm" or "coeff" or "spec" or "am" or "ab")
                return true;
        return stem.StartsWith("cubemap", StringComparison.Ordinal);
    }

    private static string Stem(string path)
    {
        var start = path.LastIndexOf('/') + 1;
        var dot = path.LastIndexOf('.');
        return dot > start ? path[start..dot] : path[start..];
    }

    private static string Title(string text) => text.Length == 0 ? "Other" : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Clean(string? text) =>
        (text ?? "").Replace("®", "", StringComparison.Ordinal).Replace("™", "", StringComparison.Ordinal).Replace("\0", "", StringComparison.Ordinal).Trim();

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKD))
            if (c < 128 && char.IsAsciiLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        return builder.ToString();
    }

    /// <summary>Share of two strings covered by their common runs, the longest run first.</summary>
    private static double Similarity(string a, string b)
    {
        static int Common(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
        {
            var (best, atX, atY) = (0, 0, 0);
            for (var i = 0; i < x.Length; i++)
                for (var j = 0; j < y.Length; j++)
                {
                    var k = 0;
                    while (i + k < x.Length && j + k < y.Length && x[i + k] == y[j + k])
                        k++;
                    if (k > best)
                        (best, atX, atY) = (k, i, j);
                }
            return best == 0 ? 0 : best + Common(x[..atX], y[..atY]) + Common(x[(atX + best)..], y[(atY + best)..]);
        }
        return a.Length + b.Length == 0 ? 0 : 2.0 * Common(a, b) / (a.Length + b.Length);
    }
}
