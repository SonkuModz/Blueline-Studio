using System.Text;
using Blueline.Core;
using Blueline.Core.Teams;
using Blueline.Formats.Tdb;

namespace Blueline.Tests;

/// <summary>A team's uniform slots (the stockteamjerseys table) and adding one on a free kit name, checked against the real
/// game (docs/UNIFORM_SLOTS.md). Read-only on the install.</summary>
[TestClass]
public sealed class UniformSlotTests
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

    private static TdbFile OpenDb()
    {
        if (_vfs == null)
            Assert.Inconclusive("NHL 19 is not next to the repository and BLUELINE_GAME is not set.");
        var file = TdbFile.Parse(Read(TeamCatalog.DatabasePath)!);
        file.ApplyMeta(Encoding.UTF8.GetString(Read(TeamCatalog.MetaPath)!));
        return file;
    }

    private static (UniformSlots Slots, GearColours Gear) Open()
    {
        var file = OpenDb();
        return (UniformSlots.Open(file, _catalog!, Read(UniformSlots.VaultPath)), GearColours.Open(file));
    }

    private static TeamInfo Team(string name) => _catalog!.Teams.Single(t => t.Name == name && t.LeagueId == 0);

    [TestMethod]
    public void TorontoHas21SlotsInMenuOrder()
    {
        var (slots, _) = Open();
        var list = slots.SlotsOf(Team("Toronto Maple Leafs"));
        Assert.AreEqual(21, list.Count);
        CollectionAssert.AreEqual(Enumerable.Range(0, 21).ToArray(), list.Select(s => s.Order).ToArray());
        Assert.AreEqual("adidas_toronto_home", list[0].KitName);
        Assert.IsTrue(list[0].IsDefault);
        Assert.AreEqual("adidas_toronto_away", list[1].KitName);
        Assert.IsTrue(list.All(slots.IsDrawable), "every stock Leafs kit has a texture pack");
        Assert.IsFalse(list.Any(s => s.KitName == "rbk_toronto_five"));
    }

    [TestMethod]
    public void FreeKitsStartWithTheTeamsOwnUnusedKit()
    {
        var (slots, _) = Open();
        var leafs = Team("Toronto Maple Leafs");
        var free = slots.FreeKits(leafs);
        Assert.AreEqual("rbk_toronto_five", free[0].Name);
        Assert.AreEqual(FreeKitSource.OwnUnused, free[0].Source);
        var twelve = free.Single(f => f.Name == "adidas_pre_order_twelve");
        Assert.AreEqual(900, twelve.ArtId);
        Assert.AreEqual(FreeKitSource.Unused, twelve.Source);
        Assert.IsTrue(free.Where(f => f.Slug == "pre_order" && f.Variant < 12).All(f => f.Source == FreeKitSource.HiddenTeam), "HUT wears pre_order 0-11");
        Assert.IsFalse(free.Any(f => f.Name == "adidas_toronto_home"));
    }

    [TestMethod]
    public void AddWritesTheRowAndGearRowsAndSurvivesASave()
    {
        var (slots, gear) = Open();
        var leafs = Team("Toronto Maple Leafs");
        var spare = slots.SpareRows;
        var gearSpare = gear.SpareRows;
        var kit = slots.FreeKits(leafs).Single(f => f.Name == "adidas_pre_order_twelve");

        var added = slots.Add(leafs, kit, locString: 6, position: 3, gear: gear);
        Assert.AreEqual(spare - 1, slots.SpareRows);
        Assert.AreEqual(21, added.JerseyIndex);
        Assert.AreEqual(3, added.Order);
        Assert.AreEqual(900, added.ArtId);
        Assert.AreEqual("adidas_pre_order_twelve", added.KitName);
        Assert.IsFalse(added.IsDefault);
        Assert.AreEqual(gearSpare - 2, gear.SpareRows, "one gloves row and one helmet row");
        var gearKit = gear.KitsOf(leafs).Single(k => k.Variant == 12);
        Assert.AreEqual(GearLink.Own, gear.Resolve(28, gearKit, GearPart.Gloves).Link);
        Assert.AreEqual(GearLink.Own, gear.Resolve(28, gearKit, GearPart.Helmet).Link);
        Assert.ThrowsExactly<InvalidOperationException>(() => slots.Add(leafs, kit), "a name is taken once");

        var size = slots.File.Data.Length;
        var data = slots.Save();
        Assert.AreEqual(size, data.Length);
        var file = TdbFile.Parse(data);
        Assert.IsTrue(file.VerifyCrcs().All(c => c.Matches));
        file.ApplyMeta(Encoding.UTF8.GetString(Read(TeamCatalog.MetaPath)!));
        var reopened = UniformSlots.Open(file, _catalog!, Read(UniformSlots.VaultPath)).SlotsOf(leafs);
        Assert.AreEqual(22, reopened.Count);
        Assert.AreEqual("adidas_pre_order_twelve", reopened[3].KitName);
        CollectionAssert.AreEqual(Enumerable.Range(0, 22).ToArray(), reopened.Select(s => s.Order).ToArray());
    }

    [TestMethod]
    public void MoveRenumbersTheMenu()
    {
        var (slots, _) = Open();
        var leafs = Team("Toronto Maple Leafs");
        var last = slots.SlotsOf(leafs)[^1];
        var moved = slots.Move(last, 2);
        var list = slots.SlotsOf(leafs);
        Assert.AreEqual(2, moved.Order);
        Assert.AreEqual(last.KitName, list[2].KitName);
        CollectionAssert.AreEqual(Enumerable.Range(0, 21).ToArray(), list.Select(s => s.Order).ToArray());
    }

    [TestMethod]
    public void RemoveCompactsJerseyNumbersAndTheMenu()
    {
        var (slots, _) = Open();
        var leafs = Team("Toronto Maple Leafs");
        var other = Team("Montreal Canadiens");
        var otherBefore = slots.SlotsOf(other).Select(s => (s.KitName, s.JerseyIndex, s.Order)).ToList();
        var third = slots.SlotsOf(leafs).Single(s => s.KitName == "adidas_toronto_two");

        slots.Remove(third);
        var list = slots.SlotsOf(leafs);
        Assert.AreEqual(20, list.Count);
        Assert.IsFalse(list.Any(s => s.KitName == "adidas_toronto_two"));
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 20).ToArray(), list.Select(s => s.JerseyIndex).ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(0, 20).ToArray(), list.Select(s => s.Order).ToArray());
        CollectionAssert.AreEqual(otherBefore, slots.SlotsOf(other).Select(s => (s.KitName, s.JerseyIndex, s.Order)).ToList());
        Assert.IsTrue(slots.FreeKits(leafs).Any(f => f.Name == "adidas_toronto_two" && f.Source == FreeKitSource.OwnUnused));
    }

    [TestMethod]
    public void ArtCoversEveryFileTheBorrowedNameHas()
    {
        var (slots, _) = Open();
        var leafs = Team("Toronto Maple Leafs");
        var kit = slots.FreeKits(leafs).Single(f => f.Name == "adidas_pre_order_twelve");
        var home = slots.SlotsOf(leafs)[0];
        var files = UniformSlotArt.Make(_vfs!, kit, new SlotArtOptions(CopyFrom: home));
        var expected = UniformSlotArt.FilesOf(kit.Name, kit.Style, kit.ArtId, kit.Variant).Where(p => _vfs!.Find(p) != null).ToList();
        Assert.IsTrue(expected.Count >= 12, $"{expected.Count} files");
        CollectionAssert.AreEquivalent(expected, files.Where(f => f.Content != null).Select(f => f.Path).ToList(),
            string.Join("\n", files.Where(f => f.Content == null).Select(f => $"{f.Path}: {f.Note}")));
        Assert.ThrowsExactly<InvalidOperationException>(() => UniformSlotArt.Make(_vfs!, kit, new SlotArtOptions(CopyFrom: slots.SlotsOf(leafs).First(s => s.Style == 1))));
    }

    [TestMethod]
    public void RosterSyncAddsTheNewJerseyRowByKey()
    {
        var game = OpenDb();
        var (slots, gear) = Open();
        var leafs = Team("Toronto Maple Leafs");
        slots.Add(leafs, slots.FreeKits(leafs).Single(f => f.Name == "adidas_pre_order_twelve"), gear: gear);
        slots.Save();
        var plan = RosterSync.Plan(game, slots.File, OpenDb());
        Assert.AreEqual(1, plan.Adds.Count(a => a.Table == UniformSlots.TableName));
        Assert.AreEqual(2, plan.Adds.Count(a => a.Table == GearColours.TableName));
    }

    [TestMethod]
    public void KitNamesFindStockKeysWhateverTheirCaseAndAddNewOnes()
    {
        if (_vfs == null)
            Assert.Inconclusive("NHL 19 is not next to the repository and BLUELINE_GAME is not set.");
        var names = new LocDictionary(Read(SlotNames.DictionaryPath)!);
        // Stock key spelled vintage_2_27_5 (lower case); the game builds VINTAGE_2_27_5. Seen in game as the kit's name.
        Assert.AreEqual("The Digital 6 Created by Adidas", SlotNames.NameOf(names, 2, 27, 5));
        Assert.IsNull(SlotNames.NameOf(names, 2, 900, 12));

        Assert.IsFalse(SlotNames.SetName(names, 2, 27, 5, "Digital 6"), "a stock name is changed in place");
        Assert.IsTrue(SlotNames.SetName(names, 2, 900, 12, " Green Test Kit "), "a new slot gets a new key");
        Assert.ThrowsExactly<ArgumentException>(() => SlotNames.SetName(names, 2, 900, 12, " "));

        var reopened = new LocDictionary(names.Save());
        Assert.AreEqual("Digital 6", SlotNames.NameOf(reopened, 2, 27, 5));
        Assert.AreEqual("Green Test Kit", SlotNames.NameOf(reopened, 2, 900, 12));
        Assert.AreEqual(names.Rows.Count, reopened.Rows.Count);
    }
}
