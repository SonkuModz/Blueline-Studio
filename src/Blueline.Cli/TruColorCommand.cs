using System.Globalization;
using System.Text;
using Blueline.Core;
using Blueline.Core.Teams;
using Blueline.Formats.Tdb;

/// <summary>
/// blueline trucolor: list TruColor's hockey leagues, or preview (and with --write, apply into a project) one league's colours.
/// Never touches the game folder or a roster save; the existing roster-sync verb carries project edits into a save.
/// </summary>
internal static class TruColorCommand
{
    public const string Usage = """
        blueline trucolor <game> [league] [options]   Team colours from TruColor (trucolor.net): no league lists the site's leagues;
                                                 a league (NHL, AHL, ECHL, OHL, QMJHL, WHL or a site league name) previews each team's
                                                 colours next to its current ones. Nothing is written without --project and --write.
            --cache <folder>        where pages are kept (default %LOCALAPPDATA%\Blueline\trucolor); --refresh fetches again
            --season <year>         colours worn in that season (2018 = 2018-19) instead of the newest
            --targets <list>        menu,pres,overlay,gear or all (default menu,pres,overlay; gear is home gloves and helmet)
            --overlay-shade <x>     scale the score clock colours, e.g. 0.6 is about as dark as stock (default 1)
            --pick "<team>=2,1"     which site colours are primary and secondary for a team (1-based; repeatable)
            --map "<team>=<site name>"   match a game team to a differently named site team (repeatable)
            --preview <file.html>   write a preview page
            --alumni                also the NHL Alumni sides (left out by default: the site gives them their club's newest colours)
            --project <folder> --write   apply into the project's copy of nhl_game.db
        """;

    public static int Run(VirtualFileSystem vfs, TeamCatalog catalog, string[] a)
    {
        try
        {
            return RunInner(vfs, catalog, a);
        }
        catch (Exception e) when (e is FormatException or OverflowException or HttpRequestException or TruColorRefusedException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Error: {(e is TaskCanceledException ? "trucolor.net did not answer in time." : e.Message)}");
            return 1;
        }
    }

