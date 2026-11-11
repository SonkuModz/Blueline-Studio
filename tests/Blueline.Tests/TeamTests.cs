using BCnEncoder.Encoder;
using Blueline.Core;
using Blueline.Core.Teams;
using Blueline.Formats.Textures;

namespace Blueline.Tests;

/// <summary>Team filing checked against the real game's table and paths. Read-only on the install.</summary>
[TestClass]
public sealed class TeamTests
{
    private const string Player = "nhl/data/kettle/rendering/characters/player/";
    private const string Common = "nhl/data/kettle/rendering/stadiums/common/";
    private const string Logos = "nhl/ui/kettle_assets/teamlogos/";

    private static readonly string[] Clubs =
    [
        "Anaheim Ducks", "Arizona Coyotes", "Boston Bruins", "Buffalo Sabres", "Calgary Flames", "Carolina Hurricanes", "Chicago Blackhawks",
        "Colorado Avalanche", "Columbus Blue Jackets", "Dallas Stars", "Detroit Red Wings", "Edmonton Oilers", "Florida Panthers", "Los Angeles Kings",
        "Minnesota Wild", "Montreal Canadiens", "Nashville Predators", "New Jersey Devils", "New York Islanders", "New York Rangers", "Ottawa Senators",
        "Philadelphia Flyers", "Pittsburgh Penguins", "San Jose Sharks", "St. Louis Blues", "Tampa Bay Lightning", "Toronto Maple Leafs",
        "Vancouver Canucks", "Vegas Golden Knights", "Washington Capitals", "Winnipeg Jets",
    ];

