using System.Text;
using System.Text.RegularExpressions;
using Blueline.Core.Teams;
using Blueline.Formats.Tdb;

namespace Blueline.Core.Franchise;

/// <summary>
/// Real-world data for Franchise mode, applied to a project's game database and text:
/// <list type="bullet">
/// <item>2025-26 NHL and AHL arena names, hockey capacities and opening years (<c>exhibitionarena</c>, through each club's
/// <c>arenaid</c>). Source: Wikipedia's lists of NHL and AHL arenas, read 2026-11-18.</item>
/// <item>Real NHL insiders as the ten analyst authors of the news feed (<c>tweetsauthors</c>, which ships with
/// "analyst 1" to "analyst 10"), and "NHL Insider" for the generic analyst name in the text.</item>
/// <item>Clean-up of the news, interview and owner lines: double spaces, trailing spaces and a few typos.</item>
/// </list>
/// A Franchise copies the arenas when it is created; an existing one gets them through <see cref="FranchiseSync"/>.
/// Whether the feed shows <c>tweetsauthors</c> names or the generic text name is not known yet; both are set.
/// </summary>
public static class FranchiseImmersion
{
    public const string TextPath = "nhl/data/kettle/loc/eng_us.db";

    /// <summary>
    /// Franchise's own seating table: one row per NHL club (TeamId is the exhibitionteams index, -1 a default) with seat
    /// shares and <c>MaxCapacity</c>. Seen in game 2026-11-18: the new-career screen shows this capacity, not
    /// exhibitionarena's (St. Louis 19,150 while the roster said 18,096).
    /// </summary>
    public const string ArenaCsvPath = "nhl/data/common/gamemodes/kernel_entity_csv/arena.csv";

    public sealed record Arena(string Team, string Name, int Capacity, int Opened);

    /// <summary>Matched against the start of each club's <c>teamname</c> in the project's database.</summary>
    public static readonly Arena[] Arenas =
    [
        new("Anaheim Ducks", "Honda Center", 17174, 1993),
        new("Winnipeg Jets", "Canada Life Centre", 15321, 2004),
        new("Boston Bruins", "TD Garden", 17565, 1995),
        new("Buffalo Sabres", "KeyBank Center", 19070, 1996),
        new("Calgary Flames", "Scotiabank Saddledome", 19289, 1983),
        new("Carolina Hurricanes", "Lenovo Center", 18547, 1999),
        new("Chicago Blackhawks", "United Center", 19717, 1994),
        new("Colorado Avalanche", "Ball Arena", 17809, 1999),
        new("Columbus Blue Jackets", "Nationwide Arena", 18144, 2000),
        new("Dallas Stars", "American Airlines Center", 18532, 2001),
        new("Detroit Red Wings", "Little Caesars Arena", 19515, 2017),
        new("Edmonton Oilers", "Rogers Place", 18347, 2016),
        new("Florida Panthers", "Amerant Bank Arena", 19250, 1998),
        new("Los Angeles Kings", "Crypto.com Arena", 18230, 1999),
        new("Minnesota Wild", "Grand Casino Arena", 17954, 2000),
        new("Montreal Canadiens", "Bell Centre", 20962, 1996),
        new("Nashville Predators", "Bridgestone Arena", 17159, 1996),
        new("New Jersey Devils", "Prudential Center", 16514, 2007),
        new("New York Islanders", "UBS Arena", 17255, 2021),
        new("New York Rangers", "Madison Square Garden", 18006, 1968),
        new("Ottawa Senators", "Canadian Tire Centre", 19347, 1996),
        new("Philadelphia Flyers", "Xfinity Mobile Arena", 19538, 1996),
        new("Utah Mammoth", "Delta Center", 16020, 1991),
        new("Pittsburgh Penguins", "PPG Paints Arena", 18387, 2010),
        new("St. Louis Blues", "Enterprise Center", 18096, 1994),
        new("San Jose Sharks", "SAP Center", 17435, 1993),
        new("Tampa Bay Lightning", "Benchmark International Arena", 19092, 1996),
        new("Toronto Maple Leafs", "Scotiabank Arena", 18800, 1999),
        new("Vancouver Canucks", "Rogers Arena", 18910, 1995),
        new("Washington Capitals", "Capital One Arena", 18573, 1997),
        new("Vegas Golden Knights", "T-Mobile Arena", 17367, 2016),
        new("Seattle Kraken", "Climate Pledge Arena", 17151, 2021),

        new("Bakersfield Condors", "Dignity Health Arena", 8751, 1998),
        new("Belleville Senators", "CIBC Place", 4400, 1978),
        new("Abbotsford Canucks", "Rogers Forum", 7000, 2009),
        new("Hamilton Hammers", "TD Coliseum", 16386, 1985),
        new("Charlotte Checkers", "Bojangles Coliseum", 8600, 1955),
        new("Chicago Wolves", "Allstate Arena", 16692, 1980),
        new("Cleveland Monsters", "Rocket Arena", 18926, 1994),
        new("Colorado Eagles", "Blue Arena", 5829, 2003),
        new("Grand Rapids Griffins", "Van Andel Arena", 10834, 1996),
        new("Hartford Wolf Pack", "PeoplesBank Arena", 14750, 1975),
        new("Hershey Bears", "Giant Center", 10500, 2002),
        new("Iowa Wild", "Casey's Center", 15181, 2005),
        new("Laval Rocket", "Place Bell", 10000, 2017),
        new("Lehigh Valley Phantoms", "PPL Center", 8420, 2014),
        new("Manitoba Moose", "Canada Life Centre", 15321, 2004),
        new("Milwaukee Admirals", "UW-Milwaukee Panther Arena", 9652, 1950),
        new("Ontario Reign", "Toyota Arena", 9736, 2008),
        new("Providence Bruins", "Amica Mutual Pavilion", 11075, 1972),
        new("Rochester Americans", "Blue Cross Arena", 11215, 1955),
        new("Rockford IceHogs", "BMO Center", 5895, 1981),
        new("Henderson Silver Knights", "Lee's Family Forum", 6019, 2022),
        new("San Diego Gulls", "Pechanga Arena", 12920, 1966),
        new("San Jose Barracuda", "TechCU Arena", 4200, 2022),
        new("Springfield Thunderbirds", "MassMutual Center", 6793, 1972),
        new("Calgary Wranglers", "Scotiabank Saddledome", 19289, 1983),
        new("Syracuse Crunch", "Upstate Medical University Arena", 5800, 1951),
        new("Texas Stars", "H-E-B Center at Cedar Park", 8000, 2009),
        new("Toronto Marlies", "Coca-Cola Coliseum", 7851, 1921),
        new("Tucson Roadrunners", "Tucson Arena", 8962, 1971),
        new("Utica Comets", "NBT Bank Arena", 3860, 1960),
        new("Wilkes-Barre/Scranton Penguins", "Mohegan Arena at Casey Plaza", 8300, 1999),
    ];

