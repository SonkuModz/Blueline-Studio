using System.Text;
using Blueline.Core;
using Blueline.Formats.Eb;

namespace Blueline.Tests;

/// <summary>Opening a Puck Peak Overtime mod as a project, and building databases one per archive. Only temporary folders are written.</summary>
[TestClass]
public sealed class PuckPeakTests
{
    private const string Database = "nhl/data/common/gamemodes/database/t3db/nhl_game.db";
    private const string Strings = "nhl/data/kettle/loc/eng_us.db";
    private const string Texture = "nhl/data/kettle/rendering/characters/player/font_adidas_anaheim_home_cm.gnf";

    private static string? _game;
    private static VirtualFileSystem _vfs = null!;
    private string _temp = "";

    [ClassInitialize]
    public static void Mount(TestContext _)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); _game == null && d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
            if (File.Exists(Path.Combine(candidate, "eboot.bin")))
                _game = candidate;
        }
        if (_game != null)
            _vfs = VirtualFileSystem.Mount(GameInstall.Open(_game, useModsFolder: false));
    }

    [ClassCleanup]
    public static void Unmount() => _vfs?.Dispose();

    [TestInitialize]
    public void Start()
    {
        if (_game == null)
            Assert.Inconclusive("The game is not here.");
        _temp = Path.Combine(Path.GetTempPath(), "blueline-puckpeak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    [TestCleanup]
    public void Finish()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, true);
    }

    private static byte[] Changed(string path, byte mark)
    {
        var content = _vfs.ReadContent(_vfs.Find(path)!);
        content[^1] ^= mark;
        return content;
    }

    [TestMethod]
    public void ManifestFilesBecomeProjectChangesAndLaterArchivesWin()
    {
        var mods = Path.Combine(_temp, "CUSA11126-mods");
        var databigs = Directory.CreateDirectory(Path.Combine(mods, "databigs")).FullName;
        var early = Changed(Texture, 1);
        var late = Changed(Texture, 2);
        var database = Changed(Database, 3);
        EbWriter.Write(Path.Combine(databigs, "patch_inxp_56.big"), [new EbWriteItem(Database, () => database), new EbWriteItem(Strings, () => _vfs.ReadContent(_vfs.Find(Strings)!))]);
        EbWriter.Write(Path.Combine(databigs, "patch_inxp_57.big"), [new EbWriteItem(Texture, () => late)]);
        EbWriter.Write(Path.Combine(databigs, "inxp_00.big"), [new EbWriteItem(Texture, () => early)]);
        File.WriteAllText(Path.Combine(mods, PuckPeakMod.ManifestName), """
            { "version": 1, "applied": "2026-11-07T18:52:30", "files": {
              "databigs/patch_inxp_56.big": { "entries": ["nhl/data/common/gamemodes/database/t3db/nhl_game.db"] },
              "databigs/inxp_00.big": { "entries": ["nhl/data/kettle/rendering/characters/player/font_adidas_anaheim_home_cm.gnf"] },
              "databigs/patch_inxp_57.big": { "new": true, "entries": ["nhl/data/kettle/rendering/characters/player/font_adidas_anaheim_home_cm.gnf"] } } }
            """);

        var mod = PuckPeakMod.Open(mods);
        Assert.AreEqual(2, mod.FileCount);
        var project = ModProject.Create(Path.Combine(_temp, "project"), "Puck Peak Overtime");
        var result = mod.Copy(project);
        project.AddStored(result.Changes, "Open Puck Peak Overtime");

        Assert.AreEqual(2, project.ChangeCount);
        Assert.IsTrue(project.TryGet(Texture, out var texture));
        CollectionAssert.AreEqual(late, project.ReadBlob(texture), "patch_inxp_57 mounts after inxp_00, so its copy wins");
        Assert.IsTrue(project.TryGet(Database, out var db));
        CollectionAssert.AreEqual(database, project.ReadBlob(db));
        Assert.IsFalse(project.TryGet(Strings, out _), "only files the manifest lists are taken");
    }

    [TestMethod]
    public void EachDatabaseGetsItsOwnArchive()
    {
        var project = ModProject.Create(Path.Combine(_temp, "project"), "Two Databases");
        project.Set(Database, Changed(Database, 1), "db");
        project.Set(Strings, Changed(Strings, 1), "strings");
        project.Set(Texture, Changed(Texture, 1), "texture");

        var build = OverlayBuilder.Build(project, _vfs, project.BuildFolder);
        var inxp = build.Archives.Where(a => a.FileName.StartsWith("patch_inxp_99", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(2, inxp.Count, "the two databases each get an inxp archive");
        Assert.AreEqual(1, build.Archives.Count(a => a.FileName.StartsWith("patch_game_99", StringComparison.Ordinal)), "the texture stays in its game archive");
        foreach (var archive in inxp)
        {
            using var opened = EbArchive.Open(archive.Path);
            Assert.IsTrue(opened.Entries.Count(e => e.Path.EndsWith(".db", StringComparison.Ordinal)) <= 1, $"{archive.FileName} holds more than one database");
        }
        Assert.IsTrue(inxp.All(a => GameInstall.IsModArchive(a.FileName)));
    }
}
