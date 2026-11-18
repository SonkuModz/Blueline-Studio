using System.Net;
using System.Text;
using Blueline.Core;
using Blueline.Core.Teams;
using Blueline.Formats.Tdb;

namespace Blueline.Tests;

/// <summary>TruColor team colours: parsing saved pages, matching to game teams, politeness (no network), and a plan on the real game.</summary>
[TestClass]
public sealed class TruColorTests
{
    private static string _temp = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        _temp = Path.Combine(Path.GetTempPath(), "blueline-trucolor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    [ClassCleanup]
    public static void Stop() => Directory.Delete(_temp, true);

    [TestMethod]
    public void Index_ListsLeaguesWithPagesAndThoseWithout()
    {
        var leagues = TruColorSite.ParseIndex(TruColorSamples.Index);
        var franchise = leagues.Single(l => l.Name == "National Hockey League: Franchise Records");
        Assert.AreEqual("1917-1918 through present", franchise.Years);
        Assert.AreEqual("Professional Leagues", franchise.Group);
        Assert.StartsWith("national-hockey-league-official-colors-franchise-records", franchise.Key!);
        Assert.IsFalse(leagues.Any(l => l.Name == "National Hockey League"), "the parent of the three NHL pages is not a league");

        var ahl = leagues.Single(l => l.Name == "American Hockey League");
        Assert.AreEqual("Minor-Pro Leagues", ahl.Group);
        Assert.IsNotNull(ahl.Url);
        Assert.IsNull(leagues.Single(l => l.Name == "All American Hockey League").Url, "listed, but the site has no page");
        Assert.AreEqual(2, leagues.Count(l => l.Name == "Western Hockey League"), "the old pro WHL and the junior WHL");

        CollectionAssert.AreEqual(new[] { 1 }, TruColorImport.GameLeaguesOf(ahl).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 21, 22 }, TruColorImport.GameLeaguesOf(franchise).ToArray());
        Assert.AreEqual(13, TruColorImport.GameLeaguesOf(leagues.Single(l => l.Name == "Western Hockey League" && l.Url != null && l.Years.StartsWith("1977"))).Single());
        Assert.IsEmpty(TruColorImport.GameLeaguesOf(leagues.Single(l => l.Name == "SPHL")));
    }

    [TestMethod]
    public void League_ReadsTeamsErasAndSwatchesInOrder()
    {
        var teams = TruColorSite.ParseLeague(TruColorSamples.Ahl);
        Assert.HasCount(3, teams);
        var abbotsford = teams[0];
        Assert.AreEqual("ABBOTSFORD CANUCKS", abbotsford.Name);
        Assert.AreEqual("Abbotsford Canucks", abbotsford.DisplayName);
        Assert.IsTrue(abbotsford.Active);
        Assert.AreEqual(2021, abbotsford.From);
        Assert.IsNull(abbotsford.To);
        var era = abbotsford.Current!;
        Assert.AreEqual("2021-2022 through present", era.Years);
        Assert.AreEqual("Field Green, Pacific Blue, Mountain White", era.Summary);
        CollectionAssert.AreEqual(new[] { 0x00843D, 0x00205B, 0xFFFFFF, 0x051C2C, 0x97999B }, era.Colours.Select(c => c.Rgb).ToArray());
        Assert.AreEqual("Field Green", era.Colours[0].Name);
        Assert.AreEqual("PANTONE 348 C", era.Colours[0].Code);
        Assert.IsTrue(era.Colours[3].IsSupporting);
        Assert.IsFalse(era.Colours[0].IsSupporting);

        var bakersfield = teams[1];
        Assert.HasCount(2, bakersfield.Franchise!.Eras);
        Assert.AreEqual(0xFC4C02, bakersfield.Current!.Colours[0].Rgb);
        Assert.AreEqual("Metallic", bakersfield.Current.Colours[2].Note);
        Assert.AreEqual(0x00205B, bakersfield.EraFor(2016)!.Colours[0].Rgb, "2016-2017 wore royal blue");
        Assert.IsNull(bakersfield.EraFor(2010));

        var worcester = teams[2];
        Assert.IsFalse(worcester.Active, "under HISTORICAL FRANCHISE RECORDS");
        Assert.AreEqual((2006, 2014), (worcester.From!.Value, worcester.To!.Value));
    }

