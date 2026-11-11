using Blueline.Core;
using Blueline.Core.Teams;
using Blueline.Formats.Textures;

namespace Blueline.Tests;

/// <summary>Jersey normal and coeff maps made from colour sheets, checked against the game's own maps.</summary>
[TestClass]
public sealed class JerseyMapsTests
{
    private const string Folder = "nhl/data/kettle/rendering/characters/player/";
    private static VirtualFileSystem _vfs = null!;
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
    }

    [ClassCleanup]
    public static void Unmount() => _vfs?.Dispose();

    [TestInitialize]
    public void Check()
    {
        if (_vfs == null)
            Assert.Inconclusive("The game is not here.");
    }

    private static byte[] Raw(string name) => _vfs.ReadContent(_vfs.Find(Folder + name) ?? throw new AssertFailedException($"No such file: {name}"));

    private static RgbaImage Sheet(string name) => GameTexture.TryOpen(Raw(name))!.DecodeMip(0);

    /// <summary>
    /// Where the game marks a crest in a coeff map: blue is near 0 on fabric and 46 to 79 on crests, so this is a cut
    /// at 30, not the raw value.
    /// </summary>
    private static float[] BlueMask(RgbaImage coeff) => Enumerable.Range(0, coeff.Width * coeff.Height).Select(i => coeff.Pixels[i * 4 + 2] > 30 ? 1f : 0f).ToArray();

    /// <summary>Gradient energy of the red channel where the mask is on, to measure how much is pressed in.</summary>
    private static double EdgeEnergy(RgbaImage normal, float[] where)
    {
        var (w, total, count) = (normal.Width, 0.0, 0);
        for (var y = 1; y < normal.Height - 1; y++)
            for (var x = 1; x < w - 1; x++)
                if (where[y * w + x] > 0.5f)
                {
                    var i = (y * w + x) * 4;
                    var gx = normal.Pixels[i + 4] - normal.Pixels[i - 4];
                    var gy = normal.Pixels[i + w * 4] - normal.Pixels[i - w * 4];
                    total += gx * gx + gy * gy;
                    count++;
                }
        return total / Math.Max(count, 1);
    }

    /// <summary>
    /// Detail at the size of a pressed logo, with the 6 px weave and the broad folds both blurred out, where the mask is
    /// on. Measures a mark without counting the weave around it.
    /// </summary>
    private static double MidEnergy(RgbaImage normal, float[] where)
    {
        var (w, h) = (normal.Width, normal.Height);
        double total = 0;
        var count = 0;
        for (var c = 0; c < 2; c++)
        {
            var channel = Enumerable.Range(0, w * h).Select(i => (float)normal.Pixels[i * 4 + c]).ToArray();
            var (mid, broad) = (JerseyMaps.Blur(channel, w, h, 2), JerseyMaps.Blur(channel, w, h, 10));
            for (var i = 0; i < channel.Length; i++)
                if (where[i] > 0.5f)
                {
                    total += Math.Pow(mid[i] - broad[i], 2);
                    count++;
                }
        }
        return total / Math.Max(count, 1);
    }

    [TestMethod]
    public void SheetNamesResolve()
    {
        var sheets = JerseySheets.FromColour(Folder + "jersey_rbk_rochester_away_cm.gnf");
        Assert.IsNotNull(sheets);
        Assert.AreEqual("rbk", sheets.Brand);
        Assert.AreEqual(Folder + "jersey_rbk_rochester_away_nm.gnf", sheets.Normal);
        Assert.AreEqual(Folder + "jersey_rbk_rochester_away_coeff.gnf", sheets.Coeff);
        Assert.IsNull(JerseySheets.FromColour(Folder + "pant_rbk_rochester_away_cm.gnf"));
        Assert.IsNull(JerseySheets.FromColour(Folder + "jersey_rbk_rochester_away_nm.gnf"));
        foreach (var path in new[] { sheets.Colour, sheets.Normal, sheets.Coeff })
            Assert.IsNotNull(_vfs.Find(path), path);
    }

    /// <summary>
    /// The template jersey's crest is in its coeff's blue channel and pressed into its normal map. Pressing that mask
    /// into a flat map must lean the same way as the game's, by about the same amount.
    /// </summary>
    [TestMethod]
    public void PressedCrestMatchesTheGamesDirection()
    {
        var stock = Sheet("jersey_rbk_template_home_nm.gnf");
        var coeff = Sheet("jersey_rbk_template_away_coeff.gnf");
        Assert.AreEqual(stock.Width, coeff.Width);
        var flat = new RgbaImage(stock.Width, stock.Height, Enumerable.Range(0, stock.Width * stock.Height * 4).Select(i => (byte)(i % 4 == 3 ? 255 : i % 4 == 2 ? 0 : 128)).ToArray());
        // Default edge, depth and softness; stitching and grain off, so only the crest's own edge is compared.
        var made = JerseyMaps.Normal(flat, BlueMask(coeff), new JerseyMapOptions(Stitch: false, Grain: 0));

        // How strong the edges look: the strongest twentieth of edge pixels, ours against the game's.
        var mask = BlueMask(coeff);
        var ring = JerseyMaps.Blur(mask, stock.Width, stock.Height, 2).Select(v => v is > 0.05f and < 0.95f ? 1f : 0f).ToArray();
        double Strong(RgbaImage image)
        {
            var values = Enumerable.Range(0, ring.Length).Where(i => ring[i] > 0)
                .Select(i => Math.Abs(image.Pixels[i * 4] - 128.0) + Math.Abs(image.Pixels[i * 4 + 1] - 128.0)).OrderBy(v => v).ToArray();
            return values[(int)(values.Length * 0.95)];
        }
        var (ourStrength, gameStrength) = (Strong(made), Strong(stock));
        TestContext.WriteLine($"Strongest edges: ours {ourStrength:F0}, the game's {gameStrength:F0}");
        Assert.IsTrue(ourStrength / gameStrength is > 0.7 and < 1.4, $"Edges look {ourStrength / gameStrength:F2} times as strong as the game's.");

        foreach (var channel in new[] { 0, 1 })
        {
            double sxy = 0, sxx = 0;
            for (var i = 0; i < made.Pixels.Length; i += 4)
            {
                var ours = made.Pixels[i + channel] - 128.0;
                if (Math.Abs(ours) < 6)
                    continue;
                sxy += ours * (stock.Pixels[i + channel] - 128.0);
                sxx += ours * ours;
            }
            var slope = sxy / sxx;
            TestContext.WriteLine($"channel {"RG"[channel]}: the game's map leans {slope:F2} times ours at the crest edges");
            // Direction must agree; depth is matched on the strongest edges above.
            Assert.IsTrue(slope is > 0.3 and < 2, $"Channel {"RG"[channel]} leans {slope:F2} times ours; the direction is off.");
        }
    }

    /// <summary>Each team's crest sits on its own pixels, so the plain base keeps fabric and loses crests.</summary>
    [TestMethod]
    public void PlainBaseDropsTheCrest()
    {
        var plain = JerseyMaps.PlainBase(_vfs, "rbk", "nm");
        var stock = Sheet("jersey_rbk_rochester_away_nm.gnf");
        var crest = BlueMask(Sheet("jersey_rbk_rochester_away_coeff.gnf"));
        var (inStock, inPlain) = (EdgeEnergy(stock, crest), EdgeEnergy(plain, crest));
        TestContext.WriteLine($"Edge energy on Rochester's crest: stock {inStock:F0}, plain base {inPlain:F0}");
        Assert.IsLessThan(inStock * 0.5, inPlain, "The plain base still has Rochester's crest pressed in.");
        Assert.HasCount(15, JerseyMaps.BaseSources(_vfs, "rbk", "nm"));
    }

    /// <summary>Every adidas jersey shares the adidas mark, so it survives the median; clearing must take it out under a new logo.</summary>
    [TestMethod]
    public void ClearingRemovesAMarkTheBaseShares()
    {
        var plain = JerseyMaps.PlainBase(_vfs, "adidas", "nm");
        var mark = BlueMask(Sheet("jersey_adidas_boston_home_coeff.gnf"));
        Assert.IsGreaterThan(100, mark.Count(m => m > 0.5f), "The adidas mark should be in the coeff's blue channel.");
        var cleared = JerseyMaps.Cleared(plain, mark, 20);
        var before = MidEnergy(plain, mark);
        var after = MidEnergy(cleared, mark);
        TestContext.WriteLine($"Logo-sized detail on the adidas mark: plain base {before:F2}, cleared {after:F2}");
        // What is left is mostly borrowed weave the blur does not fully remove.
        Assert.IsLessThan(before * 0.4, after);

        // The band cleared around a design keeps its weave, borrowed from clean fabric, so it does not look blurred.
        var (w, h) = (plain.Width, plain.Height);
        var near = JerseyMaps.Blur(mark, w, h, 12);
        var band = near.Select((v, i) => v > 0.05f && mark[i] == 0 ? 1f : 0f).ToArray();
        Assert.IsGreaterThan(1000, band.Count(b => b > 0));
        var (bandBefore, bandAfter) = (EdgeEnergy(plain, band), EdgeEnergy(cleared, band));
        TestContext.WriteLine($"Fine detail in the band around it: before {bandBefore:F0}, after {bandAfter:F0}");
        Assert.IsGreaterThan(bandBefore * 0.5, bandAfter, "The band around the design lost its weave.");
    }

    /// <summary>The weave repeats every <see cref="JerseyMaps.WeavePeriod"/> pixels, which clearing relies on to borrow it.</summary>
    [TestMethod]
    public void WeaveRepeatsAtItsPeriod()
    {
        var plain = JerseyMaps.PlainBase(_vfs, "adidas", "nm");
        var (w, h) = (plain.Width, plain.Height);
        var channel = Enumerable.Range(0, w * h).Select(i => (float)plain.Pixels[i * 4]).ToArray();
        var smooth = JerseyMaps.Blur(channel, w, h, 1);
        var fine = channel.Select((v, i) => v - smooth[i]).ToArray();
        double Correlation(int dx, int dy)
        {
            double sab = 0, saa = 0, sbb = 0;
            for (var y = 200; y < 700; y += 2)
                for (var x = 1300; x < 1800; x += 2)
                {
                    var (a, b) = (fine[y * w + x], fine[(y + dy) * w + x + dx]);
                    sab += a * b;
                    saa += a * a;
                    sbb += b * b;
                }
            return sab / Math.Sqrt(saa * sbb);
        }
        var p = JerseyMaps.WeavePeriod;
        var (across, down, off) = (Correlation(p, 0), Correlation(0, p), Correlation(p / 2, 0));
        TestContext.WriteLine($"Weave at {p} px: across {across:F2}, down {down:F2}; at {p / 2} px across {off:F2}");
        Assert.IsGreaterThan(0.5, across);
        Assert.IsGreaterThan(0.5, down);
        Assert.IsLessThan(across, off);
    }

    /// <summary>Made maps go into the game's own files in their format, and read back close to what was made.</summary>
    [TestMethod]
    public void MadeMapsImportInTheGamesFormat()
    {
        var sheets = JerseySheets.FromColour(Folder + "jersey_adidas_toronto_home_cm.gnf")!;
        var options = new JerseyMapOptions();
        var normalBase = JerseyMaps.PlainBase(_vfs, "adidas", "nm");
        var areas = JerseyMaps.FromColour(Sheet("jersey_adidas_toronto_home_cm.gnf"), options.Tolerance, normalBase.Width, normalBase.Height);
        Assert.IsTrue(areas.Mask.Average() is > 0.01f and < 0.5f, $"Raised share {areas.Mask.Average():P1} is not a jersey's design.");
        var normal = JerseyMaps.Normal(normalBase, areas, options);
        var coeff = JerseyMaps.Coeff(JerseyMaps.PlainBase(_vfs, "adidas", "coeff"), areas, options);

        foreach (var (path, made) in new[] { (sheets.Normal, normal), (sheets.Coeff, coeff) })
        {
            var original = _vfs.ReadContent(_vfs.Find(path)!);
            var gnf = TexturePipeline.Import(original, made, BCnEncoder.Encoder.CompressionQuality.Fast).Gnf;
            var (was, now) = (GameTexture.TryOpen(original)!, GameTexture.TryOpen(gnf)!);
            Assert.AreEqual(was.Summary, now.Summary, path);
            var back = now.DecodeMip(0);
            double error = 0;
            for (var i = 0; i < back.Pixels.Length; i += 4)
                for (var c = 0; c < 2; c++)
                    error += Math.Pow(back.Pixels[i + c] - made.Pixels[i + c], 2);
            var psnr = 10 * Math.Log10(255.0 * 255 / (error / (back.Pixels.Length / 2)));
            TestContext.WriteLine($"{Path.GetFileName(path)}: {now.Summary}, {psnr:F1} dB after encoding");
            Assert.IsGreaterThan(35, psnr, path);
        }
    }
}