    private static int RunInner(VirtualFileSystem vfs, TeamCatalog catalog, string[] a)
    {
        var options = a.Skip(2).ToList();
        string? Option(string name) => options.IndexOf(name) is >= 0 and var i && i + 1 < options.Count ? options[i + 1] : null;
        IEnumerable<string> All(string name) => options.Select((o, i) => (o, i)).Where(p => p.o == name && p.i + 1 < options.Count).Select(p => options[p.i + 1]);
        var flags = new HashSet<string>(["--refresh", "--write", "--alumni"]);
        var valued = new HashSet<string>(["--cache", "--season", "--targets", "--overlay-shade", "--pick", "--map", "--preview", "--project"]);
        string? leagueArg = null;
        for (var i = 0; i < options.Count; i++)
        {
            if (valued.Contains(options[i])) { i++; continue; }
            if (flags.Contains(options[i])) continue;
            if (options[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unknown option {options[i]}.\n{Usage}");
            leagueArg = leagueArg == null ? options[i] : throw new ArgumentException($"Only one league at a time.\n{Usage}");
        }

        using var site = new TruColorSite(Option("--cache") ?? TruColorSite.DefaultCache);
        var refresh = options.Contains("--refresh");
        var leagues = site.ListLeaguesAsync(refresh).GetAwaiter().GetResult();
        if (leagueArg == null)
        {
            foreach (var group in leagues.GroupBy(l => l.Group))
            {
                Console.WriteLine($"-- {group.Key}");
                foreach (var l in group)
                {
                    var game = TruColorImport.GameLeaguesOf(l).Select(id => catalog.Teams.FirstOrDefault(t => t.LeagueId == id)?.League).OfType<string>().Distinct().ToList();
                    Console.WriteLine($"{l.Name,-56} {l.Years,-30} {(l.Url == null ? "no page on the site" : game.Count > 0 ? "game: " + string.Join(", ", game) : "")}");
                }
            }
            Console.WriteLine("No European leagues are on the site. Pages read: " + site.Requests + " (the rest from the cache).");
            return 0;
        }

        var gameLeague = catalog.Teams.FirstOrDefault(t => t.League.Equals(leagueArg, StringComparison.OrdinalIgnoreCase))?.LeagueId;
        var league = (gameLeague is { } id ? TruColorImport.SiteLeagueFor(id, leagues) : null)
            ?? leagues.FirstOrDefault(l => l.Url != null && l.Name.Equals(leagueArg, StringComparison.OrdinalIgnoreCase))
            ?? leagues.FirstOrDefault(l => l.Url != null && l.Name.Contains(leagueArg, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"No league {leagueArg} with a page on the site. Run without a league to list them.");
        var gameLeagues = TruColorImport.GameLeaguesOf(league);
        if (gameLeagues.Count == 0)
            throw new InvalidOperationException($"{league.Name} has no league in the game to put colours into.");
        var siteTeams = site.ListTeamsAsync(league, refresh).GetAwaiter().GetResult();

        var overrides = All("--map").Select(Split).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var alumni = options.Contains("--alumni");
        var gameTeams = catalog.Teams.Where(t => gameLeagues.Contains(t.LeagueId) && (alumni || !TruColorImport.IsAlumni(t))).OrderBy(t => t.LeagueId).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var matches = TruColorImport.Match(gameTeams, siteTeams, overrides);

        var targets = Option("--targets") is { } t ? TruColorImport.ParseTargets(t) : TruColorTargets.Default;
        var shade = Option("--overlay-shade") is { } s
            ? double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : throw new ArgumentException($"--overlay-shade takes a number such as 0.6, not {s}.")
            : 1.0;
        if (shade is <= 0 or > 1.5)
            throw new ArgumentException("--overlay-shade takes a number such as 0.6 (0 to 1.5).");
        var season = Option("--season") is { } y
            ? int.TryParse(y, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) && year is > 1900 and < 2100 ? year : throw new ArgumentException($"--season takes a start year such as 2018, not {y}.")
            : (int?)null;
        var settings = new TruColorOptions(targets, shade, season);
        var picks = All("--pick").Select(Split).ToDictionary(p => TruColorImport.Normalize(p.Key), p =>
        {
            var parts = p.Value.Split(',').Select(n => int.TryParse(n.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v - 1 : -1).ToArray();
            return parts.Length == 2 && parts.All(x => x >= 0) ? new TruColorPick(parts[0], parts[1]) : throw new ArgumentException($"--pick {p.Key}= takes two colour numbers, e.g. 2,1.");
        });

        var projectFolder = Option("--project");
        var write = options.Contains("--write");
        if (write && projectFolder == null)
            throw new ArgumentException("--write needs --project <folder>.");
        var project = projectFolder != null ? ModProject.Open(projectFolder) : null;
        var data = project != null && project.TryGet(TeamCatalog.DatabasePath, out var stored) ? project.ReadBlob(stored) : vfs.ReadContent(vfs.Find(TeamCatalog.DatabasePath)!);
        var file = TdbFile.Parse((byte[])data.Clone());
        file.ApplyMeta(Encoding.UTF8.GetString(vfs.ReadContent(vfs.Find(TeamCatalog.MetaPath)!)));
        var details = TeamDetails.Open(file);
        var gear = targets.HasFlag(TruColorTargets.Gear) ? GearColours.Open(file) : null;

        var plans = matches.Matched.Select(m => TruColorImport.Plan(details, gear, m.Team, m.Site, settings, picks.GetValueOrDefault(TruColorImport.Normalize(m.Team.Name)))).ToList();
        Console.WriteLine($"{league.Name} ({league.Years}): {siteTeams.Count} site teams, {gameTeams.Count} game teams, {matches.Matched.Count} matched. Pages read: {site.Requests}.");
        foreach (var plan in plans)
        {
            var match = matches.Matched.First(m => m.Team == plan.Team);
            Console.WriteLine($"{plan.Team.Name}  <-  {plan.Site.DisplayName} ({plan.Site.Years}, by {match.How}){(plan.Era != null ? $", {plan.Era.Years}" : "")}");
            if (plan.Era != null)
                Console.WriteLine("    site: " + string.Join("  ", plan.Era.Colours.Select((c, i) => $"{i + 1}:{c.Hex}{(ReferenceEquals(c, plan.Primary) ? "(P)" : ReferenceEquals(c, plan.Secondary) ? "(S)" : "")} {c.Name}")));
            foreach (var c in plan.Changes)
                Console.WriteLine($"    {c.Label,-28} {GearColours.Hex(c.Old)} -> {GearColours.Hex(c.New)}{(c.Changes ? "" : "  (same)")}");
            foreach (var note in plan.Notes)
                Console.WriteLine("    note: " + note);
        }
        foreach (var team in matches.UnmatchedGame)
            Console.WriteLine($"No match on the site: {team.Name} ({team.League}). Use --map \"{team.Name}=<site name>\" if it is there under another name.");
        foreach (var team in matches.UnmatchedSite)
            Console.WriteLine($"Active on the site, not in the game: {team}");

        if (Option("--preview") is { } preview)
        {
            File.WriteAllText(preview, TruColorImport.PreviewHtml($"{league.Name}: TruColor colours", plans, matches, settings, $"{league.Name} page"));
            Console.WriteLine($"Preview: {Path.GetFullPath(preview)}");
        }
        if (!write)
        {
            Console.WriteLine($"{plans.Sum(p => p.ChangeCount)} cell(s) would change. Nothing written (add --project <folder> --write).");
            return 0;
        }
        var count = TruColorImport.Apply(details, gear, plans);
        if (count == 0)
        {
            Console.WriteLine("Nothing to change.");
            return 0;
        }
        project!.Set(TeamCatalog.DatabasePath, details.Save(), $"{league.Name} colours from TruColor");
        project.Save();
        Console.WriteLine($"{count} cell(s) written to {project.Folder}. A roster save keeps its own copy of these tables: use roster-sync to carry them over.");
        return 0;
    }

    private static (string Key, string Value) Split(string text)
    {
        var at = text.LastIndexOf('=');
        return at > 0 ? (text[..at].Trim(), text[(at + 1)..].Trim()) : throw new ArgumentException($"Expected name=value: {text}");
    }
}