    private static VirtualFileSystem? _vfs;
    private static TeamCatalog? _catalog;
    private static string _temp = "";

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        _temp = Path.Combine(Path.GetTempPath(), "blueline-team-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        var configured = Environment.GetEnvironmentVariable("BLUELINE_GAME");
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); configured == null && d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
            if (File.Exists(Path.Combine(candidate, "eboot.bin")))
                configured = candidate;
        }
        if (configured == null)
            return;
        _vfs = VirtualFileSystem.Mount(GameInstall.Open(configured, useModsFolder: false));
        _catalog = TeamCatalog.Load(ReadStock, _vfs.Files.Select(f => f.Path).ToList());
    }

    [ClassCleanup]
    public static void Stop()
    {
        _vfs?.Dispose();
        Directory.Delete(_temp, true);
    }

    private static byte[]? ReadStock(string path) => _vfs!.Find(path) is { } file ? _vfs.ReadContent(file) : null;

    private static TeamCatalog Catalog
    {
        get
        {
            if (_catalog == null)
                Assert.Inconclusive("No NHL 19 install found. Set BLUELINE_GAME.");
            return _catalog!;
        }
    }

    private static TeamInfo Team(string name) => Catalog.Teams.Single(t => t.Name == name);

    private static IReadOnlyList<TeamArt> Art(string name, TeamArtCategory category) => Catalog.ArtOf(Team(name)).Where(a => a.Category == category).ToList();

    [TestMethod]
    public void EveryNhlClubHasItsCoreArt()
    {
        foreach (var club in Clubs)
        {
            var team = Team(club);
            Assert.AreEqual("NHL", team.League, club);
            Assert.IsNotNull(team.Slug, club);
            Assert.IsNotNull(team.ArenaName, club);
            Assert.IsTrue(team.Capacity > 0, club);
            Assert.IsNotNull(Catalog.MainLogo(team), club);
            Assert.IsNotEmpty(team.Colors, club);
            Assert.IsTrue(Art(club, TeamArtCategory.Logos).Count >= 8, $"{club} logos");
            Assert.IsTrue(Art(club, TeamArtCategory.CenterIce).Any(a => a.Path.EndsWith($"ice_centerlogo_{team.ArtId}_cm.gnf", StringComparison.Ordinal)), $"{club} center ice");
            Assert.IsTrue(Art(club, TeamArtCategory.Uniforms).Count > 0, $"{club} uniforms");
            Assert.IsTrue(Art(club, TeamArtCategory.Pucks).Count > 0, $"{club} puck");
            Assert.IsTrue(Art(club, TeamArtCategory.ArenaBuilding).Count > 0, $"{club} arena");
        }
    }

    [TestMethod]
    public void NamesNeedingOverridesMatchTheRightFiles()
    {
        Assert.AreEqual("barracuda", Team("San Jose Barracuda").Slug);
        Assert.AreEqual("sanjose", Team("San Jose Sharks").Slug);
        Assert.AreEqual("lake_erie", Team("Cleveland Monsters").Slug);
        Assert.AreEqual("thomas_sabo_ice_tigers", Team("Nurnberg Ice Tigers").Slug);
        Assert.AreEqual("scranton", Team("Wilkes-Barre/Scranton Penguins").Slug);
        Assert.AreEqual("nyi", Team("New York Islanders").Slug);
        Assert.AreEqual("nyr", Team("New York Rangers").Slug);
        Assert.AreEqual("toronto", Team("Toronto Maple Leafs Alumni").Slug);
    }

    [TestMethod]
    public void TeamsThatOnlySoundLikeOthersDoNotGetTheirArt()
    {
        Assert.IsNull(Team("Quebec Nordiques Alumni").Slug);
        Assert.IsNull(Team("Hartford Whalers Alumni").Slug);
        Assert.IsNull(Team("Minnesota North Stars Alumni").Slug);
        Assert.IsNull(Team("Notes").Slug);
        Assert.IsNull(Team("EAS").Slug);
    }

    [TestMethod]
    public void ACityTeamDoesNotCollectAnotherTeamOfThatCity()
    {
        foreach (var (club, other) in new[] { ("Calgary Flames", "calgary_hitmen"), ("Edmonton Oilers", "edmonton_oil_kings"), ("Vancouver Canucks", "vancouver_giants"), ("Florida Panthers", "florida_everblades"), ("Colorado Avalanche", "colorado_eagles") })
            Assert.IsFalse(Catalog.ArtOf(Team(club)).Any(a => a.Path.Contains(other, StringComparison.Ordinal)), club);
        Assert.IsTrue(Catalog.ArtOf(Team("Calgary Hitmen")).Any(a => a.Path.Contains("calgary_hitmen", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void FilesAreFiledWhereTheyBelong()
    {
        var art = Catalog.ArtOf(Team("Toronto Maple Leafs")).ToDictionary(a => a.Path);
        Assert.AreEqual(TeamArtCategory.Logos, art[Logos + "large/t27.gnf"].Category);
        Assert.AreEqual("Large mono", art[Logos + "largemono/t27.gnf"].Section);
        Assert.AreEqual("Center ice logo", art[Common + "ice_centerlogo_27_cm.gnf"].Section);
        Assert.AreEqual("Whole rink", art[Common + "icesurface_27_bm.gnf"].Section);
        Assert.AreEqual(TeamArtCategory.Uniforms, art[Player + "font_adidas_toronto_home_cm.gnf"].Category);
        Assert.AreEqual("Adidas, Home", art[Player + "font_adidas_toronto_home_cm.gnf"].Section);
        Assert.AreEqual(TeamArtCategory.Pucks, art[Common + "puck_toronto_cm.gnf"].Category);
        Assert.AreEqual("Banners", art.Values.First(a => a.Name.StartsWith("banner_toronto_", StringComparison.Ordinal)).Section);
        Assert.IsTrue(art.Keys.Any(k => k.Contains("/stadiums/toronto/", StringComparison.Ordinal)));
        Assert.IsTrue(art.Keys.Any(k => k.Contains("/stadiums/custom_toronto/", StringComparison.Ordinal)));
        Assert.IsTrue(art.Values.Any(a => a.IsTechnical), "Technical maps are told apart");
        Assert.IsTrue(art.Values.Any(a => a.Category == TeamArtCategory.Uniforms && !a.IsTechnical));
    }

    [TestMethod]
    public void StadiumFolderNamedDifferentlyStillReachesItsTeam()
    {
        Assert.IsTrue(Catalog.ArtOf(Team("Tampa Bay Lightning")).Any(a => a.Path.Contains("/stadiums/tampa/", StringComparison.Ordinal)));
        Assert.IsTrue(Catalog.ArtOf(Team("New York Rangers")).Any(a => a.Path.Contains("/stadiums/nyrangers/", StringComparison.Ordinal)));
        Assert.IsTrue(Catalog.ArtOf(Team("HC Davos")).Any(a => a.Path.Contains("/stadiums/euroswissdavos/", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SharedFilesNameEveryTeamUsingThem()
    {
        var logoUsers = Catalog.TeamsUsing(Logos + "large/t27.gnf").Select(t => t.Name).ToList();
        CollectionAssert.Contains(logoUsers, "Toronto Maple Leafs");
        CollectionAssert.Contains(logoUsers, "Toronto Maple Leafs Alumni");

        var toronto = Team("Toronto Maple Leafs");
        var line = Catalog.TeamsUsing($"{Common}ice_cline_{toronto.CenterLine}_cm.gnf");
        CollectionAssert.Contains(line.ToList(), toronto);
        Assert.IsEmpty(Catalog.TeamsUsing("nhl/ui/not/a/team/file.gnf"));
    }

    [TestMethod]
    public void LogoSetFillsSquareLogosAndLeavesCardsAlone()
    {
        var team = Team("Boston Bruins");
        var project = ModProject.Create(Path.Combine(_temp, "logos"), "Logos");
        var result = TeamLogoImport.Run(Catalog.ArtOf(team), Disc(256), ReadStock, project, team.Name, CompressionQuality.Fast);

        Assert.AreEqual(0, result.Failed, string.Join("\n", result.Items.Where(i => i.Outcome == LogoOutcome.Failed)));
        Assert.IsTrue(result.Imported >= 8);
        Assert.IsTrue(result.Skipped > 0, "HUT card art is skipped");
        Assert.AreEqual(result.Imported, project.Changes.Count);
        Assert.IsTrue(project.CanUndo);

        byte[] Current(string path) => project.ReadBlob(project.Changes.Single(c => c.Path == path));

        // Same shape as the original, and the corner of the picture is see-through.
        var large = GameTexture.TryOpen(Current(Logos + "large/t2.gnf"))!;
        var stock = GameTexture.TryOpen(ReadStock(Logos + "large/t2.gnf")!)!;
        Assert.AreEqual(stock.Width, large.Width);
        Assert.AreEqual(stock.MipCount, large.MipCount);
        var picture = large.DecodeMip(0);
        Assert.IsTrue(picture.Pixels[3] < 16, "The corner stays transparent");
        var middle = (picture.Height / 2 * picture.Width + picture.Width / 2) * 4;
        Assert.IsTrue(picture.Pixels[middle + 3] > 200);
        Assert.IsTrue(picture.Pixels[middle + 2] > 180, "The middle keeps the source's blue");

        // A mono logo is flat: the source's blue has gone.
        var mono = GameTexture.TryOpen(Current(Logos + "largemono/t2.gnf"))!.DecodeMip(0);
        var monoMiddle = (mono.Height / 2 * mono.Width + mono.Width / 2) * 4;
        Assert.IsTrue(mono.Pixels[monoMiddle + 3] > 200);
        Assert.IsTrue(mono.Pixels[monoMiddle + 2] < 100, "Mono takes the original's colour, not the source's");

        Assert.IsTrue(result.Items.Where(i => i.Path.Contains("/hutcardbw/", StringComparison.Ordinal) || i.Path.Contains("/hutcardrare/", StringComparison.Ordinal)).All(i => i.Outcome == LogoOutcome.Skipped));
        Assert.IsTrue(result.Items.Any(i => i.Path.Contains("/hutcardrare/", StringComparison.Ordinal)));

        project.Undo();
        Assert.AreEqual(0, project.Changes.Count, "One undo step removes the whole set");
    }

    [TestMethod]
    public void AnOpaquePictureLeavesMonoLogosAlone()
    {
        var team = Team("Boston Bruins");
        var opaque = new RgbaImage(64, 64, Enumerable.Repeat((byte)200, 64 * 64 * 4).ToArray());
        Assert.IsFalse(TeamLogoImport.HasTransparency(opaque));
        var project = ModProject.Create(Path.Combine(_temp, "opaque"), "Opaque");
        var result = TeamLogoImport.Run(Catalog.ArtOf(team).Where(a => a.Path.Contains("/large", StringComparison.Ordinal)).ToList(), opaque, ReadStock, project, team.Name, CompressionQuality.Fast);
        Assert.AreEqual(LogoOutcome.Skipped, result.Items.Single(i => i.Path.EndsWith("largemono/t2.gnf", StringComparison.Ordinal)).Outcome);
        Assert.AreEqual(LogoOutcome.Imported, result.Items.Single(i => i.Path.EndsWith("large/t2.gnf", StringComparison.Ordinal)).Outcome);
    }

    [TestMethod]
    public void AWidePictureIsNotStretched()
    {
        var pixels = new byte[200 * 100 * 4];
        Array.Fill(pixels, (byte)255);
        var fitted = TeamLogoImport.Contain(new RgbaImage(200, 100, pixels), 128, 128);
        Assert.AreEqual(128, fitted.Width);
        Assert.AreEqual(128, fitted.Height);
        Assert.AreEqual(0, fitted.Pixels[3], "Above the picture is transparent");
        Assert.AreEqual(255, fitted.Pixels[(64 * 128 + 64) * 4 + 3]);
    }

    private static RgbaImage Disc(int size)
    {
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                if ((x - size / 2) * (x - size / 2) + (y - size / 2) * (y - size / 2) > size * size * 2 / 10)
                    continue;
                var i = (y * size + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)40, (byte)120, (byte)255, (byte)255);
            }
        return new RgbaImage(size, size, pixels);
    }
}
