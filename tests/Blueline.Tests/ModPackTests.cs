using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Blueline.Core;
using Blueline.Core.Packs;
using Blueline.Core.Teams;
using Blueline.Formats.Eb;
using Blueline.Formats.Sony;
using Blueline.Formats.Tdb;
using Blueline.Formats.Textures;

namespace Blueline.Tests;

/// <summary>
/// Mod packs end to end on a small fake game built with the real archive writer: export, the pack's safety checks, install,
/// conflicts and load order, other mods, and uninstall back to stock. Roster tests copy the player's own save into a temporary
/// folder when it exists; the real saves are only read.
/// </summary>
[TestClass]
public sealed class ModPackTests
{
    private string _temp = null!;
    private string _game = null!;

    private static string RealSaveData => RosterSync.SaveDataFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "shadPS4"));

    [TestInitialize]
    public void Setup()
    {
        _temp = Directory.CreateTempSubdirectory("blueline-packs-").FullName;
        _game = Path.Combine(_temp, "NHL 19", "CUSA11126");
        var databigs = Directory.CreateDirectory(Path.Combine(_game, "databigs")).FullName;
        File.WriteAllBytes(Path.Combine(_game, "eboot.bin"), [1]);
        EbWriter.Write(Path.Combine(databigs, "boot.big"), [Item("nhl/boot/clock.txt", "clock")]);
        EbWriter.Write(Path.Combine(databigs, "inxp_00.big"), [Item("nhl/ui/a.txt", "stock a"), Item("nhl/ui/b.txt", "stock b"), Item("nhl/data/x.db", "stock db")]);
        EbWriter.Write(Path.Combine(databigs, "game_00.big"), [Item("nhl/game/g.txt", "stock g")]);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_temp, true);

    private static EbWriteItem Item(string path, string text) => new(path, () => Encoding.UTF8.GetBytes(text));

    private GameInstall Install => GameInstall.Open(_game, useModsFolder: false);

    private VirtualFileSystem Mount() => VirtualFileSystem.Mount(GameInstall.Open(_game));

    private string[] Databigs() => Directory.GetFiles(Path.Combine(_game, "databigs")).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

    private ModProject Project(string name, params (string Path, string Text)[] files)
    {
        var project = ModProject.Create(Path.Combine(_temp, "projects", name), name, "Tester");
        project.SetMany([.. files.Select(f => (f.Path, Encoding.UTF8.GetBytes(f.Text), "edit"))], "edits");
        return project;
    }

    private string Export(ModProject project, PackExportOptions? options = null)
    {
        using var vfs = Mount();
        var path = Path.Combine(_temp, project.Slug + ".blpack");
        PackExporter.Export(project, vfs, options ?? new PackExportOptions(), path);
        return path;
    }

    private static HashSet<string> Entries(string archive)
    {
        using var a = EbArchive.Open(archive);
        return a.Entries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
    }

    private static byte[] Png(int width, int height, byte red)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 3]) = (red, 255);
        return PngFile.Write(new RgbaImage(width, height, pixels));
    }

    [TestMethod]
    public void Export_HoldsOnlyChanges_WithStockHashes_AndLeavesOutBootAndSameAsStock()
    {
        var project = Project("Overhaul", ("nhl/ui/a.txt", "new a"), ("nhl/ui/b.txt", "stock b"), ("nhl/ui/new.txt", "brand new"), ("nhl/boot/clock.txt", "clock 2"));
        var path = Export(project, new PackExportOptions { Version = "1.2.0", Description = "Test", Cover = new(Png(1920, 1080, 200)), Icon = new(Png(300, 200, 10)), Shots = [new(Png(640, 360, 99))] });

        var manifest = ModPack.ReadManifest(path);
        Assert.AreEqual("overhaul", manifest.Slug);
        Assert.AreEqual("1.2.0", manifest.Version);
        CollectionAssert.AreEqual(new[] { "nhl/ui/a.txt", "nhl/ui/new.txt" }, manifest.Files.Select(f => f.Path).ToArray());
        Assert.AreEqual(ModPack.Sha256(Encoding.UTF8.GetBytes("stock a")), manifest.Files[0].Stock);
        Assert.IsNull(manifest.Files[1].Stock, "A new path has no stock file.");
        Assert.HasCount(1, manifest.LeftOut);
        StringAssert.Contains(manifest.LeftOut[0], "boot.big");

        using (var zip = ZipFile.OpenRead(path))
            Assert.AreEqual(2, zip.Entries.Count(e => e.FullName.StartsWith("files/")), "Only changed content goes in, never whole archives.");
        var cover = PngFile.Read(ModPack.ReadArt(path, manifest.Art.Cover)!);
        var icon = PngFile.Read(ModPack.ReadArt(path, manifest.Art.Icon)!);
        Assert.AreEqual((1280, 720), (cover.Width, cover.Height));
        Assert.AreEqual((256, 256), (icon.Width, icon.Height));
        Assert.HasCount(1, manifest.Art.Shots);

        var again = Export(project, new PackExportOptions { Version = "1.3.0" });
        Assert.AreEqual(manifest.Id, ModPack.ReadManifest(again).Id, "The pack keeps its id across versions.");
    }

    [TestMethod]
    public void ReadManifest_RefusesUnsafeOrDamagedPacks()
    {
        var path = Export(Project("Safe", ("nhl/ui/a.txt", "new a")));
        string Variant(string name, Action<ZipArchive> change)
        {
            var copy = Path.Combine(_temp, name + ".blpack");
            File.Copy(path, copy);
            using (var zip = ZipFile.Open(copy, ZipArchiveMode.Update))
                change(zip);
            return copy;
        }
        void Rewrite(ZipArchive zip, Action<JsonNode> edit)
        {
            var entry = zip.GetEntry("manifest.json")!;
            JsonNode node;
            using (var s = entry.Open())
                node = JsonNode.Parse(s)!;
            entry.Delete();
            edit(node);
            using var w = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            w.Write(node.ToJsonString());
        }

        var traversal = Variant("traversal", z => z.CreateEntry("../evil.txt"));
        Assert.ThrowsExactly<PackFormatException>(() => ModPack.ReadManifest(traversal));

        var newer = Variant("newer", z => Rewrite(z, n => n["format"] = 2));
        StringAssert.Contains(Assert.ThrowsExactly<PackFormatException>(() => ModPack.ReadManifest(newer)).Message, "Update Blueline");

        var badPath = Variant("badpath", z => Rewrite(z, n => n["files"]![0]!["path"] = "../../eboot.bin"));
        Assert.ThrowsExactly<PackFormatException>(() => ModPack.ReadManifest(badPath));

        var damaged = Variant("damaged", z =>
        {
            var blob = z.Entries.First(e => e.FullName.StartsWith("files/"));
            var name = blob.FullName;
            var length = blob.Length;
            blob.Delete();
            using var s = z.CreateEntry(name).Open();
            s.Write(new byte[length]);
        });
        ModPack.ReadManifest(damaged);
        Assert.ThrowsExactly<PackFormatException>(() => ModPack.Unpack(damaged, Path.Combine(_temp, "unpacked")));
    }

    [TestMethod]
    public void InstallThenUninstall_LeavesTheGameExactlyAsStock()
    {
        var before = Databigs().ToDictionary(n => n, n => ModPack.Sha256(File.ReadAllBytes(Path.Combine(_game, "databigs", n))));
        var pack = Export(Project("Overhaul", ("nhl/ui/a.txt", "new a"), ("nhl/data/x.db", "new db"), ("nhl/game/g.txt", "new g")));

        using var vfs = Mount();
        var manager = new PackManager(Install);
        var check = manager.Check(pack, vfs);
        Assert.IsFalse(check.Blocked);
        Assert.IsEmpty(check.StockMismatches);
        var result = manager.Install(pack, vfs, new PackInstallOptions());

        CollectionAssert.AreEquivalent(new[] { "patch_game_99_pk-overhaul.big", "patch_inxp_99_pk-overhaul.big", "patch_inxp_99_pk-overhaul_db1.big" }, result.Archives.ToArray());
        Assert.IsTrue(Entries(Path.Combine(_game, "databigs", "patch_game_99_pk-overhaul.big")).Contains("nhl/game/g.txt"), "Game files go to the game family.");
        Assert.IsTrue(Directory.Exists(manager.Folder));

        var reopened = new PackManager(Install);
        Assert.HasCount(1, reopened.Packs);
        Assert.IsTrue(reopened.Uninstall("overhaul", vfs).Done);

        var after = Databigs().ToDictionary(n => n, n => ModPack.Sha256(File.ReadAllBytes(Path.Combine(_game, "databigs", n))));
        CollectionAssert.AreEquivalent(before.ToList(), after.ToList());
        Assert.IsFalse(Directory.Exists(manager.Folder), "Nothing of the manager is left beside the game.");
    }

    [TestMethod]
    public void TwoPacks_TheHigherWinsEachSharedFile_AndOrderAndDisableMoveIt()
    {
        var jerseys = Export(Project("Jerseys", ("nhl/ui/a.txt", "jerseys a"), ("nhl/ui/b.txt", "jerseys b")));
        var horns = Export(Project("Horns", ("nhl/ui/a.txt", "horns a")));
        string Archive(string slug) => Path.Combine(_game, "databigs", $"patch_inxp_99_pk-{slug}.big");

        using var vfs = Mount();
        var manager = new PackManager(Install);
        manager.Install(jerseys, vfs, new PackInstallOptions());
        var check = manager.Check(horns, vfs);
        Assert.HasCount(1, check.Conflicts);
        Assert.AreEqual("Horns", check.Conflicts[0].Winner, "A new pack goes on top.");
        manager.Install(horns, vfs, new PackInstallOptions());

        Assert.IsTrue(Entries(Archive("horns")).Contains("nhl/ui/a.txt"));
        CollectionAssert.AreEquivalent(new[] { "nhl/ui/b.txt" }, Entries(Archive("jerseys")).ToArray(), "The lower pack is rebuilt without the shared file.");

        manager.Move("jerseys", 0, vfs);
        CollectionAssert.AreEquivalent(new[] { "nhl/ui/a.txt", "nhl/ui/b.txt" }, Entries(Archive("jerseys")).ToArray());
        Assert.IsFalse(File.Exists(Archive("horns")), "Horns has nothing left to win, so it has no archive.");

        manager.SetEnabled("jerseys", false, vfs);
        Assert.IsFalse(File.Exists(Archive("jerseys")));
        Assert.IsTrue(Entries(Archive("horns")).Contains("nhl/ui/a.txt"), "With the top pack off, the next one gets the file.");

        manager.SetEnabled("jerseys", true, vfs);
        Assert.IsTrue(manager.Uninstall("jerseys", vfs).Done);
        Assert.IsTrue(manager.Uninstall("horns", vfs).Done);
        CollectionAssert.AreEqual(new[] { "boot.big", "game_00.big", "inxp_00.big" }, Databigs());
    }

    [TestMethod]
    public void OtherModArchives_KeepTheirFiles_UnlessThePlayerRemovesThem()
    {
        var project = Project("Local", ("nhl/ui/a.txt", "project a"));
        using var vfs = Mount();
        ModInstaller.Install(project, Install, OverlayBuilder.Build(project, vfs, project.BuildFolder));
        var pack = Export(Project("Pack", ("nhl/ui/a.txt", "pack a"), ("nhl/ui/b.txt", "pack b")));

        var manager = new PackManager(Install);
        var check = manager.Check(pack, vfs);
        Assert.HasCount(1, check.Others);
        Assert.AreEqual("patch_inxp_99_local.big", check.Others[0].Archive);
        manager.Install(pack, vfs, new PackInstallOptions());
        CollectionAssert.AreEquivalent(new[] { "nhl/ui/b.txt" }, Entries(Path.Combine(_game, "databigs", "patch_inxp_99_pk-pack.big")).ToArray());

        manager.Install(pack, vfs, new PackInstallOptions { RemoveOthers = ["patch_inxp_99_local.big"] });
        Assert.IsFalse(File.Exists(Path.Combine(_game, "databigs", "patch_inxp_99_local.big")));
        CollectionAssert.AreEquivalent(new[] { "nhl/ui/a.txt", "nhl/ui/b.txt" }, Entries(Path.Combine(_game, "databigs", "patch_inxp_99_pk-pack.big")).ToArray());
        Assert.HasCount(1, manager.Packs, "Installing the same pack again updates it in place.");
    }

    [TestMethod]
    public void Check_WarnsWhenThePlayersStockFileDiffers()
    {
        var pack = Export(Project("Pack", ("nhl/ui/a.txt", "pack a")));
        EbWriter.Write(Path.Combine(_game, "databigs", "inxp_00.big"), [Item("nhl/ui/a.txt", "another dump"), Item("nhl/ui/b.txt", "stock b"), Item("nhl/data/x.db", "stock db")]);
        using var vfs = Mount();
        var check = new PackManager(Install).Check(pack, vfs);
        CollectionAssert.AreEqual(new[] { "nhl/ui/a.txt" }, check.StockMismatches.ToArray());
        Assert.IsTrue(check.Lines.Any(l => l.Level == CheckLevel.Warning && l.Title.Contains("differ")));
        Assert.IsFalse(check.Blocked);
    }

    [TestMethod]
    public void StockHashes_ComeFromTheInstall_EvenWhenTheModsFolderReplacesAnArchiveByName()
    {
        // Puck Peak ships its own inxp_00.big, which hides the install's copy from a normal mount.
        var mods = Directory.CreateDirectory(Path.Combine(_temp, "NHL 19", "CUSA11126-mods", "databigs")).FullName;
        EbWriter.Write(Path.Combine(mods, "inxp_00.big"), [Item("nhl/ui/a.txt", "puck peak a"), Item("nhl/ui/b.txt", "stock b"), Item("nhl/data/x.db", "puck peak db")]);
        var pack = Export(Project("Pack", ("nhl/ui/a.txt", "pack a"), ("nhl/ui/b.txt", "stock b")));

        var manifest = ModPack.ReadManifest(pack);
        CollectionAssert.AreEqual(new[] { "nhl/ui/a.txt" }, manifest.Files.Select(f => f.Path).ToArray());
        Assert.AreEqual(ModPack.Sha256(Encoding.UTF8.GetBytes("stock a")), manifest.Files[0].Stock, "The install's file, not the mods folder's.");
        using var vfs = Mount();
        var check = new PackManager(Install).Check(pack, vfs);
        Assert.IsEmpty(check.StockMismatches);
        CollectionAssert.AreEqual(new[] { "nhl/ui/a.txt" }, check.OverModsFolder.ToArray());
    }

    [TestMethod]
    public void Builder_PutsColourCubesInTheirOwnArchive()
    {
        var project = Project("Grade", ("nhl/ui/a.txt", "xml"), ("nhl/postfx/lut/tier1.cube", "cube"));
        using var vfs = Mount();
        var build = OverlayBuilder.Build(project, vfs, project.BuildFolder);
        CollectionAssert.AreEquivalent(new[] { "patch_inxp_99_grade.big", "patch_inxp_99_grade_cube.big" }, build.Archives.Select(a => a.FileName).ToArray());
    }

    private string CopyRealRoster(string slot)
    {
        var source = Path.Combine(RealSaveData, slot);
        if (!RosterSave.IsRosterSave(source))
            Assert.Inconclusive($"There is no {slot} save in {RealSaveData}.");
        var copy = Path.Combine(_temp, "source-saves", slot);
        RosterSave.CopyFolder(source, copy);
        return copy;
    }

    [TestMethod]
    public void Roster_GoesInAFreeSlotNeverSlotZero_AndUninstallPutsItBack()
    {
        var source = CopyRealRoster("RosterCustom1");
        var pack = Export(Project("Roster Pack", ("nhl/ui/a.txt", "a")), new PackExportOptions { RosterFolder = source });
        var manifest = ModPack.ReadManifest(pack);
        Assert.IsNotNull(manifest.Roster);
        using (var zip = ZipFile.OpenRead(pack))
        using (var s = new MemoryStream())
        {
            zip.GetEntry("saves/roster/sce_sys/param.sfo")!.Open().CopyTo(s);
            var at = ParamSfo.Locate(s.ToArray(), "ACCOUNT_ID")!.Value;
            Assert.IsTrue(s.ToArray().AsSpan(at.Offset, at.Length).IndexOfAnyExcept((byte)0) < 0, "The account id is blanked.");
        }

        // The player's saves: Puck Peak's slot 0 and their own slot 1.
        var saveData = Path.Combine(_temp, "savedata", "CUSA11126");
        RosterSave.CopyFolder(source, Path.Combine(saveData, "RosterCustom0"));
        RosterSave.CopyFolder(source, Path.Combine(saveData, "RosterCustom1"));
        var slot0 = RosterSlots.HashFolder(Path.Combine(saveData, "RosterCustom0"));

        using var vfs = Mount();
        var manager = new PackManager(Install);
        Assert.ThrowsExactly<InvalidOperationException>(() => manager.Install(pack, vfs, new PackInstallOptions { SaveData = saveData, InstallRoster = true, Slot = "RosterCustom0" }));
        var installed = manager.Install(pack, vfs, new PackInstallOptions { SaveData = saveData, InstallRoster = true }).Pack;

        Assert.AreEqual("RosterCustom2", installed.Roster!.Slot);
        var placed = Path.Combine(saveData, "RosterCustom2");
        RosterSave.Read(placed);
        Assert.AreEqual("RosterCustom2", File.ReadAllText(Path.Combine(placed, "NAME")));
        Assert.AreEqual("RosterCustom2", Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(placed, "INDEX")), 0, 13));
        Assert.AreEqual("RosterCustom2", ParamSfo.Parse(File.ReadAllBytes(Path.Combine(placed, "sce_sys", "param.sfo")))["SAVEDATA_DIRECTORY"]);
        CollectionAssert.AreEquivalent(slot0.ToList(), RosterSlots.HashFolder(Path.Combine(saveData, "RosterCustom0")).ToList(), "Slot 0 is never touched.");

        // Saved over in game: uninstall stops and asks.
        File.WriteAllBytes(Path.Combine(placed, "sce_sys", "icon0.png"), [1, 2, 3]);
        var stopped = manager.Uninstall("roster-pack", vfs);
        Assert.IsFalse(stopped.Done);
        Assert.AreEqual(RosterRemoval.ChangedSinceInstall, stopped.Roster);
        Assert.IsTrue(Directory.Exists(placed));

        var done = manager.Uninstall("roster-pack", vfs, forceRoster: true);
        Assert.IsTrue(done.Done);
        Assert.AreEqual(RosterRemoval.Removed, done.Roster);
        Assert.IsFalse(Directory.Exists(placed));
        Assert.IsTrue(Directory.GetDirectories(RosterSlots.BackupFolder(saveData)).Any(d => d.Contains("RosterCustom2-") && d.EndsWith("-removed-roster-pack")), "The removed roster is kept as a backup.");
    }

    [TestMethod]
    public void Roster_IntoAnExistingSlot_IsBackedUpAndRestored()
    {
        var source = CopyRealRoster("RosterCustom1");
        var pack = Export(Project("Roster Pack", ("nhl/ui/a.txt", "a")), new PackExportOptions { RosterFolder = source });
        var saveData = Path.Combine(_temp, "savedata", "CUSA11126");
        RosterSave.CopyFolder(source, Path.Combine(saveData, "RosterCustom1"));
        File.WriteAllBytes(Path.Combine(saveData, "RosterCustom1", "sce_sys", "icon0.png"), [9, 9]);
        var before = RosterSlots.HashFolder(Path.Combine(saveData, "RosterCustom1"));

        using var vfs = Mount();
        var manager = new PackManager(Install);
        var roster = manager.Install(pack, vfs, new PackInstallOptions { SaveData = saveData, InstallRoster = true, Slot = "RosterCustom1" }).Pack.Roster!;
        Assert.IsNotNull(roster.Backup);
        Assert.AreEqual(RosterRemoval.Restored, manager.Uninstall("roster-pack", vfs).Roster);
        CollectionAssert.AreEquivalent(before.ToList(), RosterSlots.HashFolder(Path.Combine(saveData, "RosterCustom1")).ToList());
    }
}
