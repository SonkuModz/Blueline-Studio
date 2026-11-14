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
}