    /// <summary>The ten analyst rows of <c>tweetsauthors</c> (author ids 1 to 10). Both fields hold 14 bytes.</summary>
    public static readonly (string Name, string Handle)[] Insiders =
    [
        ("E. Friedman", "FriedgeHNIC"),
        ("P. LeBrun", "PierreVLeBrun"),
        ("D. Dreger", "DarrenDreger"),
        ("C. Johnston", "reporterchris"),
        ("B. McKenzie", "TSNBobMcKenzie"),
        ("K. Weekes", "KevinWeekes"),
        ("E. Kaplan", "emilymkaplan"),
        ("G. Wyshynski", "wyshynski"),
        ("M. Russo", "RussoHockey"),
        ("NHL PR", "PR_NHL"),
    ];

    public const string AnalystNameKey = "TWITTER_ANALYTIC_AUTHOR_NAME";
    public const string AnalystName = "NHL Insider";

    /// <summary>
    /// Whole-line rewrites of the weakest news lines (caps, "!!!", repeated words), in a newsroom voice. Each keeps exactly
    /// the placeholders of the line it replaces; one that does not is skipped. The game picks lines by key and its line
    /// table is full (968 of 968 rows), so lines can be rewritten but not added.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Rewrites = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["TXT_TWEET_NEWS_PLAYER_SENT_DOWN_9"] = "[<%1 pn>] has been reassigned to the AHL. #Assigned",
        ["TXT_TWEET_NEWS_PLAYER_SENT_DOWN_7"] = "The #[<%3 tnn>] reassign [<%1 pn>] to their AHL affiliate, the [<%2 tnn>].",
        ["TXT_TWEET_NEWS_TRADE_1"] = "Trade: [<%1 pl>] heads to the [<%3 tnn>]. #[<%2 tnn>]",
        ["TXT_TWEET_NEWS_OFFER_SHEET_MATCH_13"] = "[<%1 tf>] match the offer sheet: [<%3 pn>] stays with the [<%1 tnn>].",
        ["TXT_TWEET_RECAP_SHOOTOUT_GAME_16"] = "Shootout winner! [<%2 tc>] gets past [<%3 tc>] [<%4 n>]-[<%5 n>] and the [<%2 tnn>] take the extra point.",
        ["TXT_TWEET_RECAP_SHOOTOUT_GAME_9"] = "[<%1 pn>] scores the winner as the [<%2 tnn>] edge the [<%3 tnn>] in a shootout, [<%4 n>]-[<%5 n>].",
        ["TXT_TWEET_RECAP_SHOOTOUT_GAME_8"] = "That's the winner. [<%2 tc>] beats [<%3 tc>] in a shootout.",
        ["TXT_TWEET_RECAP_CLINCH_DIVISION_16"] = "[<%1 tc>] have locked up the division title. #DivisionChamps",
        ["TXT_TWEET_RECAP_WON_STANLEY_CUP_2"] = "That's the final horn. The [<%1 tnn>] are your Stanley Cup champions.",
        ["TXT_TWEET_RECAP_WON_STANLEY_CUP_3"] = "The [<%1 tnn>] have done it. They are your Stanley Cup champions.",
        ["TXT_TWEET_RECAP_WON_STANLEY_CUP_4"] = "The [<%1 tnn>] are Stanley Cup champions. Hoist it high. #StanleyCup",
        ["TXT_TWEET_RECAP_WON_STANLEY_CUP_7"] = "The [<%1 tnn>] win the Cup! They get past the [<%2 tnn>] and Lord Stanley is theirs.",
        ["TXT_TWEET_RECAP_WON_STANLEY_CUP_9"] = "In case you missed it: [<%1 tf>] have won the Stanley Cup.",
        ["TXT_TWEET_RECAP_BIG_UPSET_4"] = "Upset alert: the [<%1 tnn>] pull off a [<%3 n>]-[<%4 n>] win nobody saw coming.",
        ["TXT_TWEET_RECAP_NOTABLE_PERFORMANCE_100_POINTS_7"] = "[<%1 pn>] puts up [<%2 n>] points tonight and reaches 100 on the season. Elite company.",
        ["TXT_TWEET_RECAP_NOTABLE_PERFORMANCE_SHUTOUT_6"] = "[<%1 pn>] slams the door on the [<%3 tnn>] for the shutout.",
        ["TXT_TWEET_RECAP_NOTABLE_PERFORMANCE_50_GOALS_7"] = "50 goals for #[<%1 pl>]. Pure finish.",
        ["TXT_TWEET_RECAP_STREAKING_TEAMS_11"] = "[<%1 tc>] wins again. That's [<%3 n>] in a row and counting.",
        ["TXT_TWEET_PREVIEW_CHANCE_TO_CLINCH_PLAYOFFS_16"] = "One more win and the #[<%1 tnn>] are in. #StanleyCupPlayoffs",
        ["TXT_TWEET_PREVIEW_CHANCE_TO_CLINCH_PLAYOFFS_20"] = "One win decides whether the [<%1 tnn>] get a shot at Lord Stanley. No bigger game this season.",
        ["TXT_TWEET_RECAP_CHANCE_TO_CONFERENCE_11"] = "First place in the conference is wrapped up for [<%1 tf>].",
        ["TXT_TWEET_PREVIEW_CHANCE_TO_CONFERENCE_12"] = "Win and they're in. #[<%1 tnn>]",
        ["TXT_TWEET_RECAP_STILL_WINLESS_12"] = "The slump goes on for [<%1 tf>]: [<%2 n>] straight games without a win.",
        ["TXT_TWEET_PREVIEW_INJURY_RETURN_16"] = "He's back. #[<%1 pl>] returns to the lineup tonight. #[<%2 tnn>]",
        ["TXT_TWEET_PREVIEW_INJURY_RETURN_14"] = "Welcome back, [<%1 pn>]. [<%1 pl>] is in tonight's lineup. #[<%2 tnn>]",
        ["TXT_TWEET_RECAP_BIG_UPSET_PLAYOFF_ADVANCEMENT_8"] = "The [<%1 tnn>] are moving on. Few saw it coming, but the [<%2 tnn>] are out after dropping the series [<%3 n>]-[<%4 n>].",
        ["TXT_TWEET_RECAP_BIG_UPSET_PLAYOFF_ADVANCEMENT_3"] = "Stunner: the [<%2 tnn>] have been knocked out of the playoffs by the lower-seeded [<%1 tnn>].",
        ["TXT_TWEET_RECAP_COMEBACK_WIN_15"] = "Comeback complete. [<%2 tf>] lets the lead slip and [<%1 tf>] takes it. #[<%1 tnn>]",
        ["TXT_TWEET_RECAP_COMEBACK_WIN_13"] = "Rally caps on. The [<%1 tnn>] battle back to beat the [<%2 tnn>].",
        ["TXT_TWEET_RECAP_CLINCH_PLAYOFF_BERTH_12"] = "[<%1 tc>] fans can exhale: their team has clinched a playoff berth.",
        ["TXT_TWEET_RECAP_CLINCH_PLAYOFF_BERTH_10"] = "[<%1 tc>] are in the playoffs. Anything can happen now.",
        ["TXT_TWEET_RECAP_CLINCH_PLAYOFF_BERTH_7"] = "A sigh of relief in [<%1 tc>]: they're into the Stanley Cup playoffs.",
        ["TXT_TWEET_RECAP_CLINCH_PLAYOFF_BERTH_3"] = "It's official: the [<%1 tnn>] have clinched a spot in the Stanley Cup playoffs. #Playoffs",
        ["TXT_TWEET_RECAP_STANLEY_CUP_FINALS_6"] = "The [<%1 tnn>] beat the [<%2 tnn>] [<%6 n>]-[<%7 n>] in Game [<%5 n>] of the Final. #StanleyCup",
        ["TXT_TWEET_RECAP_GAME_RESULTS_17"] = "The [<%1 tnn>] take care of business, [<%3 n>]-[<%4 n>] over [<%2 tf>].",
        ["TXT_TWEET_RECAP_PLAYER_RETURNED_FROM_INJURY_8"] = "What injury? [<%1 pn>] puts up [<%4 n>] points in his return. #[<%2 ta>]vs[<%3 ta>] #[<%1 pl>]",
        ["TXT_TWEET_RECAP_OVERTIME_GAME_12"] = "#[<%5 pl>] ends it in overtime. The [<%1 tnn>] win.",
        ["TXT_TWEET_RECAP_OVERTIME_GAME_17"] = "[<%5 pn>] wins it in overtime. Final: [<%1 tnn>] [<%3 n>], [<%2 tc>] [<%4 n>].",
        ["TXT_TWEET_PREVIEW_CHANCE_TO_CLINCH_DIVISION_13"] = "The city is buzzing: one win and the [<%1 tnn>] are division champions. #DivisionChamps",
        ["TXT_TWEET_PREVIEW_CONFERENCE_GAME_7"] = "Game [<%3 n>] of the [<%4 cnf>] Final: the [<%1 tnn>] and the [<%2 tnn>]. Can't wait. #LordStanley",
    };

    private static readonly Regex Placeholder = new(@"\[<%\d+ \w+>\]");

    /// <summary>The distinct placeholders of a line, sorted, to check a rewrite keeps them.</summary>
    public static string PlaceholderSet(string text) =>
        string.Join(" ", Placeholder.Matches(text).Select(m => m.Value).Distinct().OrderBy(v => v, StringComparer.Ordinal));

    /// <summary>Small fixes applied to every news, interview and owner line.</summary>
    private static readonly (Regex Pattern, string Replacement)[] Fixes =
    [
        (new Regex(@"(?<=\S) {2,}(?=\S)"), " "),
        (new Regex(@"\bapart of\b"), "a part of"),
        (new Regex(@"has a received"), "has received"),
        (new Regex(@"\]a Contract offer"), "] a contract offer"),
        (new Regex(@"coming back to soon"), "coming back too soon"),
        (new Regex(@"when its ready"), "when it's ready"),
        (new Regex(@"the leagues best"), "the league's best"),
        (new Regex(@" +$"), ""),
    ];

    private static readonly string[] TextPrefixes = ["TXT_TWEET_", "TXT_INT_", "TXT_OWNER_", "TXT_TEAM_OWNER_MSG_"];

    public sealed record Result(int ArenaCells, IReadOnlyList<string> ArenasNotFound, int AuthorCells, int TextLines, int CapacityRows, IReadOnlyList<string> Log);

    /// <summary>Works out every change; with <paramref name="write"/> it applies them to the project as one undo step (which saves it).</summary>
    public static Result Apply(ModProject project, Workspace workspace, bool write)
    {
        var meta = Encoding.UTF8.GetString(workspace.ReadCurrent(TeamCatalog.MetaPath) ?? throw new InvalidDataException("The game has no database meta."));
        var db = TdbFile.Parse(workspace.ReadCurrent(TeamCatalog.DatabasePath) ?? throw new InvalidDataException("The game has no database."));
        db.ApplyMeta(meta);
        var log = new List<string>();
        var (arenaCells, notFound) = ApplyArenas(db, log);
        var authorCells = ApplyInsiders(db, log);
        db.RepairCrcs();

        var csv = workspace.ReadCurrent(ArenaCsvPath) ?? throw new InvalidDataException("The game has no Franchise arena table.");
        var (newCsv, capacityRows) = ApplyCapacities(csv, db, log);

        var text = new LocDictionary(workspace.ReadCurrent(TextPath) ?? throw new InvalidDataException("The game has no English text."));
        var lines = ApplyText(text, log);

        var items = new List<(string, byte[], string)>();
        if (arenaCells + authorCells > 0)
            items.Add((TeamCatalog.DatabasePath, db.Data, "Franchise: 2025-26 arenas and NHL insiders"));
        if (capacityRows > 0)
            items.Add((ArenaCsvPath, newCsv, "Franchise: arena capacities"));
        if (lines > 0)
            items.Add((TextPath, text.Save(), "Franchise: news and interview text"));
        if (write && items.Count > 0)
            project.SetMany(items, "Franchise immersion pack");
        return new Result(arenaCells, notFound, authorCells, lines, capacityRows, log);
    }

    public static (int Cells, IReadOnlyList<string> NotFound) ApplyArenas(TdbFile db, List<string>? log = null)
    {
        var teams = Table(db, "exhibitionteams");
        var arenas = Table(db, "exhibitionarena");
        var teamName = Field(teams, "teamname");
        var teamLeague = Field(teams, "league");
        var teamArena = Field(teams, "arenaid");
        var arenaIndex = Field(arenas, "index");
        var arenaName = Field(arenas, "arenaname");
        var capacity = Field(arenas, "maxattendance");
        var opened = Field(arenas, "yearopened");

        var arenaRows = new Dictionary<long, int>();
        for (var r = 0; r < arenas.Count; r++)
            arenaRows.TryAdd(Convert.ToInt64(arenas.Read(r, arenaIndex)), r);

        var cells = 0;
        var notFound = new List<string>();
        foreach (var arena in Arenas)
        {
            // The first NHL or AHL club whose name starts with the team (alumni and circuit teams come later or are other leagues).
            var row = Enumerable.Range(0, teams.Count).FirstOrDefault(r =>
                Convert.ToInt64(teams.Read(r, teamLeague)) is 0 or 1
                && Plain((string)teams.Read(r, teamName)).StartsWith(arena.Team, StringComparison.OrdinalIgnoreCase), -1);
            if (row < 0 || !arenaRows.TryGetValue(Convert.ToInt64(teams.Read(row, teamArena)), out var a))
            {
                notFound.Add(arena.Team);
                continue;
            }
            log?.Add($"{arena.Team}: \"{Plain((string)arenas.Read(a, arenaName))}\" {arenas.Read(a, capacity)} {arenas.Read(a, opened)} -> \"{arena.Name}\" {arena.Capacity} {arena.Opened}");
            if (Plain((string)arenas.Read(a, arenaName)) != arena.Name)
            {
                arenas.Write(a, arenaName, arena.Name);
                cells++;
            }
            if (Convert.ToInt64(arenas.Read(a, capacity)) != arena.Capacity)
            {
                arenas.Write(a, capacity, arena.Capacity);
                cells++;
            }
            if (Convert.ToInt64(arenas.Read(a, opened)) != arena.Opened)
            {
                arenas.Write(a, opened, arena.Opened);
                cells++;
            }
        }
        return (cells, notFound);
    }

    /// <summary>Sets each club's MaxCapacity in Franchise's arena table to its exhibitionarena capacity. Other columns stay.</summary>
    public static (byte[] Csv, int Rows) ApplyCapacities(byte[] csv, TdbFile db, List<string>? log = null)
    {
        var teams = Table(db, "exhibitionteams");
        var arenas = Table(db, "exhibitionarena");
        var arenaRows = new Dictionary<long, int>();
        for (var r = 0; r < arenas.Count; r++)
            arenaRows.TryAdd(Convert.ToInt64(arenas.Read(r, Field(arenas, "index"))), r);
        var capacityOf = new Dictionary<long, long>();
        for (var r = 0; r < teams.Count; r++)
            if (arenaRows.TryGetValue(Convert.ToInt64(teams.Read(r, Field(teams, "arenaid"))), out var a))
                capacityOf.TryAdd(Convert.ToInt64(teams.Read(r, Field(teams, "index"))), Convert.ToInt64(arenas.Read(a, Field(arenas, "maxattendance"))));

        var text = Encoding.UTF8.GetString(csv);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(newline);
        var header = lines[0].Split(',');
        var idAt = Array.IndexOf(header, "TeamId");
        var capAt = Array.IndexOf(header, "MaxCapacity");
        if (idAt < 0 || capAt < 0)
            throw new InvalidDataException("The Franchise arena table has no TeamId or MaxCapacity column.");
        var rows = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            var cells = lines[i].Split(',');
            if (cells.Length != header.Length || !long.TryParse(cells[idAt], out var id) || id < 0 || !capacityOf.TryGetValue(id, out var capacity))
                continue;
            if (cells[capAt] == capacity.ToString())
                continue;
            log?.Add($"arena.csv team {id}: {cells[capAt]} -> {capacity}");
            cells[capAt] = capacity.ToString();
            lines[i] = string.Join(',', cells);
            rows++;
        }
        return (Encoding.UTF8.GetBytes(string.Join(newline, lines)), rows);
    }

    public static int ApplyInsiders(TdbFile db, List<string>? log = null)
    {
        var authors = Table(db, "tweetsauthors");
        var id = Field(authors, "authorid");
        var group = Field(authors, "authorgroup");
        var name = Field(authors, "username");
        var handle = Field(authors, "userhandle");
        var cells = 0;
        for (var r = 0; r < authors.Count; r++)
        {
            var author = Convert.ToInt64(authors.Read(r, id));
            if (Convert.ToInt64(authors.Read(r, group)) != 1 || author < 1 || author > Insiders.Length)
                continue;
            var (n, h) = Insiders[author - 1];
            log?.Add($"author {author}: {authors.Read(r, name)} -> {n} @{h}");
            if ((string)authors.Read(r, name) != n)
            {
                authors.Write(r, name, n);
                cells++;
            }
            if ((string)authors.Read(r, handle) != h)
            {
                authors.Write(r, handle, h);
                cells++;
            }
        }
        return cells;
    }

    public static int ApplyText(LocDictionary text, List<string>? log = null)
    {
        var lines = 0;
        foreach (var row in text.Rows.ToList())
        {
            string? updated = null;
            if (row.Key == AnalystNameKey)
                updated = AnalystName;
            else if (Rewrites.TryGetValue(row.Key, out var rewrite))
            {
                if (row.Text != null && PlaceholderSet(rewrite) != PlaceholderSet(row.Text))
                {
                    log?.Add($"{row.Key}: rewrite skipped, its placeholders differ from the line's");
                    continue;
                }
                updated = rewrite;
            }
            else if (TextPrefixes.Any(p => row.Key.StartsWith(p, StringComparison.Ordinal)) && row.Text is { } original)
            {
                var fixedText = original;
                foreach (var (pattern, replacement) in Fixes)
                    fixedText = pattern.Replace(fixedText, replacement);
                updated = fixedText;
            }
            if (updated != null && updated != row.Text && text.FindUnencodable(updated) == null)
            {
                log?.Add($"{row.Key}: \"{row.Text}\" -> \"{updated}\"");
                text.SetText(row.Key, updated);
                lines++;
            }
        }
        return lines;
    }

    /// <summary>The name without the ® and ™ marks and the spaces around them.</summary>
    private static string Plain(string text) => new string(text.Where(c => c < 128).ToArray()).Trim();

    private static TdbTable Table(TdbFile file, string name) =>
        file.Tables.FirstOrDefault(t => t.Label == name) ?? throw new InvalidDataException($"The database has no {name} table.");

    private static TdbField Field(TdbTable table, string name) =>
        table.Fields.FirstOrDefault(f => f.Label == name) ?? throw new InvalidDataException($"{table.Label} has no {name} field.");
}
