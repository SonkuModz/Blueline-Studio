using System.Text;
using Blueline.Core;

namespace Blueline.Tests;

/// <summary>Adding one mod to another, against the real game. Only temporary folders are written.</summary>
[TestClass]
public sealed class ModMergeTests
{
    private const string Texture = "nhl/data/kettle/rendering/characters/player/font_adidas_anaheim_home_cm.gnf";
    private const string OtherTexture = "nhl/data/kettle/rendering/characters/player/font_adidas_toronto_home_cm.gnf";

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
        _temp = Path.Combine(Path.GetTempPath(), "blueline-merge-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Finish()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, true);
    }

    private ModProject NewProject(string name) => ModProject.Create(Path.Combine(_temp, name), name);

    private static byte[] Changed(string path, byte mark)
    {
        var content = _vfs.ReadContent(_vfs.Find(path)!);
        content[^1] ^= mark;
        return content;
    }

    private static void Take(MergePlan plan, ModProject project, IReadOnlyCollection<string> paths) =>
        project.AddStored(ModMerge.Store(plan, paths, project, _vfs), "Merged");

    [TestMethod]
    public void AProjectAddsWhatIsNewAndAsksAboutTheRest()
    {
        var mine = NewProject("Mine");
        var theirs = NewProject("Theirs");
        mine.Set(Texture, Changed(Texture, 1), "My jersey");
        mine.Set("nhl/blueline/same.txt", [1, 2], "Same");
        theirs.Set(Texture, Changed(Texture, 2), "Their jersey");
        theirs.Set(OtherTexture, Changed(OtherTexture, 2), "Their other jersey");
        theirs.Set("nhl/blueline/same.txt", [1, 2], "Same");
        theirs.Set("nhl/blueline/new.txt", [3], "New");
        var stockCopy = "nhl/data/kettle/rendering/characters/player/font_adidas_boston_home_cm.gnf";
        theirs.Set(stockCopy, _vfs.ReadContent(_vfs.Find(stockCopy)!), "Unchanged really");

        var plan = ModMerge.Analyze(theirs.Folder, mine, _vfs);
        Assert.AreEqual(MergeKind.Conflict, plan.Items.Single(i => i.Path == Texture).Kind);
        Assert.AreEqual(MergeKind.Replaces, plan.Items.Single(i => i.Path == OtherTexture).Kind);
        Assert.AreEqual(MergeKind.New, plan.Items.Single(i => i.Path.EndsWith("new.txt")).Kind);
        Assert.AreEqual(MergeKind.SameAsProject, plan.Items.Single(i => i.Path.EndsWith("same.txt")).Kind);
        Assert.AreEqual(MergeKind.SameAsGame, plan.Items.Single(i => i.Path == stockCopy).Kind);
        Assert.HasCount(2, plan.Default);
        Assert.AreEqual(2, mine.ChangeCount, "Comparing must not change the project.");

        var myJersey = mine.ReadBlob(Change(mine, Texture));
        Take(plan, mine, plan.Default);
        Assert.AreEqual(4, mine.ChangeCount);
        CollectionAssert.AreEqual(myJersey, mine.ReadBlob(Change(mine, Texture)), "A conflict stays mine unless chosen.");
        CollectionAssert.AreEqual(Changed(OtherTexture, 2), mine.ReadBlob(Change(mine, OtherTexture)));

        mine.Undo();
        Assert.AreEqual(2, mine.ChangeCount, "One merge is one undo step.");

        Take(ModMerge.Analyze(theirs.Folder, mine, _vfs), mine, [Texture]);
        CollectionAssert.AreEqual(Changed(Texture, 2), mine.ReadBlob(Change(mine, Texture)));
        Assert.AreEqual(5, theirs.ChangeCount, "The other mod is only read.");
    }

