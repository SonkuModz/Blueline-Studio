using System.Text;
using Blueline.Core;
using Blueline.Core.Teams;
using Blueline.Formats.Tdb;

namespace Blueline.Tests;

/// <summary>Glove and helmet colours per kit, checked against the real game's tables. The expected rows are the ones seen in game
/// (docs/RESEARCH.md section 6.10). Read-only on the install.</summary>
[TestClass]
public sealed class GearColourTests
{
    private static VirtualFileSystem? _vfs;
    private static TeamCatalog? _catalog;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
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
        _catalog = TeamCatalog.Load(Read, _vfs.Files.Select(f => f.Path).ToList());
    }

    [ClassCleanup]
    public static void Stop() => _vfs?.Dispose();

    private static byte[]? Read(string path) => _vfs!.Find(path) is { } file ? _vfs.ReadContent(file) : null;

    private static GearColours OpenGear()
    {
        if (_vfs == null)
            Assert.Inconclusive("NHL 19 is not next to the repository and BLUELINE_GAME is not set.");
        var file = TdbFile.Parse(Read(TeamCatalog.DatabasePath)!);
        file.ApplyMeta(Encoding.UTF8.GetString(Read(TeamCatalog.MetaPath)!));
        return GearColours.Open(file);
    }

    private static TeamInfo Team(string name) => _catalog!.Teams.Single(t => t.Name == name && t.LeagueId == 0);

    [TestMethod]
    public void StLouisKitsReadTheRowsSeenInGame()
    {
        var gear = OpenGear();
        var blues = Team("St. Louis Blues");
        Assert.AreEqual(25, GearColours.GearTeamOf(blues));
        var kits = gear.KitsOf(blues).ToDictionary(k => k.Label);

        var homeGloves = gear.Resolve(25, kits["Home"], GearPart.Gloves);
        Assert.AreEqual(GearLink.Fallback, homeGloves.Link);
        Assert.AreEqual(900, homeGloves.Row!.Index);
        Assert.AreEqual(900, gear.Resolve(25, kits["Away"], GearPart.Gloves).Row!.Index);
        Assert.AreEqual(907, gear.Resolve(25, kits["Home"], GearPart.Helmet).Row!.Index);
        Assert.AreEqual(908, gear.Resolve(25, kits["Away"], GearPart.Helmet).Row!.Index);
        Assert.AreEqual(901, gear.Resolve(25, kits["Alternate (variant 3)"], GearPart.Gloves).Row!.Index);
        Assert.AreEqual(909, gear.Resolve(25, kits["Alternate (variant 3)"], GearPart.Helmet).Row!.Index);

        CollectionAssert.AreEquivalent(new[] { "Home", "Away", "Alternate (variant 4)" }, gear.KitsUsing(blues, homeGloves.Row).Select(k => k.Label).ToArray());
    }

    [TestMethod]
    public void MainColourKeepsAccents()
    {
        var gear = OpenGear();
        var row = gear.RowsOf(25).Single(r => r.Index == 900);
        var accents = row.Zones.Select((c, i) => (c, i)).Where(z => z.c != row.Main).ToList();
        var changed = gear.SetMain(row, 0x0072CE);
        Assert.AreEqual(0x0072CE, changed.Main);
        foreach (var (colour, zone) in accents)
            Assert.AreEqual(colour, changed.Zones[zone]);
        Assert.IsTrue(changed.Zones.Count(c => c == 0x0072CE) == GearColours.ZoneCount - accents.Count);

        var saved = TdbFile.Parse(gear.Save());
        Assert.IsTrue(saved.VerifyCrcs().All(c => c.Matches));
    }

    [TestMethod]
    public void TorontoHomeGlovesTakeTheUnusedRow()
    {
        var gear = OpenGear();
        var leafs = Team("Toronto Maple Leafs");
        var home = gear.KitsOf(leafs).Single(k => k.Label == "Home");
        Assert.AreEqual(995, gear.UnusedRow(leafs, GearPart.Gloves)!.Index);

        var (row, added) = gear.GiveOwnRow(leafs, home, GearPart.Gloves);
        Assert.IsFalse(added);
        Assert.AreEqual(995, row.Index);
        Assert.AreEqual(GearLink.Own, gear.Resolve(28, home, GearPart.Gloves).Link);
        Assert.AreEqual(GearLink.Fallback, gear.Resolve(28, gear.KitsOf(leafs).Single(k => k.Label == "Away"), GearPart.Gloves).Link);
    }

    [TestMethod]
    public void NewRowGoesInTheSpareSpaceAndSurvivesASave()
    {
        var gear = OpenGear();
        var blues = Team("St. Louis Blues");
        var away = gear.KitsOf(blues).Single(k => k.Label == "Away");
        var spare = gear.SpareRows;
        var size = gear.File.Data.Length;

        var (row, added) = gear.GiveOwnRow(blues, away, GearPart.Gloves);
        Assert.IsTrue(added);
        Assert.AreEqual(spare - 1, gear.SpareRows);
        gear.SetMain(row, 0xFFFFFF);

        var data = gear.Save();
        Assert.AreEqual(size, data.Length);
        var file = TdbFile.Parse(data);
        file.ApplyMeta(Encoding.UTF8.GetString(Read(TeamCatalog.MetaPath)!));
        var reopened = GearColours.Open(file);
        var resolved = reopened.Resolve(25, away, GearPart.Gloves);
        Assert.AreEqual(GearLink.Own, resolved.Link);
        Assert.AreEqual(0xFFFFFF, resolved.Row!.Main);
        Assert.AreEqual(GearLink.Fallback, reopened.Resolve(25, reopened.KitsOf(blues).Single(k => k.Label == "Home"), GearPart.Gloves).Link);
        Assert.AreEqual(0x12192E, reopened.Resolve(25, reopened.KitsOf(blues).Single(k => k.Label == "Home"), GearPart.Gloves).Row!.Main);
    }
    [TestMethod]
    public void PreviewFindsGearModelsAndTintsByZone()
    {
        if (_vfs == null)
            Assert.Inconclusive("NHL 19 is not next to the repository and BLUELINE_GAME is not set.");
        var paths = _vfs.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var gloves = GearPreview.Models(GearPart.Gloves, paths);
        var helmets = GearPreview.Models(GearPart.Helmet, paths);
        Assert.IsTrue(gloves.Any(m => m.Name == GearPreview.DefaultGloves));
        Assert.IsTrue(helmets.Any(m => m.Name == GearPreview.DefaultHelmet));
        Assert.IsTrue(helmets.All(m => m.Pieces.Count == 1) && gloves.All(m => m.Pieces.Count == 2));

        // One pixel fully in zone 2 (green channel of tm1), no detail, no ambient: it takes zone 2's colour.
        var detail = new Formats.Textures.RgbaImage(1, 1, [0, 0, 0, 0]);
        var mask = new Formats.Textures.RgbaImage(1, 1, [0, 255, 0, 255]);
        var zones = Enumerable.Repeat(0x111111, GearColours.ZoneCount).ToArray();
        zones[1] = 0x0072CE;
        var tinted = GearPreview.Tint(detail, null, [mask], zones);
        CollectionAssert.AreEqual(new byte[] { 0x00, 0x72, 0xCE, 255 }, tinted.Pixels);
    }

    [TestMethod]
    public void ZoneMapFindsTheCcmLogoAsZoneSeven()
    {
        if (_vfs == null)
            Assert.Inconclusive("NHL 19 is not next to the repository and BLUELINE_GAME is not set.");
        // Seen in game: zone 7 of the CCM 4rollpro glove is its logo, a small patch of the texture.
        var model = new GearModel(GearPart.Gloves, GearPreview.DefaultGloves);
        var prefix = model.Pieces[0].Prefix;
        var masks = Enumerable.Range(1, 4).Select(n => Read(GearModel.MaskMap(prefix, n)) is { } data
            && Formats.Textures.GameTexture.TryOpen(data) is { Blocker: null } texture ? texture.DecodeMip(0) : null).ToList();
        Assert.IsNotNull(masks[0]);
        var map = GearZoneMap.Build(masks);
        Assert.IsTrue(map.Coverage.TryGetValue(7, out var logo) && logo is > 0 and < 0.1, $"zone 7 covers {logo:P1}");
        Assert.IsTrue(map.Coverage.ContainsKey(1), "the glove has a main zone");
    }

    [TestMethod]
    public void HighlightKeepsTheZoneAndDimsTheRest()
    {
        // Two pixels: the left in zone 1 (red of tm1), the right in zone 2 (green). Zone 2 is highlighted.
        var detail = new Formats.Textures.RgbaImage(2, 1, new byte[8]);
        var mask = new Formats.Textures.RgbaImage(2, 1, [255, 0, 0, 255, 0, 255, 0, 255]);
        var zones = Enumerable.Repeat(0xC00000, GearColours.ZoneCount).ToArray();
        zones[1] = 0x00C000;
        var map = GearZoneMap.Build([mask]);
        Assert.AreEqual(1, map.ZoneAt(0.25, 0.5));
        Assert.AreEqual(2, map.ZoneAt(0.75, 0.5));
        Assert.AreEqual(2, map.ZoneAt(1.75, -0.5), "coordinates wrap");
        var tinted = GearPreview.Tint(detail, null, [mask], zones, highlight: 2, map: map, outline: 0xFFD54A);
        Assert.IsTrue(tinted.Pixels[0] < 0x80 && tinted.Pixels[0] == tinted.Pixels[1], "zone 1 goes to a dim grey");
        // A one-pixel zone is all edge, so it takes the outline colour.
        CollectionAssert.AreEqual(new byte[] { 0xFF, 0xD5, 0x4A, 255 }, tinted.Pixels[4..8]);
    }
}
