using BCnEncoder.Encoder;
using System.Net;
using Blueline.Core;
using Blueline.Core.Rinks;
using Blueline.Formats.Models;
using Blueline.Formats.Textures;

namespace Blueline.Tests;

/// <summary>Full-ice rinks: TheFaceoff.net parsing and politeness (no network), the fit, and the plain rink.</summary>
[TestClass]
public sealed class FullIceTests
{
    private static string _temp = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        _temp = Path.Combine(Path.GetTempPath(), "blueline-fullice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    [ClassCleanup]
    public static void Stop() => Directory.Delete(_temp, true);

    private const string Id = "84911f_ff388c997054408cad67e83aa56a051a~mv2.jpg";
    private const string Page = "https://www.thefaceoff.net/center-ice/617d2d04-6b2d-4c4d-ba33-c2897bf27d7c";

    [TestMethod]
    public void ReadsImageEntriesInBothKeyOrders()
    {
        var html = "{\"width\":5000,\"height\":2236,\"uri\":\"84911f_00000000000000000000000000000001~mv2.jpg\"}"
            + "\"comp-x\":{\"height\":2236,\"width\":5000,\"fallbackTitle\":\"2027 Rink.jpg\",\"uri\":\"" + Id + "\",\"crop\":null,\"link\":{\"href\":\"" + Page.Replace("/", "\\/") + "\"}}"
            + "{\"height\":724,\"width\":1286,\"uri\":\"84911f_00000000000000000000000000000002~mv2.jpg\"}";
        var images = FaceoffNet.ParseImages(html);
        Assert.HasCount(3, images);
        Assert.AreEqual(5000, images[0].Width);
        Assert.AreEqual(2236, images[1].Height);
        Assert.AreEqual(Page, images[1].Link);
        Assert.IsTrue(images[1].IsLayoutShape);
        Assert.IsFalse(images[2].IsLayoutShape);
    }

    [TestMethod]
    public void MatchesTeamTitlesOnly()
    {
        FaceoffRink R(string t) => new(t, "https://www.thefaceoff.net/center-ice/" + t.GetHashCode(), null);
        var list = new[] { R("Toronto Maple Leafs Practice 2027"), R("Toronto Maple Leafs 2027"), R("Montréal Canadiens 2027"), R("Toronto Maple Leafs 2026") };
        Assert.AreEqual("Toronto Maple Leafs 2027", FaceoffNet.MatchTeam(list, ["Toronto Maple Leafs"])?.Title);
        Assert.AreEqual("Montréal Canadiens 2027", FaceoffNet.MatchTeam(list, ["Montreal Canadiens"])?.Title);
        Assert.IsNull(FaceoffNet.MatchTeam(list, ["Toronto"]));
        var midSeason = new[] { R("Cleveland Monsters 2027 (2nd)"), R("Cleveland Monsters 2027 (1st)") };
        Assert.AreEqual("Cleveland Monsters 2027 (2nd)", FaceoffNet.MatchTeam(midSeason, ["Cleveland Monsters"])?.Title);
    }

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Url, DateTime At)> Seen { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Seen.Add((request.RequestUri!.ToString(), DateTime.UtcNow));
            return Task.FromResult(answer(request));
        }
    }

    [TestMethod]
    public async Task RefusalIsNeverRetried()
    {
        var fake = new Fake(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var site = new FaceoffNet(Path.Combine(_temp, "refused"), fake);
        await Assert.ThrowsExactlyAsync<FaceoffRefusedException>(() => site.ListAsync("nhl", 1));
        Assert.HasCount(1, fake.Seen);
    }

    [TestMethod]
    public async Task DownloadsAreSpacedAndCached()
    {
        var fake = new Fake(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        var cache = Path.Combine(_temp, "cache");
        using var site = new FaceoffNet(cache, fake);
        var a = await site.DownloadAsync(new FaceoffImage("84911f_00000000000000000000000000000001~mv2.jpg", 5000, 2236));
        var b = await site.DownloadAsync(new FaceoffImage("84911f_00000000000000000000000000000002~mv2.jpg", 5000, 2236));
        var again = await site.DownloadAsync(new FaceoffImage("84911f_00000000000000000000000000000001~mv2.jpg", 5000, 2236));
        Assert.HasCount(2, fake.Seen);
        Assert.AreEqual(a, again);
        Assert.IsTrue(fake.Seen[1].At - fake.Seen[0].At >= TimeSpan.FromSeconds(1.4));
        Assert.IsFalse(Directory.EnumerateFiles(cache, "*.part").Any());
        Assert.IsTrue(File.Exists(b));
    }

    /// <summary>A TheFaceoff.net-style layout: white page, dark board outline, white ice, a red line at centre.</summary>
    private static RgbaImage SyntheticLayout(int w = 2500, int h = 1118, int left = 38, int right = 2461)
    {
        var px = new byte[w * h * 4];
        Array.Fill(px, (byte)255);
        var scale = (right - left + 1) / FullIce.IceLength;
        var halfWidth = FullIce.IceWidth / 2 * scale;
        var top = (int)Math.Round(h / 2.0 - halfWidth) - 1;
        var bottom = (int)Math.Round(h / 2.0 + halfWidth);
        void Set(int x, int y, byte r, byte g, byte b)
        {
            var o = (y * w + x) * 4;
            px[o] = r; px[o + 1] = g; px[o + 2] = b;
        }
        for (var y = top - 15; y <= bottom + 15; y++)
            for (var t = 1; t <= 16; t++)
            {
                Set(left - t, y, 30, 30, 30);
                Set(right + t, y, 30, 30, 30);
            }
        for (var x = left - 16; x <= right + 16; x++)
            for (var t = 0; t < 4; t++)
            {
                Set(x, top - t, 30, 30, 30);
                Set(x, bottom + t, 30, 30, 30);
            }
        var centre = (left + right + 1) / 2;
        for (var y = top + 1; y < bottom; y++)
            for (var x = centre - 6; x <= centre + 6; x++)
                Set(x, y, 200, 20, 40);
        return new RgbaImage(w, h, px);
    }

    [TestMethod]
    public void MeasuresTheBoardOutline()
    {
        var m = FullIce.Measure(SyntheticLayout());
        Assert.IsTrue(m.Found, m.How);
        Assert.AreEqual(1250, m.CentreX, 1.0);
        Assert.AreEqual((2461 - 38 + 1) / FullIce.IceLength, m.PixelsPerMetre, 0.2);
    }

    [TestMethod]
    public void GoalLinesDecideTheScaleWhenTheDrawingHasThem()
    {
        // Goal lines drawn 26.9 m from centre by the boards' scale: the fit must put them on the game's 27.165.
        var image = SyntheticLayout();
        var boards = FullIce.Measure(image);
        var w = image.Width;
        foreach (var side in new[] { -1, 1 })
        {
            var x0 = (int)Math.Round(boards.CentreX + side * 26.9 * boards.PixelsPerMetre);
            for (var y = (int)(boards.CentreY - 12 * boards.PixelsPerMetre); y < boards.CentreY + 12 * boards.PixelsPerMetre; y++)
                for (var x = x0 - 2; x <= x0 + 2; x++)
                {
                    var o = (y * w + x) * 4;
                    image.Pixels[o] = 200; image.Pixels[o + 1] = 20; image.Pixels[o + 2] = 40;
                }
        }
        var m = FullIce.Measure(image);
        StringAssert.StartsWith(m.How, "goal lines");
        Assert.AreEqual(boards.PixelsPerMetre * 26.9 / FullIce.GoalLine, m.PixelsPerMetre, 0.1);
        Assert.AreEqual(boards.CentreX, m.CentreX, 1.0);
    }

    [TestMethod]
    public void FitPutsTheEndBoardsOnTheGamesAndTheRedLineInTheMiddle()
    {
        var fitted = FullIce.Fit(SyntheticLayout(), FullIceFit.Layout, 512);
        Assert.AreEqual(512, fitted.Width);
        Assert.AreEqual(512, fitted.Height);
        int Sum(int x, int y) { var o = (y * 512 + x) * 4; return fitted.Pixels[o] + fitted.Pixels[o + 1] + fitted.Pixels[o + 2]; }
        bool Red(int x, int y) { var o = (y * 512 + x) * 4; return fitted.Pixels[o] > 150 && fitted.Pixels[o + 1] < 120; }

        Assert.IsTrue(Red(255, 256) || Red(256, 256), "the red line is at u = 0.5");
        // The end boards: u = 0.5 +- 30.48 / 62.56.
        var board = 0.5 + FullIce.IceLength / 2 / FullIce.FrameLength;
        var bx = (int)(board * 512);
        Assert.IsTrue(Enumerable.Range(bx - 1, 4).Any(x => Sum(x, 256) < 450), "dark outline at the game's end boards");
        Assert.IsTrue(Enumerable.Range(0, 512 * 512).All(i => fitted.Pixels[i * 4 + 3] == 255), "fully opaque");
    }

    [TestMethod]
    public void PlainRinkMarkingsSitWhereTheRuleBookSays()
    {
        var size = 1024;
        var rink = PlainRink.Draw(null, null, size);
        (byte R, byte G, byte B) At(double zFeet, double xFeet)
        {
            var u = 0.5 + zFeet / 3.28084 / FullIce.FrameLength;
            var v = 0.5 - xFeet / 3.28084 / FullIce.FrameWidth;
            var o = ((int)(v * size) * size + (int)(u * size)) * 4;
            return (rink.Pixels[o], rink.Pixels[o + 1], rink.Pixels[o + 2]);
        }
        var blue = At(25.5, 30);
        Assert.IsTrue(blue.B > 120 && blue.R < 60, $"blue line {blue}");
        var red = At(0, 30);
        Assert.IsTrue(red.R > 150 && red.G < 80, $"red line {red}");
        var ice = At(50, 30);
        Assert.IsTrue(ice.R > 220 && ice.G > 220 && ice.B > 220, $"ice {ice}");
        var dot = At(69, 22);
        Assert.IsTrue(dot.R > 150 && dot.G < 80, $"faceoff dot {dot}");
        var crease = At(86.5, 0);
        Assert.IsTrue(crease.B > 200 && crease.R < 160, $"crease {crease}");
    }

    [TestMethod]
    public void PlainRinkPutsTheLogoWhereTheStockModelHadIt()
    {
        var logo = new RgbaImage(4, 4, Enumerable.Repeat(new byte[] { 10, 200, 10, 255 }, 16).SelectMany(p => p).ToArray());
        var rink = PlainRink.Draw(logo, null, 1024);
        int Green(double zm) { var u = 0.5 + zm / FullIce.FrameLength; var o = (512 * 1024 + (int)(u * 1024)) * 4; return rink.Pixels[o + 1] - rink.Pixels[o]; }
        Assert.IsGreaterThan(150, Green(3.0));
        Assert.IsLessThan(50, Green(6.0));
    }

    [TestMethod]
    public void SharedRinkIsEncodedOncePerSlotFormat()
    {
        string? game = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && game == null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
            if (File.Exists(Path.Combine(candidate, "eboot.bin")))
                game = candidate;
        }
        if (game == null)
            Assert.Inconclusive("No NHL 19 install found.");
        using var vfs = VirtualFileSystem.Mount(GameInstall.Open(game, useModsFolder: false));
        var slots = new[] { 27, 15, 1 }.Select(FullIce.SlotPath).Where(p => vfs.Find(p) != null)
            .Select(p => (p, vfs.ReadContent(vfs.Find(p)!))).ToList();
        var shared = FullIceJob.Spread(PlainRink.Draw(null, null, 256), slots, CompressionQuality.Fast);
        Assert.HasCount(slots.Count, shared);
        Assert.IsTrue(shared.All(s => ReferenceEquals(s.Gnf, shared[0].Gnf)), "same format, one encode, shared bytes");
    }

    private static string? RepoFile(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, relative)))
                return Path.Combine(d.FullName, relative);
        return null;
    }

    [TestMethod]
    public void TellsTheFullIceModelFromTheStockOne()
    {
        var reference = RepoFile(Path.Combine("references", "rink_ice_nhl.mcd"));
        if (reference == null)
            Assert.Inconclusive("references/rink_ice_nhl.mcd is not here (it is the user's file, not part of the repository).");
        var model = McdFile.Parse(File.ReadAllBytes(reference));
        Assert.IsTrue(FullIce.IsFullIceModel(model));
        var frame = FullIce.FrameOf(model)!.Value;
        Assert.AreEqual(FullIce.FrameLength, frame.Length, 0.05);
        Assert.AreEqual(FullIce.FrameWidth, frame.Width, 0.05);
    }

    [TestMethod]
    public void StockModelAndSlotAreWhatTheFitAssumes()
    {
        string? game = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && game == null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
            if (File.Exists(Path.Combine(candidate, "eboot.bin")))
                game = candidate;
        }
        if (game == null)
            Assert.Inconclusive("No NHL 19 install found.");
        using var vfs = VirtualFileSystem.Mount(GameInstall.Open(game, useModsFolder: false));
        var model = McdFile.Parse(vfs.ReadContent(vfs.Find(FullIce.ModelPath)!));
        Assert.IsFalse(FullIce.IsFullIceModel(model));
        Assert.AreEqual(10.42, FullIce.FrameOf(model)!.Value.Length, 0.05);
        var slot = GnfTexture.Parse(vfs.ReadContent(vfs.Find(FullIce.SlotPath(27))!));
        Assert.AreEqual(1024, slot.Info.Width);
        Assert.AreEqual(1024, slot.Info.Height);
    }
}
