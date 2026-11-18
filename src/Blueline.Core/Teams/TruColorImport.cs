using System.Globalization;
using System.Net;
using System.Text;

namespace Blueline.Core.Teams;

/// <summary>Which of a team's colour sets an import writes.</summary>
[Flags]
public enum TruColorTargets
{
    None = 0,
    /// <summary>feprimarycolor / fesecondarycolor: menus, and Studio's team swatches.</summary>
    Menu = 1,
    /// <summary>presprimarycolor / pressecondarycolor: broadcast presentation.</summary>
    Presentation = 2,
    /// <summary>overlayprimarycolor / overlaysecondarycolor: the score clock (seen in game for overlayprimarycolor).</summary>
    Overlay = 4,
    /// <summary>Main colour of the home kit's gloves and helmet rows (NHL clubs only). Gear follows the kit, so this is opt-in.</summary>
    Gear = 8,
    Default = Menu | Presentation | Overlay,
}

/// <summary>How the site's colours go into the game. OverlayShade scales the score clock colours (stock teams sit near 0.6).</summary>
public sealed record TruColorOptions(TruColorTargets Targets = TruColorTargets.Default, double OverlayShade = 1.0, int? Season = null);

/// <summary>Which of the era's colours are primary and secondary (indexes into its primary row), for teams whose order should differ.</summary>
public sealed record TruColorPick(int Primary = 0, int Secondary = 1);

public sealed record TruColorMatch(TeamInfo Team, TruColorTeam Site, string How);

public sealed record TruColorMatches(IReadOnlyList<TruColorMatch> Matched, IReadOnlyList<TeamInfo> UnmatchedGame, IReadOnlyList<TruColorTeam> UnmatchedSite);

/// <summary>One cell an import changes.</summary>
public sealed record TruColorChange(string Key, string Label, int Old, int New)
{
    public bool Changes => Old != New;
}

/// <summary>
/// What an import would write for one team. Primary and Secondary are the menu and presentation pair; the score clock pair is the
/// site's own order, which differs only for a black-first team with no pick of its own (the stock game puts the accent first in
/// its menus there but keeps black on the clock).
/// </summary>
public sealed record TruColorPlan(TeamInfo Team, TruColorTeam Site, TruColorEra? Era, TruColorSwatch? Primary, TruColorSwatch? Secondary,
    IReadOnlyList<TruColorChange> Changes, IReadOnlyList<string> Notes, TruColorSwatch? ClockPrimary = null, TruColorSwatch? ClockSecondary = null)
{
    public int ChangeCount => Changes.Count(c => c.Changes);

    /// <summary>The pick that gives this plan's menu pair, as indexes into the era's colours, for editing from there.</summary>
    public TruColorPick? CurrentPick => Era is { } era && Primary != null
        ? new TruColorPick(IndexOf(era, Primary), Secondary != null ? IndexOf(era, Secondary) : Math.Min(1, era.Colours.Count - 1))
        : null;

    private static int IndexOf(TruColorEra era, TruColorSwatch swatch) => era.Colours.Select((c, i) => (c, i)).First(p => ReferenceEquals(p.c, swatch)).i;
}

/// <summary>
/// Puts TruColor team colours into the game's team table. Game leagues are paired with the site's league pages; teams are matched
/// by full name (city and name), never by nickname alone, with aliases for names the two spell differently. The plan lists every
/// cell before and after so it can be previewed; applying writes through <see cref="TeamDetails"/> (and <see cref="GearColours"/>
/// for gear) into a working copy that the caller saves to a project. A roster save the player loads keeps its own copy of these
/// tables; the existing roster sync carries the edits there.
/// </summary>
public static class TruColorImport
{
    /// <summary>Game league ids by the start of the site page's slug.</summary>
    public static readonly IReadOnlyList<(string SlugStart, int[] GameLeagues)> LeaguePairs =
    [
        ("national-hockey-league-official-colors-franchise-records", [0, 21, 22]),
        ("american-hockey-league-official-colors", [1]),
        ("echl-official-colors", [15]),
        ("ontario-hockey-league-official-colors", [11]),
        ("quebec-maritimes-junior-hockey-league-official-colors", [12]),
        ("western-hockey-league-official-colors-1977", [13]),
    ];