    [TestMethod]
    public void ABuiltArchiveAddsTheSameContentAndBuilds()
    {
        var theirs = NewProject("Theirs");
        var content = Changed(OtherTexture, 3);
        theirs.Set(OtherTexture, content, "Their jersey");
        theirs.Set("nhl/blueline/new.txt", [3], "New");
        var built = OverlayBuilder.Build(theirs, _vfs, theirs.BuildFolder);

        // The folder of archives gives both files; a single archive gives what that archive holds.
        foreach (var (source, files) in new[] { (theirs.BuildFolder, 2), (built.Archives[0].Path, built.Archives[0].Files) })
        {
            var mine = NewProject("Mine" + Guid.NewGuid().ToString("N"));
            var plan = ModMerge.Analyze(source, mine, _vfs);
            Assert.HasCount(files, plan.Default);
            Take(plan, mine, plan.Default);
            if (source == theirs.BuildFolder)
                CollectionAssert.AreEqual(content, mine.ReadBlob(Change(mine, OtherTexture)), "A chunkzip file arrives unwrapped, as a project stores it.");
            Assert.IsNotEmpty(OverlayBuilder.Build(mine, _vfs, mine.BuildFolder).Archives);
        }
    }

    [TestMethod]
    public void InAFolderTheLaterArchiveWins()
    {
        var first = NewProject("A");
        first.Set("nhl/blueline/x.txt", Encoding.UTF8.GetBytes("early"), "x");
        var second = NewProject("B");
        second.Set("nhl/blueline/x.txt", Encoding.UTF8.GetBytes("late"), "x");
        var folder = Directory.CreateDirectory(Path.Combine(_temp, "pack", "databigs")).FullName;
        foreach (var (project, name) in new[] { (first, "patch_inxp_10.big"), (second, "patch_inxp_20.big") })
            File.Copy(OverlayBuilder.Build(project, _vfs, project.BuildFolder).Archives[0].Path, Path.Combine(folder, name));

        var mine = NewProject("Mine");
        var plan = ModMerge.Analyze(Path.GetDirectoryName(folder)!, mine, _vfs);
        Take(plan, mine, plan.Default);
        Assert.AreEqual("late", Encoding.UTF8.GetString(mine.ReadBlob(Change(mine, "nhl/blueline/x.txt"))));
    }

    [TestMethod]
    public void ItRefusesWhatItCannotMerge()
    {
        var mine = NewProject("Mine");
        Assert.ThrowsExactly<MergeRefusedException>(() => ModMerge.Analyze(mine.Folder, mine, _vfs));
        Assert.ThrowsExactly<MergeRefusedException>(() => ModMerge.Analyze(Path.Combine(_temp, "missing"), mine, _vfs));
        Assert.ThrowsExactly<MergeRefusedException>(() => ModMerge.Analyze(Directory.CreateDirectory(Path.Combine(_temp, "empty")).FullName, mine, _vfs));

        using var modded = VirtualFileSystem.Mount(GameInstall.Open(_game!));
        if (modded.Install.ModsFolder is { } mods)
        {
            Assert.ThrowsExactly<MergeRefusedException>(() => ModMerge.Analyze(mods, mine, modded));
            Assert.ThrowsExactly<MergeRefusedException>(() => ModMerge.Analyze(Path.Combine(mods, "databigs", "patch_inxp_57.big"), mine, modded));
        }
    }

    [TestMethod]
    public void AModThatChangedAfterComparingIsNotAdded()
    {
        var theirs = NewProject("Theirs");
        theirs.Set("nhl/blueline/a.txt", [1], "A");
        var mine = NewProject("Mine");
        var plan = ModMerge.Analyze(theirs.Folder, mine, _vfs);
        theirs.Set("nhl/blueline/a.txt", [2], "A again");

        Assert.ThrowsExactly<MergeRefusedException>(() => ModMerge.Store(plan, plan.Default, mine, _vfs));
        Assert.AreEqual(0, mine.ChangeCount);
    }

    [TestMethod]
    public void ARealModArchiveIsCutDownToWhatItChanged()
    {
        var real = _game + "-mods/databigs/patch_inxp_56.big";
        if (!File.Exists(real))
            Assert.Inconclusive("No mods folder here.");

        var plan = ModMerge.Analyze(real, NewProject("Mine"), _vfs);
        Assert.IsNotEmpty(plan.Items);
        Assert.AreEqual(0, plan.Count(MergeKind.Conflict));
        Assert.IsGreaterThan(0, plan.Count(MergeKind.SameAsGame) + plan.Takeable.Count());
        Console.WriteLine($"{plan.Items.Count} files: {plan.Count(MergeKind.Replaces)} replace, {plan.Count(MergeKind.New)} new, {plan.Count(MergeKind.SameAsGame)} same as the game");
    }

    private static ProjectChange Change(ModProject project, string path)
    {
        Assert.IsTrue(project.TryGet(path, out var change), path);
        return change;
    }
}
