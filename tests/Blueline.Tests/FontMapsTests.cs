using BCnEncoder.Encoder;
using Blueline.Core;
using Blueline.Core.Teams;
using Blueline.Formats.Textures;

namespace Blueline.Tests;

/// <summary>Font normal and sm maps made from colour sheets, checked against the game's own fonts.</summary>
[TestClass]
public sealed class FontMapsTests
{
    private const string Folder = "nhl/data/kettle/rendering/characters/player/";

    /// <summary>Fonts of each brand and style, picked from the 120 the defaults were measured on.</summary>
    private static readonly string[] Samples =
    [
        "font_rbk_kamloops_blazers_away", "font_rbk_owen_sound_attack_home", "font_rbk_kansascity_mavericks_away",
        "font_nineties_philadelphia_two", "font_adidas_detroit_home", "font_rbk_utica_away",
    ];

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

    private static byte[]? Raw(string path) => _vfs.Find(path) is { } file ? _vfs.ReadContent(file) : null;

    private static RgbaImage Sheet(string name) => GameTexture.TryOpen(Raw(Folder + name) ?? throw new AssertFailedException($"No such file: {name}"))!.DecodeMip(0);

    /// <summary>Mean slope (stored offset of red and green from 128) on letter pixels up to 3 px in from an edge.</summary>
    private static double EdgeSlope(RgbaImage normal, FontLayers layers)
    {
        var distance = FontMaps.InsideDistance(layers.Letters, layers.Width, layers.Height);
        var (total, count) = (0.0, 0);
        for (var i = 0; i < distance.Length; i++)
            if (distance[i] is > 0 and <= 3 && i / layers.Width < layers.BottomRowStart)
            {
                total += Math.Abs(normal.Pixels[i * 4] - 128) + Math.Abs(normal.Pixels[i * 4 + 1] - 128);
                count++;
            }
        return total / Math.Max(count, 1);
    }

    [TestMethod]
    public void SheetNamesResolve()
    {
        var sheets = FontSheets.FromColour(Folder + "font_rbk_binghamton_home_cm.gnf")!;
        Assert.AreEqual(Folder + "font_rbk_binghamton_home_nm.gnf", sheets.Normal);
        Assert.AreEqual(Folder + "font_rbk_binghamton_home_sm.gnf", sheets.Sm);
        Assert.AreEqual("rbk", sheets.Brand);
        Assert.IsNull(FontSheets.FromColour(Folder + "jersey_rbk_binghamton_home_cm.gnf"));
        Assert.IsNotNull(Raw(sheets.Normal));
        Assert.IsNotNull(Raw(sheets.Sm));
    }

    /// <summary>At default options the edges are about as steep as the game's, and lean the same way.</summary>
    [TestMethod]
    public void EdgesMatchTheGame()
    {
        var ratios = new List<double>();
        foreach (var name in Samples)
        {
            var layers = FontMaps.Layers(Sheet(name + "_cm.gnf"), 30, 1024, 1024);
            var stock = Sheet(name + "_nm.gnf");
            var made = FontMaps.Normal(layers, new FontMapOptions());
            ratios.Add(EdgeSlope(made, layers) / EdgeSlope(stock, layers));

            // Same direction: red against red and green against green, on edge pixels.
            var distance = FontMaps.InsideDistance(layers.Letters, 1024, 1024);
            double agree = 0, total = 0;
            for (var i = 0; i < distance.Length; i++)
                if (distance[i] is > 0 and <= 2)
                    for (var c = 0; c < 2; c++)
                    {
                        agree += (made.Pixels[i * 4 + c] - 128.0) * (stock.Pixels[i * 4 + c] - 128.0);
                        total += Math.Abs((made.Pixels[i * 4 + c] - 128.0) * (stock.Pixels[i * 4 + c] - 128.0));
                    }
            Assert.IsGreaterThan(0.3, agree / total, $"{name}: our edges lean the other way");
            for (var i = 0; i < made.Pixels.Length; i += 4)
                if (made.Pixels[i + 2] != 0)
                    Assert.Fail("Blue must stay empty, as in the game's normal maps.");
        }
        var median = ratios.Order().ElementAt(ratios.Count / 2);
        Assert.IsTrue(median is > 0.7 and < 1.4, $"Edge slope against the game's: {string.Join(", ", ratios.Select(r => r.ToString("F2")))}");
    }

    /// <summary>Black off the letters, the measured greys on them, and the bottom row of small numbers on its own.</summary>
    [TestMethod]
    public void SmHasTheGamesLevels()
    {
        var layers = FontMaps.Layers(Sheet("font_rbk_owen_sound_attack_home_cm.gnf"), 30, 1024, 1024);
        Assert.IsTrue(layers.BottomRowStart is > 880 and < 930, $"bottom row starts at {layers.BottomRowStart}");
        var sm = FontMaps.Sm(layers, new FontMapOptions());
        var (fill, outline, bottom) = (new List<int>(), new List<int>(), new List<int>());
        for (var i = 0; i < layers.Letters.Length; i++)
        {
            int value = sm.Pixels[i * 4];
            if (layers.Letters[i] == 0)
                Assert.AreEqual(0, value, "Off the letters the sm map is black");
            else if (layers.Letters[i] == 1 && i / 1024 >= layers.BottomRowStart)
                bottom.Add(value);
            else if (layers.Letters[i] == 1 && layers.Fill[i] == 1)
                fill.Add(value);
            else if (layers.Letters[i] == 1 && layers.Fill[i] == 0)
                outline.Add(value);
        }
        static int Median(List<int> values) => values.Order().ElementAt(values.Count / 2);
        Assert.AreEqual(30, Median(fill), 2);
        Assert.AreEqual(24, Median(outline), 2);
        Assert.AreEqual(50, Median(bottom), 2);
    }

    /// <summary>Encoded maps keep the game file's size and format, and only maps the game has for a font are made.</summary>
    [TestMethod]
    public void MadeMapsImportInTheGamesFormat()
    {
        var colour = Sheet("font_rbk_kamloops_blazers_away_cm.gnf");
        var items = FontMaps.Encode(FontSheets.FromColour(Folder + "font_rbk_kamloops_blazers_away_cm.gnf")!, colour, Raw, new FontMapOptions(), CompressionQuality.Fast);
        Assert.HasCount(2, items);
        foreach (var (path, content, _) in items)
        {
            var original = GameTexture.TryOpen(Raw(path)!)!;
            var made = GameTexture.TryOpen(content)!;
            Assert.AreEqual(original.Summary, made.Summary, path);
        }

        // Toronto's home font has a colour sheet and nothing else in the game.
        var colourOnly = FontSheets.FromColour(Folder + "font_rbk_toronto_home_cm.gnf")!;
        Assert.IsNull(Raw(colourOnly.Normal));
        Assert.IsNull(Raw(colourOnly.Sm));
        Assert.IsEmpty(FontMaps.Encode(colourOnly, GameTexture.TryOpen(Raw(colourOnly.Colour)!)!.DecodeMip(0), Raw, new FontMapOptions(), CompressionQuality.Fast));
    }
}