    [TestMethod]
    public void League_KeepsOtherSectionsOutOfTheFranchiseColours()
    {
        var teams = TruColorSite.ParseLeague(TruColorSamples.Nhl);
        var kraken = teams.Single(t => t.Name == "SEATTLE KRAKEN");
        Assert.IsTrue(kraken.Sections.Count > 1, "the Kraken have more than franchise colours");
        Assert.AreEqual(TruColorTeam.FranchiseSection, kraken.Sections[0].Name);
        CollectionAssert.AreEqual(new[] { 0x051C2C, 0x9CDBD9 }, kraken.Current!.Colours.Take(2).Select(c => c.Rgb).ToArray());
        var utah = teams.Single(t => t.Name == "UTAH MAMMOTH");
        Assert.IsTrue(utah.Active, "a check-mark entity before the name is not part of it");
        Assert.AreEqual(0x69B3E7, utah.Current!.Colours[1].Rgb);
        Assert.AreEqual(2, teams.Count(t => t.Name == "OTTAWA SENATORS" && !t.Active));
        foreach (var era in teams.SelectMany(t => t.Sections).SelectMany(s => s.Eras))
            Assert.IsTrue(era.Colours.Count > 0, era.ToString());
    }

    [TestMethod]
    public void League_DecodesAccentedNames()
    {
        var team = TruColorSite.ParseLeague(TruColorSamples.Qmjhl).Single();
        Assert.AreEqual("CHICOUTIMI SAGUENÉENS", team.Name);
        Assert.AreEqual("Chicoutimi Saguenéens", team.DisplayName);
        Assert.AreEqual(1973, team.From);
    }

    private static TeamInfo Game(int index, string name, string city, int league = 1) =>
        new(index, index, name, city, "", league, league == 0 ? "NHL" : "AHL", null, 0, null, null, 0, 0, [0, 0, 0]);

    private static TruColorTeam Site(string name, bool active, int from, int? to) =>
        new(name, "", active, from, to, [new(TruColorTeam.FranchiseSection, [new("", "", from, to, [new("PRIMARY COLORS", [new("Red", 0xFF0000, null, null, false)])])])]);

    [TestMethod]
    public void Match_UsesFullNamesAliasesAndPrefersActiveTeams()
    {
        var site = new List<TruColorTeam>
        {
            Site("OTTAWA SENATORS", false, 1917, 1930),
            Site("OTTAWA SENATORS", true, 1992, null),
            Site("OTTAWA SENATORS", false, 1932, 1933),
            Site("BELLEVILLE SENATORS", true, 2017, null),
            Site("SEATTLE KRAKEN", true, 2021, null),
            Site("SAULT STE. MARIE GREYHOUNDS", true, 1980, null),
            Site("QUEBEC NORDIQUES", false, 1979, 1994),
            Site("CHICOUTIMI SAGUENÉENS", true, 1973, null),
            Site("UNUSED CLUB", true, 2000, null),
            Site("GONE CLUB", false, 1950, 1960),
        };
        var game = new[]
        {
            Game(1, "Ottawa Senators", "Ottawa", 0),
            Game(2, "Belleville Senators", "Belleville"),
            Game(3, "Kraken", "Seattle", 0),
            Game(4, "Soo Greyhounds", "Sault Ste. Marie"),
            Game(5, "Quebec Nordiques Alumni", "Quebec", 21),
            Game(6, "Chicoutimi Sagueneens", "Chicoutimi"),
            Game(7, "Atlantic Division All-Stars", "Atlantic", 0),
            Game(8, "Senators", "Nowhere"),
        };
        var result = TruColorImport.Match(game, site);
        TruColorTeam? Of(int index) => result.Matched.SingleOrDefault(m => m.Team.Index == index)?.Site;
        Assert.AreEqual(1992, Of(1)!.From, "the active Ottawa Senators");
        Assert.AreEqual("BELLEVILLE SENATORS", Of(2)!.Name, "a farm club is not its parent");
        Assert.AreEqual("SEATTLE KRAKEN", Of(3)!.Name);
        Assert.AreEqual("SAULT STE. MARIE GREYHOUNDS", Of(4)!.Name);
        Assert.AreEqual("QUEBEC NORDIQUES", Of(5)!.Name);
        Assert.AreEqual("CHICOUTIMI SAGUENÉENS", Of(6)!.Name, "accents do not matter");
        CollectionAssert.AreEquivalent(new[] { 7, 8 }, result.UnmatchedGame.Select(t => t.Index).ToArray(), "never matched on a nickname alone");
        CollectionAssert.AreEquivalent(new[] { "UNUSED CLUB" }, result.UnmatchedSite.Select(t => t.Name).ToArray(), "historical clubs with no game side are not listed");

        var forced = TruColorImport.Match([Game(8, "Senators", "Nowhere")], site, new Dictionary<string, string> { ["Senators"] = "Belleville Senators" });
        Assert.AreEqual("BELLEVILLE SENATORS", forced.Matched.Single().Site.Name);
        Assert.AreEqual("override", forced.Matched.Single().How);
    }