    /// <summary>Game team names (normalised) whose site name is spelled differently.</summary>
    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["soogreyhounds"] = "SAULT STE. MARIE GREYHOUNDS",
        ["worcesterrailers"] = "WORCESTER RAILERS HC",
    };

    /// <summary>The game leagues a site league stands for (none when the game has no such league).</summary>
    public static IReadOnlyList<int> GameLeaguesOf(TruColorLeague league) =>
        league.Key is { } key ? LeaguePairs.FirstOrDefault(p => key.StartsWith(p.SlugStart, StringComparison.Ordinal)).GameLeagues ?? [] : [];

    /// <summary>The site league for a game league, from a list read off the index.</summary>
    public static TruColorLeague? SiteLeagueFor(int gameLeague, IEnumerable<TruColorLeague> leagues) =>
        leagues.FirstOrDefault(l => GameLeaguesOf(l).Contains(gameLeague));

    public static string Normalize(string text) => Rinks.FaceoffNet.Normalize(text);

    /// <summary>NHL Alumni sides wear their club's newest colours from the site, not their own era, so imports leave them out unless asked.</summary>
    public static bool IsAlumni(TeamInfo team) => team.LeagueId is 21 or 22;

    /// <summary>
    /// Pairs game teams with site teams. Tries, in order: an override (game name to site name), an alias, the full name, city plus
    /// name, and for alumni sides the name without "Alumni". Among site teams of the same name (Ottawa Senators three times) an active
    /// one wins, then the latest. Site teams left over are listed only when active, since most historical clubs have no game side.
    /// </summary>
    public static TruColorMatches Match(IEnumerable<TeamInfo> gameTeams, IReadOnlyList<TruColorTeam> siteTeams, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var byName = siteTeams.GroupBy(t => Normalize(t.Name)).ToDictionary(g => g.Key,
            g => g.OrderByDescending(t => t.Active).ThenByDescending(t => t.To ?? int.MaxValue).ThenByDescending(t => t.From ?? 0).First(), StringComparer.Ordinal);
        var overrideMap = (overrides ?? new Dictionary<string, string>()).ToDictionary(p => Normalize(p.Key), p => Normalize(p.Value), StringComparer.Ordinal);
        var matched = new List<TruColorMatch>();
        var missing = new List<TeamInfo>();
        foreach (var team in gameTeams)
        {
            var name = Normalize(team.Name);
            var city = Normalize(team.City);
            var tries = new List<(string? Key, string How)>
            {
                (overrideMap.GetValueOrDefault(name), "override"),
                (Aliases.TryGetValue(name, out var alias) ? Normalize(alias) : null, "alias"),
                (name, "name"),
                (name.StartsWith(city, StringComparison.Ordinal) ? null : city + name, "city and name"),
                (name.EndsWith("alumni", StringComparison.Ordinal) ? name[..^"alumni".Length] : null, "alumni of"),
            };
            var hit = tries.Where(t => t.Key != null).Select(t => (Site: byName.GetValueOrDefault(t.Key!), t.How)).FirstOrDefault(t => t.Site != null);
            if (hit.Site is { } site)
                matched.Add(new TruColorMatch(team, site, hit.How));
            else
                missing.Add(team);
        }
        var used = matched.Select(m => m.Site).ToHashSet();
        return new TruColorMatches(matched, missing, siteTeams.Where(t => t.Active && !used.Contains(t)).ToList());
    }

    /// <summary>The era an import uses: the one covering the season when one is asked for, else the newest.</summary>
    public static TruColorEra? EraOf(TruColorTeam site, int? season) => season is { } s ? site.EraFor(s) : site.Current;

    /// <summary>Works out what an import would change for one team. Nothing is written.</summary>
    public static TruColorPlan Plan(TeamDetails details, GearColours? gear, TeamInfo team, TruColorTeam site, TruColorOptions options, TruColorPick? pick = null)
    {
        var automatic = pick == null;
        pick ??= new TruColorPick();
        var notes = new List<string>();
        var era = EraOf(site, options.Season);
        if (era == null)
        {
            notes.Add(options.Season is { } s ? $"No franchise colours for {s}-{s + 1}." : "No franchise colours on the site.");
            return new TruColorPlan(team, site, null, null, null, [], notes);
        }
        var colours = era.Colours;
        var primary = colours.ElementAtOrDefault(pick.Primary);
        var secondary = colours.ElementAtOrDefault(pick.Secondary);
        if (primary == null)
        {
            notes.Add("The era lists no colours.");
            return new TruColorPlan(team, site, era, null, null, [], notes);
        }
        if (secondary == null)
            notes.Add("Only one colour is listed, so secondary is left as it is.");
        if (colours.Take(2).Any(c => c.Estimated))
            notes.Add("The site marks a colour as estimated.");

        // Black-first teams (Bruins, Penguins, most junior clubs in black): the stock menus and presentation lead with the
        // accent and keep black second, while the score clock stays black. Measured on every stock club, 2026-11-17.
        var (clockPrimary, clockSecondary) = (primary, secondary);
        if (automatic && IsBlack(primary.Rgb) && colours.Skip(1).FirstOrDefault(c => !IsBlack(c.Rgb) && !IsWhite(c.Rgb) && !c.IsSupporting) is { } accent)
        {
            (primary, secondary) = (accent, clockPrimary);
            notes.Add($"Black comes first on the site, so menus and presentation lead with {accent.Name} (as the stock game does); the score clock keeps black.");
        }

        var fields = details.FieldsOf(team.Index).Where(f => f.Table == TeamTable.Team && f.Kind == TeamFieldKind.Colour).ToDictionary(f => f.Field.Label, StringComparer.Ordinal);
        var changes = new List<TruColorChange>();
        void Add(string field, string label, int? rgb)
        {
            if (rgb is { } value && fields.TryGetValue(field, out var f))
                changes.Add(new TruColorChange("team." + field, label, Convert.ToInt32(f.Value), value));
        }
        if (options.Targets.HasFlag(TruColorTargets.Menu))
        {
            Add("feprimarycolor", "Menu primary", primary.Rgb);
            Add("fesecondarycolor", "Menu secondary", secondary?.Rgb);
        }
        if (options.Targets.HasFlag(TruColorTargets.Presentation))
        {
            Add("presprimarycolor", "Presentation primary", primary.Rgb);
            Add("pressecondarycolor", "Presentation secondary", secondary?.Rgb);
        }
        if (options.Targets.HasFlag(TruColorTargets.Overlay))
        {
            Add("overlayprimarycolor", "Score clock primary", Shade(clockPrimary.Rgb, options.OverlayShade));
            Add("overlaysecondarycolor", "Score clock secondary", clockSecondary == null ? null : Shade(clockSecondary.Rgb, options.OverlayShade));
        }
        if (options.Targets.HasFlag(TruColorTargets.Gear))
        {
            if (gear == null || GearColours.GearTeamOf(team) is not { } gearTeam)
                notes.Add("Gear is only linked for NHL clubs; left as it is.");
            else if (gear.KitsOf(team).FirstOrDefault(k => k.Variant == 0) is not { } home)
                notes.Add("No home kit found for the gear rows.");
            else
                foreach (var part in Enum.GetValues<GearPart>())
                {
                    var resolved = gear.Resolve(gearTeam, home, part);
                    if (resolved.Row is not { } row)
                        continue;
                    changes.Add(new TruColorChange($"gear.{row.Index}", $"Home {part.ToString().ToLowerInvariant()} (row {row.Index})", row.Main, clockPrimary.Rgb));
                    if (resolved.Link == GearLink.Fallback)
                        notes.Add($"Home {part.ToString().ToLowerInvariant()} read the team's fallback row {row.Index}, which other kits share.");
                }
        }
        return new TruColorPlan(team, site, era, primary, secondary, changes, notes, clockPrimary, clockSecondary);
    }

    /// <summary>Writes the plans' changes into the working copies. Returns the number of cells changed.</summary>
    public static int Apply(TeamDetails details, GearColours? gear, IEnumerable<TruColorPlan> plans)
    {
        var count = 0;
        foreach (var plan in plans)
        {
            var fields = details.FieldsOf(plan.Team.Index).ToDictionary(f => f.Key, StringComparer.Ordinal);
            foreach (var change in plan.Changes.Where(c => c.Changes))
            {
                if (change.Key.StartsWith("gear.", StringComparison.Ordinal))
                {
                    var index = int.Parse(change.Key[5..], CultureInfo.InvariantCulture);
                    if (gear == null || GearColours.GearTeamOf(plan.Team) is not { } gearTeam)
                        continue;
                    var row = gear.RowsOf(gearTeam).FirstOrDefault(r => r.Index == index) ?? throw new InvalidDataException($"No gear row {index} for {plan.Team.Name}.");
                    gear.SetMain(row, change.New);
                }
                else
                {
                    var field = fields.GetValueOrDefault(change.Key) ?? throw new InvalidDataException($"{plan.Team.Name} has no {change.Key}.");
                    details.Set(field, (long)change.New);
                }
                count++;
            }
        }
        return count;
    }

    /// <summary>Near black (every channel under 0x30): the site's "Black" is 010101.</summary>
    public static bool IsBlack(int rgb) => ((rgb >> 16) & 0xFF) < 0x30 && ((rgb >> 8) & 0xFF) < 0x30 && (rgb & 0xFF) < 0x30;

    public static bool IsWhite(int rgb) => ((rgb >> 16) & 0xFF) > 0xF0 && ((rgb >> 8) & 0xFF) > 0xF0 && (rgb & 0xFF) > 0xF0;

    /// <summary>Scales a colour's channels: 0.6 makes it as dark as the stock score clock panels sit.</summary>
    public static int Shade(int rgb, double factor)
    {
        if (factor >= 0.999 && factor <= 1.001)
            return rgb;
        int Channel(int shift) => Math.Clamp((int)Math.Round(((rgb >> shift) & 0xFF) * factor), 0, 255);
        return (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    /// <summary>"menu,pres,overlay,gear" (or "all") as targets.</summary>
    public static TruColorTargets ParseTargets(string text)
    {
        var targets = TruColorTargets.None;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            targets |= part.ToLowerInvariant() switch
            {
                "menu" or "fe" => TruColorTargets.Menu,
                "pres" or "presentation" => TruColorTargets.Presentation,
                "overlay" or "clock" => TruColorTargets.Overlay,
                "gear" => TruColorTargets.Gear,
                "all" => TruColorTargets.Default | TruColorTargets.Gear,
                _ => throw new ArgumentException($"Unknown colour target {part}. Use menu, pres, overlay, gear or all."),
            };
        return targets;
    }

    // ---- Preview

    /// <summary>A stand-alone HTML page showing, per team, the site's colours next to the game's current ones and what would change.</summary>
    public static string PreviewHtml(string title, IReadOnlyList<TruColorPlan> plans, TruColorMatches matches, TruColorOptions options, string? source = null)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        static string Chip(int rgb, string? tip = null, bool mark = false) =>
            $"<span class=\"chip{(mark ? " mark" : "")}\" style=\"background:#{GearColours.Hex(rgb)}\" title=\"{E(tip ?? "#" + GearColours.Hex(rgb))}\"></span>";
        var html = new StringBuilder();
        const string Head = """
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <style>
            :root{--bg:#f6f7f9;--fg:#1b1f24;--dim:#5b6470;--line:#d5d9df;--card:#fff;--warn:#9a5b00}
            @media (prefers-color-scheme: dark){:root{--bg:#14171b;--fg:#e8eaed;--dim:#9aa3ad;--line:#2c323a;--card:#1c2026;--warn:#e0a54a}}
            body{background:var(--bg);color:var(--fg);font:14px/1.45 "Segoe UI",system-ui,sans-serif;margin:0;padding:16px}
            h1{font-size:20px;margin:0 0 4px}h2{font-size:16px;margin:16px 0 4px}p{margin:4px 0;color:var(--dim)}
            table{border-collapse:collapse;width:100%;background:var(--card);margin-top:12px}
            th,td{border-bottom:1px solid var(--line);padding:6px 8px;text-align:left;vertical-align:middle}
            th{font-size:12px;color:var(--dim);font-weight:600}
            .chip{display:inline-block;width:20px;height:20px;border-radius:4px;border:1px solid var(--line);margin-right:3px;vertical-align:middle}
            .chip.mark{outline:2px solid var(--fg);outline-offset:1px}
            .arrow{color:var(--dim);margin:0 4px}.hex{font:12px Consolas,monospace;color:var(--dim)}
            .same{opacity:.45}.note{color:var(--warn);font-size:12px}.wrap{overflow-x:auto}
            ul{margin:4px 0 0 18px;padding:0}
            </style>
            """;
        var shade = options.Targets.HasFlag(TruColorTargets.Overlay) && Math.Abs(options.OverlayShade - 1) > 0.001
            ? $", score clock shaded x{options.OverlayShade.ToString("0.##", CultureInfo.InvariantCulture)}" : "";
        var season = options.Season is { } s ? $", colours of {s}-{s + 1}" : ", newest colours";
        html.Append(Head).Append("<title>").Append(E(title)).Append("</title></head><body>\n<h1>").Append(E(title)).Append("</h1>\n");
        html.Append("<p>Colours from TruColor (trucolor.net)").Append(source != null ? ", " + E(source) : "")
            .Append(". Nothing has been written. Outlined swatches are the ones used as primary and secondary.</p>\n");
        html.Append("<p>Writes: ").Append(E(options.Targets.ToString())).Append(shade).Append(season).Append(".</p>\n");
        html.Append("<div class=\"wrap\"><table><tr><th>Team</th><th>Site record and colours</th><th>Change (now → new)</th><th>Notes</th></tr>\n");
        foreach (var plan in plans)
        {
            html.Append("<tr><td><b>").Append(E(plan.Team.Name)).Append("</b><br><span class=\"hex\">").Append(E(plan.Team.League)).Append("</span></td><td>");
            html.Append(E(plan.Site.DisplayName));
            if (plan.Era != null)
            {
                html.Append("<br><span class=\"hex\">").Append(E(plan.Era.Years)).Append("</span><br>");
                foreach (var c in plan.Era.Colours)
                    html.Append(Chip(c.Rgb, c.ToString(), ReferenceEquals(c, plan.Primary) || ReferenceEquals(c, plan.Secondary)));
            }
            html.Append("</td><td>");
            foreach (var c in plan.Changes)
                html.Append($"<div class=\"{(c.Changes ? "" : "same")}\">{Chip(c.Old)}<span class=\"arrow\">→</span>{Chip(c.New)} <span class=\"hex\">{E(c.Label)} {GearColours.Hex(c.Old)} → {GearColours.Hex(c.New)}</span></div>");
            html.Append("</td><td class=\"note\">").Append(string.Join("<br>", plan.Notes.Select(E))).Append("</td></tr>\n");
        }
        html.Append("</table></div>");
        if (matches.UnmatchedGame.Count > 0)
        {
            html.Append("<h2>Game teams with no match on the site</h2><ul>");
            foreach (var t in matches.UnmatchedGame)
                html.Append("<li>").Append(E($"{t.Name} ({t.League})")).Append("</li>");
            html.Append("</ul>");
        }
        if (matches.UnmatchedSite.Count > 0)
        {
            html.Append("<h2>Active site teams with no game team</h2><ul>");
            foreach (var t in matches.UnmatchedSite)
                html.Append("<li>").Append(E(t.ToString())).Append("</li>");
            html.Append("</ul>");
        }
        html.Append("</body></html>");
        return html.ToString();
    }
}
