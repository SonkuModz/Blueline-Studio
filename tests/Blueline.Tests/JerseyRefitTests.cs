using Blueline.Core;
using Blueline.Core.Teams;
using Blueline.Formats.Textures;

namespace Blueline.Tests;

/// <summary>Fitting a sheet drawn for one brand's jersey onto another's, checked on the game's own jersey meshes.</summary>
[TestClass]
public sealed class JerseyRefitTests
{
    private const int Size = JerseyRefit.DefaultSize;
    private static VirtualFileSystem _vfs = null!;

    [ClassInitialize]
    public static void Mount(TestContext _)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && _vfs == null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
            if (File.Exists(Path.Combine(candidate, "eboot.bin")))
                _vfs = VirtualFileSystem.Mount(GameInstall.Open(candidate, useModsFolder: false));
        }
    }

    [ClassCleanup]
    public static void Unmount() => _vfs?.Dispose();

    [TestInitialize]
    public void Check()
    {
        if (_vfs == null)
            Assert.Inconclusive("The game is not here.");
    }

    private static byte[]? Read(string path) => _vfs.Find(path) is { } file ? _vfs.ReadContent(file) : null;

    /// <summary>A jersey fitted onto itself reads every texel from where it already is.</summary>
    [TestMethod]
    public void SameJerseyIsUnchanged()
    {
        var map = JerseyRefit.Map(Read, "adidas", "adidas", Size);
        var errors = new List<double>();
        for (var i = 0; i < Size * Size; i++)
            if (map.Covered[i])
                errors.Add(Math.Max(Math.Abs(map.Source[i * 2] * Size - (i % Size + 0.5)), Math.Abs(map.Source[i * 2 + 1] * Size - (i / Size + 0.5))));
        var sorted = errors.Order().ToList();
        Assert.IsLessThan(1.0, sorted[sorted.Count / 2], "median error in texels");
        Assert.IsLessThan(4.0, sorted[sorted.Count * 99 / 100], "99th percentile error in texels");
    }

    /// <summary>
    /// The bug this exists for: an adidas-layout sheet in an rbk slot showed its dark collar patch under the arm. Paint
    /// everything the adidas jersey does not use black; fitted to the rbk jersey, none of that may show.
    /// </summary>
    [TestMethod]
    public void UnusedAreasStayOffTheJersey()
    {
        // The adidas jersey's area, grown by a few texels as any real sheet's padding is, so filtering at its edges reads white.
        var adidas = JerseyRefit.Map(Read, "adidas", "adidas", Size).Covered;
        var used = (bool[])adidas.Clone();
        for (var ring = 0; ring < 3; ring++)
        {
            var grown = (bool[])used.Clone();
            for (var i = 0; i < Size * Size; i++)
                if (used[i])
                    foreach (var n in new[] { i - 1, i + 1, i - Size, i + Size })
                        if (n >= 0 && n < Size * Size)
                            grown[n] = true;
            used = grown;
        }
        var pixels = new byte[Size * Size * 4];
        for (var i = 0; i < Size * Size; i++)
        {
            var v = used[i] ? (byte)255 : (byte)0;
            (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) = (v, v, v, 255);
        }
        var map = JerseyRefit.Map(Read, "adidas", "rbk", Size);
        var fitted = JerseyRefit.Colour(map, new RgbaImage(Size, Size, pixels));
        int covered = 0, dark = 0;
        for (var i = 0; i < Size * Size; i++)
            if (map.Covered[i])
            {
                covered++;
                if (fitted.Pixels[i * 4] < 128)
                    dark++;
            }
        Assert.IsLessThan(0.002, dark / (double)covered, $"{dark} of {covered} rbk texels read from outside the adidas jersey");
    }

    /// <summary>
    /// Neighbouring texels read from neighbouring spots, so logos bend rather than break. Measured on every 4th texel; some
    /// jumps are real seams, where pieces touch on the sheet but not on the jersey. The map seen working in game had 0.8%.
    /// </summary>
    [TestMethod]
    public void StretchHasFewBreaks()
    {
        var map = JerseyRefit.Map(Read, "adidas", "rbk", Size);
        int pairs = 0, breaks = 0;
        for (var y = 0; y < Size; y += 4)
            for (var x = 0; x + 4 < Size; x += 4)
            {
                var (a, b) = (y * Size + x, y * Size + x + 4);
                if (!map.Covered[a] || !map.Covered[b])
                    continue;
                pairs++;
                var jump = Math.Max(Math.Abs(map.Source[a * 2] - map.Source[b * 2]), Math.Abs(map.Source[a * 2 + 1] - map.Source[b * 2 + 1])) * Size;
                if (jump > 12)
                    breaks++;
            }
        Assert.IsLessThan(0.012, breaks / (double)pairs, $"{breaks} of {pairs} texel pairs 4 apart jump more than 12 texels");
    }

    /// <summary>A flat normal map stays flat, and blue stays empty as in the game's maps.</summary>
    [TestMethod]
    public void FlatNormalStaysFlat()
    {
        var pixels = new byte[Size * Size * 4];
        for (var i = 0; i < Size * Size; i++)
            (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) = (128, 128, 255, 255);
        var fitted = JerseyRefit.Normal(JerseyRefit.Map(Read, "adidas", "rbk", Size), new RgbaImage(Size, Size, pixels));
        for (var i = 0; i < Size * Size; i++)
        {
            Assert.AreEqual(128, fitted.Pixels[i * 4], 1);
            Assert.AreEqual(128, fitted.Pixels[i * 4 + 1], 1);
            Assert.AreEqual(0, fitted.Pixels[i * 4 + 2]);
        }
    }
}
