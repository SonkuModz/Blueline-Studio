using System.Text.Json;
using Blueline.Core;
using Blueline.Core.Teams;

namespace Blueline.Tests;

/// <summary>Reading a "CUSA11126-mods" folder laid over the game, as shadPS4 does. Read-only on both folders.</summary>
[TestClass]
public sealed class ModsFolderTests
{
    private static string MakeFolder(string root, params string[] files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
        }
        return root;
    }

    [TestMethod]
    public void TheModsFolderAddsArchivesAndItsCopiesWin()
    {
        var temp = Path.Combine(Path.GetTempPath(), "blueline-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            var game = MakeFolder(Path.Combine(temp, "CUSA11126"), "eboot.bin", "databigs/boot.big", "databigs/inxp_00.big", "databigs/patch_inxp_56.big", "databigs/game_60.big", "databigs/patch_game_96.big");
            MakeFolder(game + "-mods", "databigs/boot.big", "databigs/patch_inxp_56.big", "databigs/patch_inxp_57.big", "databigs/patch_game_97.big", "databigs/patch_game_99_mine.big");

            var install = GameInstall.Open(game);
            Assert.AreEqual(game + "-mods", install.ModsFolder);
            CollectionAssert.AreEqual(new[] { "boot.big", "inxp_00.big", "patch_inxp_56.big", "patch_inxp_57.big", "game_60.big", "patch_game_96.big", "patch_game_97.big" }, install.StockMountOrder().ToArray());
            CollectionAssert.AreEqual(new[] { "patch_game_97.big", "patch_inxp_57.big" }, install.ArchivesAddedByMods.ToArray());
            CollectionAssert.AreEqual(new[] { "boot.big", "patch_inxp_56.big" }, install.ArchivesReplacedByMods.ToArray());
            Assert.AreEqual(Path.Combine(game + "-mods", "databigs", "boot.big"), install.ArchivePath("boot.big"));
            Assert.AreEqual(Path.Combine(game, "databigs", "inxp_00.big"), install.ArchivePath("inxp_00.big"));

            var stock = GameInstall.Open(game, useModsFolder: false);
            Assert.IsNull(stock.ModsFolder);
            Assert.AreEqual(Path.Combine(game, "databigs", "boot.big"), stock.ArchivePath("boot.big"));
            CollectionAssert.DoesNotContain(stock.StockMountOrder().ToList(), "patch_inxp_57.big");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [TestMethod]
    public void AGameWithoutAModsFolderIsUnchanged()
    {
        var temp = Path.Combine(Path.GetTempPath(), "blueline-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            var game = MakeFolder(temp, "eboot.bin", "databigs/boot.big");
            var install = GameInstall.Open(game);
            Assert.IsNull(install.ModsFolder);
            Assert.IsEmpty(install.ArchivesAddedByMods);
            Assert.IsEmpty(install.ArchivesReplacedByMods);
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [TestMethod]
    public void EveryFileARealModInstalledIsWhatStudioShows()
    {
        string? game = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); game == null && d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
            if (File.Exists(Path.Combine(candidate, "eboot.bin")))
                game = candidate;
        }
        var manifest = game + "-mods/overtime-patch.json";
        if (game == null || !File.Exists(manifest))
        {
            Assert.Inconclusive("No installed mods folder with a manifest to check against.");
            return;
        }

        using var modded = VirtualFileSystem.Mount(GameInstall.Open(game));
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var checkedFiles = 0;
        foreach (var archive in document.RootElement.GetProperty("files").EnumerateObject())
            foreach (var entry in archive.Value.GetProperty("entries").EnumerateArray())
            {
                var path = entry.ValueKind == JsonValueKind.String ? entry.GetString()! : entry.GetProperty("path").GetString()!;
                var file = modded.Find(path);
                Assert.IsNotNull(file, path);
                Assert.IsTrue(file.IsFromModsFolder, $"{path} came from {file.SourceName}");
                checkedFiles++;
            }
        Assert.IsGreaterThan(1000, checkedFiles);

        // A renamed club keeps its files, and it is the art id that says so.
        var catalog = TeamCatalog.Load(p => modded.Find(p) is { } f ? modded.ReadContent(f) : null, modded.Files.Select(f => f.Path).ToList());
        Assert.AreEqual("arizona", catalog.Teams.First(t => t.ArtId == 22 && t.LeagueId == 0).Slug);
        foreach (var kraken in catalog.Teams.Where(t => t.ArtId == 10499 && t.LeagueId == 0))
            Assert.AreEqual("pre_order", kraken.Slug);
        Assert.IsTrue(catalog.ArtOf(catalog.Teams.First(t => t.ArtId == 27 && t.LeagueId == 0)).Any(a => a.Category == TeamArtCategory.CenterIce));
    }
}