    [TestMethod]
    public void Shade_ScalesEachChannel()
    {
        Assert.AreEqual(0xC8102E, TruColorImport.Shade(0xC8102E, 1));
        Assert.AreEqual(0x780A1C, TruColorImport.Shade(0xC8102E, 0.6));
        Assert.AreEqual(TruColorTargets.Menu | TruColorTargets.Overlay, TruColorImport.ParseTargets("menu, clock"));
        Assert.ThrowsExactly<ArgumentException>(() => TruColorImport.ParseTargets("jersey"));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Url, DateTime At, string? Agent)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Seen.Add((request.RequestUri!.ToString(), DateTime.UtcNow, request.Headers.UserAgent.ToString()));
            return Task.FromResult(answer(request));
        }
    }

    [TestMethod]
    public async Task Site_CachesPagesSpacesRequestsAndNeverRetriesARefusal()
    {
        var fake = new FakeHandler(r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(r.RequestUri!.AbsolutePath.StartsWith("/team-colors/hockey", StringComparison.Ordinal) ? TruColorSamples.Index : TruColorSamples.Ahl, Encoding.UTF8),
        });
        using (var site = new TruColorSite(Path.Combine(_temp, "cache"), fake))
        {
            var leagues = await site.ListLeaguesAsync();
            var ahl = leagues.Single(l => l.Name == "American Hockey League");
            Assert.HasCount(3, await site.ListTeamsAsync(ahl));
            Assert.HasCount(3, await site.ListTeamsAsync(ahl));
            await site.ListLeaguesAsync();
            Assert.AreEqual(2, site.Requests, "the second reads come from the cache");
            Assert.IsTrue(fake.Seen[1].At - fake.Seen[0].At >= TimeSpan.FromSeconds(1.9), "requests are spaced out");
            Assert.DoesNotContain("@", fake.Seen[0].Agent!);
            Assert.IsNotNull(site.CachedAt(ahl.Url!));
            await site.GetPageAsync(ahl.Url!, refresh: true);
            Assert.AreEqual(3, site.Requests);
        }

        var refusing = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var refused = new TruColorSite(Path.Combine(_temp, "refused"), refusing);
        await Assert.ThrowsExactlyAsync<TruColorRefusedException>(() => refused.ListLeaguesAsync());
        Assert.HasCount(1, refusing.Seen);
    }

    // ---- On the real game (inconclusive without it)

    private static VirtualFileSystem? Mount()
    {
        var configured = Environment.GetEnvironmentVariable("BLUELINE_GAME");
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); configured == null && d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
            if (File.Exists(Path.Combine(candidate, "eboot.bin")))
                configured = candidate;
        }
        return configured == null ? null : VirtualFileSystem.Mount(GameInstall.Open(configured, useModsFolder: false));
    }

    [TestMethod]
    public void RealGame_PlansAndAppliesStockClubColours()
    {
        using var vfs = Mount();
        if (vfs == null)
            Assert.Inconclusive("NHL 19 is not next to the repository and BLUELINE_GAME is not set.");
        byte[]? Read(string p) => vfs.Find(p) is { } f ? vfs.ReadContent(f) : null;
        var catalog = TeamCatalog.Load(Read, vfs.Files.Select(f => f.Path).ToList());
        var file = TdbFile.Parse((byte[])Read(TeamCatalog.DatabasePath)!.Clone());
        file.ApplyMeta(Encoding.UTF8.GetString(Read(TeamCatalog.MetaPath)!));
        var details = TeamDetails.Open(file);
        var gear = GearColours.Open(file);

        // Calgary as the stock game has it: menu D0122A, clock 641313.
        var flames = catalog.Teams.Single(t => t.LeagueId == 0 && t.Name.StartsWith("Calgary Flames", StringComparison.Ordinal));
        var site = new TruColorTeam("CALGARY FLAMES", "1980-1981 through present", true, 1980, null,
        [
            new(TruColorTeam.FranchiseSection,
            [
                new("2020-2021 through present", "Red, Gold, Black, White", 2020, null,
                    [new("PRIMARY COLORS", [new("Red", 0xC8102E, null, null, false), new("Gold", 0xF1BE48, null, null, false), new("Black", 0x010101, null, null, false)])]),
            ]),
        ]);
        var options = new TruColorOptions(TruColorTargets.Default | TruColorTargets.Gear, OverlayShade: 0.6);
        var plan = TruColorImport.Plan(details, gear, flames, site, options);
        var byKey = plan.Changes.ToDictionary(c => c.Key);
        Assert.AreEqual(0xD0122A, byKey["team.feprimarycolor"].Old);
        Assert.AreEqual(0xC8102E, byKey["team.feprimarycolor"].New);
        Assert.AreEqual(0xF1BE48, byKey["team.fesecondarycolor"].New);
        Assert.AreEqual(0x641313, byKey["team.overlayprimarycolor"].Old);
        Assert.AreEqual(TruColorImport.Shade(0xC8102E, 0.6), byKey["team.overlayprimarycolor"].New);
        Assert.IsTrue(plan.Changes.Any(c => c.Key.StartsWith("gear.", StringComparison.Ordinal)), "home gloves and helmet when asked for");

        var swapped = TruColorImport.Plan(details, null, flames, site, new TruColorOptions(TruColorTargets.Menu), new TruColorPick(1, 0));
        Assert.AreEqual(0xF1BE48, swapped.Changes.Single(c => c.Key == "team.feprimarycolor").New);
        Assert.HasCount(2, swapped.Changes, "only the menu colours");

        // Black first on the site: menus lead with the accent like the stock Bruins (gold), the clock keeps black.
        var bruins = catalog.Teams.Single(t => t.LeagueId == 0 && t.Name.StartsWith("Boston Bruins", StringComparison.Ordinal));
        var black = new TruColorTeam("BOSTON BRUINS", "", true, 1924, null,
        [
            new(TruColorTeam.FranchiseSection, [new("2008-2009 through present", "", 2008, null,
                [new("PRIMARY COLORS", [new("Black", 0x010101, null, null, false), new("Gold", 0xFFB81C, null, null, false), new("White", 0xFFFFFF, null, null, false)])])]),
        ]);
        var auto = TruColorImport.Plan(details, null, bruins, black, new TruColorOptions()).Changes.ToDictionary(c => c.Key, c => c.New);
        Assert.AreEqual(0xFFB81C, auto["team.feprimarycolor"]);
        Assert.AreEqual(0x010101, auto["team.fesecondarycolor"]);
        Assert.AreEqual(0xFFB81C, auto["team.presprimarycolor"]);
        Assert.AreEqual(0x010101, auto["team.overlayprimarycolor"]);
        Assert.AreEqual(0xFFB81C, auto["team.overlaysecondarycolor"]);
        var asSite = TruColorImport.Plan(details, null, bruins, black, new TruColorOptions(), new TruColorPick(0, 1));
        Assert.AreEqual(0x010101, asSite.Changes.Single(c => c.Key == "team.feprimarycolor").New, "a pick of its own is kept as given");
        Assert.AreEqual(new TruColorPick(1, 0), TruColorImport.Plan(details, null, bruins, black, new TruColorOptions()).CurrentPick);

        var count = TruColorImport.Apply(details, gear, [plan]);
        Assert.AreEqual(plan.ChangeCount, count);
        var saved = details.Save();
        var reread = TdbFile.Parse(saved);
        reread.ApplyMeta(Encoding.UTF8.GetString(Read(TeamCatalog.MetaPath)!));
        var after = TeamDetails.Open(reread).FieldsOf(flames.Index).ToDictionary(f => f.Key);
        Assert.AreEqual(0xC8102E, Convert.ToInt32(after["team.feprimarycolor"].Value));
        Assert.AreEqual(0xF1BE48, Convert.ToInt32(after["team.pressecondarycolor"].Value));
        Assert.AreEqual(TruColorImport.Shade(0xF1BE48, 0.6), Convert.ToInt32(after["team.overlaysecondarycolor"].Value));
        var gearRow = int.Parse(plan.Changes.First(c => c.Key.StartsWith("gear.", StringComparison.Ordinal)).Key[5..]);
        Assert.AreEqual(0xC8102E, GearColours.Open(reread).RowsOf(GearColours.GearTeamOf(flames)!.Value).Single(r => r.Index == gearRow).Main, "gear edits land in the same database");
        Assert.IsTrue(reread.VerifyCrcs().All(c => c.Matches));

        var html = TruColorImport.PreviewHtml("NHL", [plan], new TruColorMatches([], [], []), options);
        StringAssert.Contains(html, "C8102E");
        StringAssert.Contains(html, "Nothing has been written");
    }
}
