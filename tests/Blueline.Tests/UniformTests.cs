using Blueline.Core;
using Blueline.Core.Teams;
using Blueline.Formats.Models;
using Blueline.Formats.Textures;

namespace Blueline.Tests;

/// <summary>Uniform sheets, the meshes they go on, and vertex fields split across streams, on the real game.</summary>
[TestClass]
public sealed class UniformTests
{
    private static VirtualFileSystem _vfs = null!;
    private static TeamCatalog _catalog = null!;
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void Mount(TestContext _)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && _vfs == null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
            if (File.Exists(Path.Combine(candidate, "eboot.bin")))
                _vfs = VirtualFileSystem.Mount(GameInstall.Open(candidate, useModsFolder: false));
        }
        if (_vfs != null)
            _catalog = TeamCatalog.Load(p => _vfs.Find(p) is { } f ? _vfs.ReadContent(f) : null, _vfs.Files.Select(f => f.Path).ToList());
    }

    [ClassCleanup]
    public static void Unmount() => _vfs?.Dispose();

    [TestInitialize]
    public void Check()
    {
        if (_vfs == null)
            Assert.Inconclusive("The game is not here.");
    }

    private static McdFile Model(string path) => McdFile.Parse(_vfs.ReadContent(_vfs.Find(path) ?? throw new AssertFailedException($"No such model: {path}")));

    [TestMethod]
    public void EveryKitOptionHasItsMeshes()
    {
        foreach (var brand in new[] { "adidas", "rbk", "nineties" })
            foreach (var size in Enum.GetValues<UniformSize>())
                foreach (var tuck in Enum.GetValues<UniformTuck>())
                    foreach (var pants in Enum.GetValues<UniformPants>())
                    {
                        var models = UniformKits.ModelsFor(brand, size, tuck, pants);
                        foreach (var path in new[] { models.Jersey, models.JerseyTie, models.Pant, models.Sock }.Concat(models.Gear))
                            Assert.IsNotNull(_vfs.Find(path), path);
                    }
    }

    [TestMethod]
    public void EveryNhlClubHasFullKits()
    {
        var clubs = _catalog.Teams.Where(t => t.LeagueId == 0 && t.Slug is { Length: > 0 } && !t.Name.Contains("All-Stars", StringComparison.Ordinal)).ToList();
        var total = 0;
        var incomplete = new List<string>();
        foreach (var club in clubs)
        {
            var kits = UniformKits.From(_catalog.ArtOf(club));
            total += kits.Count;
            incomplete.AddRange(kits.Where(k => k.Pant == null || k.Sock == null).Select(k => $"{club.Name} {k.Key}"));
            if (club.ArtId is not 10499)
                Assert.IsTrue(kits.Any(k => k.Brand == "adidas" && k.Kit == "home"), $"{club.Name} has no Adidas home kit");
        }
        TestContext.WriteLine($"{clubs.Count} clubs, {total} kits, {incomplete.Count} without a pant or sock sheet");
        foreach (var kit in incomplete.Take(30))
            TestContext.WriteLine("  " + kit);
        Assert.IsGreaterThan(30, clubs.Count);
    }

    [TestMethod]
    public void EverySheetOfAKitFindsIt()
    {
        var toronto = _catalog.Teams.First(t => t.Name == "Toronto Maple Leafs");
        var kits = UniformKits.From(_catalog.ArtOf(toronto));
        const string folder = "nhl/data/kettle/rendering/characters/player/";
        foreach (var sheet in new[] { "jersey_adidas_toronto_home_cm", "jersey_adidas_toronto_home_nm", "pant_adidas_toronto_home_cm", "sock_adidas_toronto_home_cm", "font_adidas_toronto_home_cm", "name_adidas_toronto_home" })
            Assert.AreEqual("adidas_toronto_home", UniformKits.KitOf(kits, $"{folder}{sheet}.gnf")?.Key, sheet);
        Assert.IsNull(UniformKits.KitOf(kits, $"{folder}helmetlogo_toronto_cm.gnf"));
    }

    [TestMethod]
    public void UniformMeshesHaveUvsOnTheSheet()
    {
        foreach (var brand in new[] { "adidas", "rbk", "nineties" })
        {
            var models = UniformKits.ModelsFor(brand, UniformSize.Large, UniformTuck.None, UniformPants.Long);
            foreach (var path in new[] { models.Jersey, models.JerseyTie, models.Pant, models.Sock })
            {
                var model = Model(path);
                Assert.IsNotEmpty(model.Shapes, path);
                foreach (var shape in model.Shapes)
                {
                    var uvs = model.ReadUvs(shape);
                    Assert.IsNotNull(uvs, $"{path} has no UVs");
                    Assert.HasCount(model.Streams[shape.Stream].Count, uvs, path);
                    Assert.IsTrue(uvs.All(t => t.X is >= -0.01f and <= 1.01f && t.Y is >= -0.01f and <= 1.01f), $"{path} has UVs off the sheet");
                    // The lace patch uses a small corner of the jersey sheet; the rest spread across theirs.
                    if (path != models.JerseyTie)
                        Assert.IsGreaterThan(0.3f, uvs.Max(t => t.X) - uvs.Min(t => t.X), $"{path} has UVs that barely move");
                }
            }
        }
    }

    [TestMethod]
    public void PantsSitBetweenJerseyAndSocks()
    {
        var models = UniformKits.ModelsFor("adidas", UniformSize.Large, UniformTuck.None, UniformPants.Long);
        (float Low, float High) Height(string path)
        {
            var model = Model(path);
            var ys = model.Shapes.SelectMany(s => model.ReadPositions(model.Streams[s.Stream])).Select(p => p.Y).ToList();
            return (ys.Min(), ys.Max());
        }
        var (jersey, pant, sock) = (Height(models.Jersey), Height(models.Pant), Height(models.Sock));
        Assert.IsTrue(sock.Low < pant.Low && pant.Low < jersey.Low && pant.High < jersey.High, $"sock {sock}, pant {pant}, jersey {jersey}");
        Assert.IsGreaterThan(pant.Low, sock.High, "Socks should reach up inside the pants.");
        Assert.IsGreaterThan(jersey.Low, pant.High, "The jersey should hang over the pants.");
    }

    /// <summary>The stream slot in a format token, checked on every model: a split shape's strides add up to its format's.</summary>
    [TestMethod]
    public void SplitVertexStreamsMatchTheirFormat()
    {
        int shapes = 0, split = 0, withFormat = 0, withUvs = 0, mismatched = 0, unnamedStreams = 0, stillUnknown = 0;
        var problems = new List<string>();
        foreach (var file in _vfs.Files.Where(f => !f.IsLoose && f.Extension == ".mcd"))
        {
            var data = _vfs.ReadContent(file);
            if (!McdFile.IsMcd(data))
                continue;
            var model = McdFile.Parse(data);
            var usedWithFormat = new HashSet<int>();
            foreach (var shape in model.Shapes)
            {
                shapes++;
                var ids = shape.AllStreams ?? [shape.Stream];
                if (ids.Count > 1)
                    split++;
                if (shape.Format < 0 || shape.Format >= model.Formats.Count)
                    continue;
                withFormat++;
                var format = model.Formats[shape.Format];
                if (ids.Sum(id => model.Streams[id].Stride) != format.Stride)
                {
                    mismatched++;
                    // Only player heads are known to do this: one 40-byte stream named with a 16-byte format.
                    if (ids.Count > 1 || !shape.Name.Contains("head", StringComparison.OrdinalIgnoreCase))
                        problems.Add($"UNEXPECTED {file.Name} {shape.Name}");
                    continue;
                }
                usedWithFormat.UnionWith(ids);
                if (model.ReadUvs(shape) != null)
                    withUvs++;
            }
            for (var s = 0; s < model.Streams.Count; s++)
                if (model.Streams[s].Format == null)
                {
                    unnamedStreams++;
                    if (!usedWithFormat.Contains(s))
                        stillUnknown++;
                }
        }
        TestContext.WriteLine($"{shapes} shapes, {split} split across streams, {withFormat} with a format, {withUvs} with UVs, {mismatched} whose strides do not add up");
        TestContext.WriteLine($"{unnamedStreams} streams match no format by themselves; {stillUnknown} of those are still unexplained");
        foreach (var problem in problems)
            TestContext.WriteLine(problem);
        Assert.IsGreaterThan(0, split);
        Assert.IsEmpty(problems, "A shape other than a head has strides that do not add up to its format. See the test output.");
    }

    [TestMethod]
    public void UniformSheetsDecode()
    {
        var toronto = UniformKits.From(_catalog.ArtOf(_catalog.Teams.First(t => t.Name == "Toronto Maple Leafs"))).First(k => k.Key == "adidas_toronto_home");
        foreach (var path in new[] { toronto.Jersey, toronto.Pant!, toronto.Sock! })
        {
            var texture = GameTexture.TryOpen(_vfs.ReadContent(_vfs.Find(path)!));
            Assert.IsNotNull(texture, path);
            Assert.IsNull(texture.Blocker, path);
            Assert.IsGreaterThan(0, texture.DecodeMip(0).Width, path);
        }
    }
}
