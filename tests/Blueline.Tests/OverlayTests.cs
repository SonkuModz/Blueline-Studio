using System.Text;
using System.Text.Json.Nodes;
using Blueline.Core;
using Blueline.Core.Overlays;
using Blueline.Formats.Scaleform;
using Blueline.Formats.Textures;

namespace Blueline.Tests;

/// <summary>Overlays against the game's own files.</summary>
[TestClass]
public sealed class OverlayTests
{
    private const string ScoreClockLayout = "nhl/ui/features/overlays/vvms/scoreclock/scoreclockoverlay.layout";
    private static VirtualFileSystem _vfs = null!;
    private static OverlayCatalog _catalog = null!;

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
            _catalog = OverlayCatalog.Build(Read, _vfs.Files.Select(f => f.Path));
    }

    [ClassCleanup]
    public static void Unmount() => _vfs?.Dispose();

    [TestInitialize]
    public void Setup()
    {
        if (_vfs == null)
            Assert.Inconclusive("The game is not here.");
    }

    private static byte[]? Read(string path) => _vfs.Find(path) is { } file ? _vfs.ReadContent(file) : null;

    private static (double, double)? SizeOf(string asset)
    {
        var path = _catalog.AssetPath(asset);
        return path != null && path.EndsWith(".gnf", StringComparison.Ordinal) && GameTexture.TryOpen(Read(path) ?? []) is { } texture ? (texture.Width, texture.Height) : null;
    }

    private static readonly OverlaySample Sample = new(new SampleTeam(1, "AWY", 0x112233, 2), new SampleTeam(2, "HOM", 0x445566, 3));

    private static PlacerOptions Screen(bool showHidden = false, LayoutWidget? reveal = null) => new()
    {
        ScreenWidth = 1920,
        ScreenHeight = 1080,
        ShowHidden = showHidden,
        Reveal = reveal,
        GameColour = Sample.Colour,
        GameBox = Sample.GameBox,
        Text = Sample.Text,
        Colour = value => value != null && value.StartsWith('$') && _catalog.Colours.TryGetValue(value[1..], out var rgb) ? rgb & 0xFFFFFF : null,
    };

    private static LayoutWidget WidgetOf(LayoutDocument document, string name) => document.All().First(w => w.Name == name);

    private static double NumberAt(OverlayTimeline timeline, double frames, LayoutWidget widget, string key) =>
        JsonEdit.Number(timeline.At(OverlayScript.Frames(frames)).Get(widget, key)) ?? double.NaN;

    [TestMethod]
    public void ScriptsRunTogetherUntilTheyAreToldToWait()
    {
        var document = LayoutDocument.Parse(Read(ScoreClockLayout)!);
        var a = WidgetOf(document, "capLeft");
        var b = WidgetOf(document, "capRight");
        var script = new OverlayScript();
        script.To(a, "alpha", 0, 10, OverlayEase.Linear).To(b, "alpha", 0, 4, OverlayEase.Linear);
        script.Wait().To(a, "x", 100, 10, OverlayEase.Linear);
        script.Delay(2).To(b, "x", 50, 2, OverlayEase.Linear);
        var timeline = new OverlayTimeline(script, new OverlayPose());

        Assert.AreEqual(OverlayScript.Frames(20), timeline.Duration, 0.001);
        Assert.AreEqual(0.5, NumberAt(timeline, 5, a, "alpha"), 0.001);
        Assert.AreEqual(0, NumberAt(timeline, 5, b, "alpha"), 0.001);
        Assert.IsFalse(timeline.At(OverlayScript.Frames(9)).Has(a, "x"), "Wait holds later steps back");
        Assert.AreEqual(50, NumberAt(timeline, 15, a, "x"), 0.001);
        Assert.AreEqual(50, NumberAt(timeline, 14, b, "x"), 0.001);
    }

    [TestMethod]
    public void TwoAnimationsOnOnePropertyBothStartFromWhereItWas()
    {
        var document = LayoutDocument.Parse(Read(ScoreClockLayout)!);
        var cap = WidgetOf(document, "capLeft");
        var start = new OverlayPose();
        start.Set(cap, "x", 248);
        var script = new OverlayScript();
        script.To(cap, "x", -488, 10, OverlayEase.Linear).To(cap, "x", -441, 10, OverlayEase.Linear);
        var timeline = new OverlayTimeline(script, start);

        Assert.AreEqual(248 + (-441 - 248) * 0.5, NumberAt(timeline, 5, cap, "x"), 0.001);
    }

    [TestMethod]
    public void EasingMatchesTheGamesCurves()
    {
        Assert.AreEqual(0.25, OverlayTimeline.Ease(OverlayEase.QuadIn, 0.5), 0.0001);
        Assert.AreEqual(0.75, OverlayTimeline.Ease(OverlayEase.QuadOut, 0.5), 0.0001);
        Assert.AreEqual(0.125 * 1, OverlayTimeline.Ease(OverlayEase.QuadInOut, 0.25), 0.0001);
        Assert.AreEqual(0.5, OverlayTimeline.Ease(OverlayEase.QuadInOut, 0.5), 0.0001);
    }

    [TestMethod]
    public void EveryScoreClockMomentPlaysAndFindsItsParts()
    {
        var document = LayoutDocument.Parse(Read(ScoreClockLayout)!);
        var scene = new ScoreClockScene(document, Sample);
        foreach (var choice in ScoreClockScene.Choices)
        {
            var timeline = scene.Build(choice.Event);
            if (choice.Event == ScoreClockEvent.InPlay)
            {
                Assert.IsNull(timeline);
                continue;
            }
            Assert.IsNotNull(timeline, choice.Name);
            Assert.IsTrue(timeline.FrameCount > 5, choice.Name);
            var placer = new LayoutPlacer(SizeOf, _ => null, Screen(showHidden: true) with { Posed = true, Styled = (w, k) => timeline.At(1000).Has(w, k) ? timeline.At(1000).Get(w, k) : _catalog.Styled(w, k) });
            Assert.IsTrue(placer.Place(document).Count > 20, choice.Name);
        }
    }

    [TestMethod]
    public void PowerPlayTabOpensToTheGamesHeightAndClosesAgain()
    {
        var document = LayoutDocument.Parse(Read(ScoreClockLayout)!);
        var timeline = new ScoreClockScene(document, Sample).Build(ScoreClockEvent.PowerPlayAway)!;
        var tab = WidgetOf(document, "awayDropDown");
        var bar = WidgetOf(document, "awayHighlight");

        Assert.AreEqual(0, NumberAt(timeline, 0, tab, "height"), 0.001);
        Assert.AreEqual(35, NumberAt(timeline, 40, tab, "height"), 0.001);
        Assert.AreEqual(35, NumberAt(timeline, 40, bar, "y"), 0.001);
        Assert.AreEqual(0, NumberAt(timeline, timeline.FrameCount, tab, "height"), 0.001);
        Assert.AreEqual(0, NumberAt(timeline, timeline.FrameCount, bar, "alpha"), 0.001);
    }

    [TestMethod]
    public void GoalLastsAsLongAsTheGamesScriptsAddUpTo()
    {
        var document = LayoutDocument.Parse(Read(ScoreClockLayout)!);
        var timeline = new ScoreClockScene(document, Sample).Build(ScoreClockEvent.GoalHome)!;
        var goal = WidgetOf(document, "goal");

        // 30 frames of bar, 15 ms, 87 frames of banner, then the 45 frame sweep and 15 frame fade of the team's panel.
        Assert.AreEqual(OverlayScript.Frames(30 + 87 + 45) + 15, timeline.Duration, 0.01);
        Assert.AreEqual(746, NumberAt(timeline, 60, goal, "width"), 0.001);
        Assert.AreEqual(0, NumberAt(timeline, 150, goal, "width"), 0.001);
        Assert.AreEqual("GOAL", JsonEdit.String(timeline.At(OverlayScript.Frames(60)).Get(WidgetOf(document, "text"), "text")));
        Assert.AreEqual(2, JsonEdit.Number(timeline.At(OverlayScript.Frames(60)).Get(goal.Children.First(c => c.Name == "logoMask").Children[0].Children[0], "id")));
    }

    [TestMethod]
    public void ClockSlidesInToWhereTheLayoutPutsIt()
    {
        var document = LayoutDocument.Parse(Read(ScoreClockLayout)!);
        var clock = WidgetOf(document, "scoreClock");
        clock.Node["x"] = 200.0;
        var timeline = new ScoreClockScene(document, Sample).Build(ScoreClockEvent.ShowClock)!;

        Assert.AreEqual(200 - 380, NumberAt(timeline, 0, clock, "x"), 0.001);
        Assert.AreEqual(200, NumberAt(timeline, timeline.FrameCount, clock, "x"), 0.001);
    }

    [TestMethod]
    public void SportsnetLookKeepsEveryNodeTheGamesCodeNeedsAndSetsTheBugsSize()
    {
        var original = Read(ScoreClockLayout)!;
        var document = LayoutDocument.Parse(original);
        var before = document.All().Select(w => w.Name).ToHashSet();
        Assert.IsNull(SportsnetBug.Problem(document));
        Assert.IsFalse(SportsnetBug.IsApplied(document));

        SportsnetBug.Apply(document);

        Assert.IsTrue(SportsnetBug.IsApplied(document));
        var after = document.All().Select(w => w.Name).ToHashSet();
        Assert.IsTrue(before.IsSubsetOf(after), "no node of the stock layout was removed or renamed");
        Assert.AreEqual(SportsnetBug.Scale, JsonEdit.Number(WidgetOf(document, "scoreClock").Node["scaleX"])!.Value, 0.005, "stored with two decimals");

        // Whole bug width at real size: tile + panels + clock cell, about the 440 pixels measured on the broadcast.
        var placed = new LayoutPlacer(SizeOf, _ => null, Screen()).Place(document);
        var tile = placed.First(p => p.Widget.Name == "snTile");
        var clockBg = placed.First(p => p.Widget.Name == "snClockBase");
        var width = clockBg.X + clockBg.Width * clockBg.ScaleX - tile.X;
        Assert.AreEqual(436, width, 8, "about 440 pixels wide at 1920 by 1080");
        Assert.AreEqual(84, tile.Height * tile.ScaleY, 1, "84 pixels high");
        Assert.AreEqual(tile.Width * tile.ScaleX, tile.Height * tile.ScaleY, 0.01, "the logo tile is square");
        Assert.AreEqual(SportsnetBug.ScreenX, tile.X, 0.01);
    }

    [TestMethod]
    public void SportsnetLookCanOnlyBeAppliedOnceAndNeedsTheScoreClock()
    {
        var document = LayoutDocument.Parse(Read(ScoreClockLayout)!);
        SportsnetBug.Apply(document);
        Assert.ThrowsExactly<InvalidOperationException>(() => SportsnetBug.Apply(document));

        var other = LayoutDocument.Parse(Read(_catalog.Overlays.First(o => o.Name == "TeamStats").Layouts.First().Path)!);
        Assert.IsNotNull(SportsnetBug.Problem(other));
        Assert.ThrowsExactly<InvalidOperationException>(() => SportsnetBug.Apply(other));
    }

    [TestMethod]
    public void SportsnetLookHidesWhatNeedsLiveDataAndStillPlaysEveryMoment()
    {
        var document = LayoutDocument.Parse(Read(ScoreClockLayout)!);
        SportsnetBug.Apply(document);
        var scene = new ScoreClockScene(document, Sample);
        foreach (var choice in ScoreClockScene.Choices.Where(c => c.Event != ScoreClockEvent.InPlay))
            Assert.IsNotNull(scene.Build(choice.Event), choice.Name);

        // The panels show no team logo at rest and the goal bar none at all.
        var placed = new LayoutPlacer(SizeOf, _ => null, Screen()).Place(document);
        Assert.IsFalse(placed.Any(p => p.Widget.Name == "logoAsset" && p.Widget.Parent!.Parent!.Name is "away" or "home"));
        Assert.IsFalse(placed.Any(p => p.Widget.Name == "logo" && p.Widget.Parent!.Name == "logoHolder"));
    }

    [TestMethod]
    public void FlatScorebugReplacesOnlyShadingTexturesAndKeepsTheirSizes()
    {
        var items = FlatScorebug.Build(_catalog, Read, includeFlat: true);
        Assert.AreEqual(FlatScorebug.Assets.Count, items.Count, "every shading texture exists in this game");

        // By default only the transparent ones, which are the ones confirmed in game.
        var safe = FlatScorebug.Build(_catalog, Read);
        Assert.AreEqual(FlatScorebug.ClearedAssets.Count, safe.Count);
        Assert.IsFalse(safe.Any(i => i.Path.EndsWith("timebgbase.gnf", StringComparison.Ordinal)), "the clock cell texture is not part of the default");
        Assert.IsFalse(items.Any(i => i.Path.EndsWith(".layout", StringComparison.Ordinal)), "no layout is touched, so nothing moves");

        foreach (var (path, content, _) in items)
        {
            var before = GameTexture.TryOpen(Read(path)!)!;
            var after = GameTexture.TryOpen(content);
            Assert.IsNotNull(after, path);
            Assert.AreEqual(before.Width, after.Width, path);
            Assert.AreEqual(before.Height, after.Height, path);
        }
    }

    [TestMethod]
    public void FlatScorebugClearsGradientsAndMakesBasesOneColour()
    {
        var items = FlatScorebug.Build(_catalog, Read, includeFlat: true).ToDictionary(i => Path.GetFileNameWithoutExtension(i.Path), i => GameTexture.TryOpen(i.Content)!.DecodeMip(0));

        // A top highlight is an add blend, so it has to end up as nothing.
        var highlight = items["colourbasetophl"];
        for (var i = 0; i < highlight.Width * highlight.Height; i++)
            Assert.IsTrue(highlight.Pixels[i * 4 + 3] <= 2, "fully transparent after encoding");

        // The silver base bar was a gradient, now a single solid colour.
        var bar = items["backgroundmid"];
        var rows = Enumerable.Range(0, bar.Height).Where(y => bar.Pixels[y * bar.Width * 4 + 3] >= 250).Select(y => bar.Pixels[y * bar.Width * 4 + 2]).ToList();
        Assert.IsTrue(rows.Max() - rows.Min() <= 4, "no gradient left");
        Assert.IsTrue(rows.Count >= bar.Height * 0.8, "solid, apart from the soft edge rows the original had");

        // The clock cell keeps its rounded shape, so it still has transparent corners.
        var cell = items["timebgbase"];
        Assert.IsTrue(Enumerable.Range(0, cell.Width * cell.Height).Any(i => cell.Pixels[i * 4 + 3] < 40), "outline kept");
    }

    [TestMethod]
    public void LogosUseTheGamesTableForEachTeam()
    {
        Assert.AreEqual((0.61, -11.0, -12.0), ScoreClockLogos.For(0));
        Assert.AreEqual((0.57, -8.0, -9.0), ScoreClockLogos.For(99999));
    }

    [TestMethod]
    public void AnAnimationCanSwitchAWidgetOffEvenWithHiddenPartsShown()
    {
        var document = LayoutDocument.Parse(Read(ScoreClockLayout)!);
        var stats = WidgetOf(document, "statDropDown");
        var pose = new OverlayPose();
        pose.Set(stats, "visible", false);
        var placed = new LayoutPlacer(SizeOf, _ => null, Screen(showHidden: true) with { Posed = true, Styled = (w, k) => pose.Has(w, k) ? pose.Get(w, k) : _catalog.Styled(w, k) }).Place(document);

        Assert.IsFalse(placed.Any(p => ReferenceEquals(p.Widget, stats)));
    }

    [TestMethod]
    public void ScoreClockFontMeasuresTheWayTheGameScreenshotDoes()
    {
        var line = FontLine.Read(Read(_catalog.FontPath("NBCSN_Frank Bold")!)!);
        Assert.IsNotNull(line);
        Assert.AreEqual(0.75, line.Value.Ascent, 0.001);
        Assert.AreEqual(0.25, line.Value.Descent, 0.001);

        // The score label: 53 point text at the top of a 50 pixel box.
        Assert.AreEqual(41.75, LabelPlacement.Baseline(line.Value, 53, 50, null), 0.001);
        Assert.AreEqual(41.75, LabelPlacement.Baseline(line.Value, 53, 50, "TOP"), 0.001);
        Assert.AreEqual((100 - 20) / 2.0 + 15, LabelPlacement.Baseline(line.Value, 20, 100, "CENTER"), 0.001);
        Assert.AreEqual(100 - 2 - 20 + 15, LabelPlacement.Baseline(line.Value, 20, 100, "BOTTOM"), 0.001);
    }

    [TestMethod]
    public void BrokenFontFilesAreNotRead()
    {
        Assert.IsNull(FontLine.Read([]));
        Assert.IsNull(FontLine.Read(new byte[64]));
    }

    [TestMethod]
    public void LabelsTakeTheirLookFromStylesUnlessTheySayOtherwise()
    {
        var layout = LayoutDocument.Parse(Read("nhl/ui/features/overlays/vvms/championsscore/championsscore.layout")!);
        var styled = layout.All().First(w => w.Kind == "Label" && w.Node["fontSize"] == null && w.Node["styles"] is JsonArray names && names.Any(n => n!.ToString() == "CHAMP_SCORE_OVERLAY"));
        var look = _catalog.Look(styled);
        Assert.AreEqual("NBCSN_Frank Regular", look.Face);
        Assert.AreEqual(24, look.Size);

        // A shared style, then the widget's own value on top of it.
        var node = new JsonObject { ["commonName"] = "Label", ["styles"] = new JsonArray("ULTRA_CONDENSED_60", "FONT_SWATCH_DEFAULT") };
        var label = new LayoutWidget(node, null);
        Assert.AreEqual("$UltraCondensed", _catalog.Look(label).Face);
        Assert.AreEqual(60, _catalog.Look(label).Size);
        Assert.AreEqual("$WHITE_PRIMARY", _catalog.Look(label).Colour);
        node["fontSize"] = 33.0;
        Assert.AreEqual(33, _catalog.Look(label).Size);

        Assert.AreEqual(20, _catalog.Look(new LayoutWidget(new JsonObject { ["commonName"] = "Label" }, null)).Size);
    }

    [TestMethod]
    public void ScoreClockHidesDropDownsUntilAskedAndSelectedWidgetsShow()
    {
        var document = LayoutDocument.Parse(Read(ScoreClockLayout)!);
        string[] Names(PlacerOptions options) => new LayoutPlacer(SizeOf, _ => null, options).Place(document).Select(p => p.Widget.Name).ToArray();

        var normal = Names(Screen());
        CollectionAssert.DoesNotContain(normal, "dropDowns");
        CollectionAssert.Contains(normal, "teamName");
        CollectionAssert.Contains(normal, "period");
        CollectionAssert.Contains(Names(Screen(showHidden: true)), "dropDowns");

        var inside = document.All().First(w => w.Name == "statDropDown");
        var revealed = Names(Screen(reveal: inside));
        CollectionAssert.Contains(revealed, "statDropDown");
        CollectionAssert.DoesNotContain(revealed, "homeDropDown");
    }

    [TestMethod]
    public void ScoreClockClipsLogosAndTintsTeamPanels()
    {
        var document = LayoutDocument.Parse(Read(ScoreClockLayout)!);
        var placed = new LayoutPlacer(SizeOf, _ => null, Screen()).Place(document);

        var logo = placed.First(p => p.Widget.Name == "logoAsset" && p.Widget.Parent!.Parent!.Name == "home");
        Assert.IsNotNull(logo.Clip, "The logo sits in a group that cuts it off.");
        Assert.AreEqual(73, logo.Clip!.Value.Right - logo.Clip.Value.Left, 0.01);
        var fit = ScoreClockLogos.For(Sample.Home.ArtId);
        Assert.AreEqual(128 * fit.Scale, logo.Width, 0.01, "The logo is scaled by the game's table, not drawn at the file's 128.");
        Assert.AreEqual(logo.Clip.Value.Left + fit.X, logo.X, 0.01, "It sits where the game's table puts it.");
        Assert.AreEqual(OverlaySample.PanelColour(0x445566), placed.First(p => p.Widget.Name == "tint" && p.Widget.Parent!.Name == "home").Tint);
        Assert.AreEqual(OverlaySample.PanelColour(0x112233), placed.First(p => p.Widget.Name == "tint" && p.Widget.Parent!.Name == "away").Tint);
        Assert.AreEqual(0x445566u, placed.First(p => p.Widget.Name == "glow1" && p.Widget.Parent!.Parent!.Name == "home").Tint, "The bottom glow is the team colour.");
        Assert.AreEqual("3", Sample.Text(placed.First(p => p.Widget.Name == "score" && p.Widget.Parent!.Name == "home").Widget));
    }

    [TestMethod]
    public void TeamLogosAndFontsResolveToGameFiles()
    {
        var logo = _catalog.AssetPath("team_logo_small_shiny", 2);
        Assert.AreEqual("nhl/ui/kettle_assets/teamlogos/smallglossy/t2.gnf", logo);
        Assert.IsNotNull(GameTexture.TryOpen(Read(logo!)!));
        var font = _catalog.FontPath("NBCSN_Frank Bold");
        Assert.IsNotNull(font);
        Assert.IsGreaterThan(1000, Read(font!)!.Length);
        Assert.IsNotNull(_catalog.FontPath(""), "Labels with no font use the game's default.");
    }

    [TestMethod]
    public void EveryMovieFrameCanBeLaidOutAndNamesRealTextures()
    {
        var movies = _catalog.Overlays.SelectMany(o => o.Movies).Select(m => m.Path).Distinct().ToList();
        Assert.IsNotEmpty(movies);
        var drawn = 0;
        foreach (var path in movies)
        {
            var timeline = GfxTimeline.Parse(Read(path)!);
            Assert.IsGreaterThan(0, timeline.StageWidth, path);
            foreach (var (_, name) in timeline.Images)
                Assert.IsNotNull(_catalog.MoviePicturePath(path, name), $"{path} names {name}");
            for (var frame = 0; frame < timeline.FrameCount; frame++)
                drawn += timeline.Frame(frame).Count;
        }
        Assert.IsGreaterThan(0, drawn);
    }

    [TestMethod]
    public void RobocamFlipbookShowsADifferentPictureEachFrame()
    {
        var path = _catalog.Overlays.Single(o => o.Name == "Robocam").Movies.Single().Path;
        var timeline = GfxTimeline.Parse(Read(path)!);
        var first = timeline.Frame(10).Where(p => p.ImageId >= 0).Select(p => p.ImageId).ToArray();
        var later = timeline.Frame(50).Where(p => p.ImageId >= 0).Select(p => p.ImageId).ToArray();
        Assert.IsNotEmpty(first);
        Assert.IsNotEmpty(later);
        CollectionAssert.AreNotEqual(first, later);
    }

    /// <summary>A layout and definition from boot.big and a texture from an inxp archive go through a real build and read back.</summary>
    [TestMethod]
    public void EditedOverlayBuildsIntoAnArchiveThatReadsBackTheEdits()
    {
        var folder = Path.Combine(Path.GetTempPath(), "blueline-overlay-build-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = ModProject.Create(Path.Combine(folder, "project"), "Overlay build");
            var layout = LayoutDocument.Parse(Read(ScoreClockLayout)!);
            LayoutDocument.Move(layout.All().First(w => w.Name == "clock"), -20, 0);
            var layoutBytes = layout.ToBytes();
            project.Set(ScoreClockLayout, layoutBytes, "Moved clock");

            var definitions = JsonEdit.Parse(Read(OverlayCatalog.DefinitionPath)!);
            definitions["overlays"]!.AsArray()[0]!["lifetime"] = 9;
            var definitionBytes = JsonEdit.ToBytes(definitions);
            project.Set(OverlayCatalog.DefinitionPath, definitionBytes, "Lifetime");

            var texturePath = _catalog.AssetPath("scoreclock_time_base")!;
            var textureBytes = Read(texturePath)!;
            project.Set(texturePath, textureBytes.Reverse().Reverse().ToArray(), "Texture");

            var result = OverlayBuilder.Build(project, _vfs, Path.Combine(folder, "out"));
            var read = new Dictionary<string, byte[]>();
            foreach (var built in result.Archives)
            {
                using var archive = Blueline.Formats.Eb.EbArchive.Open(built.Path);
                foreach (var entry in archive.Entries)
                    read[entry.Path] = archive.ReadContent(entry);
            }

            CollectionAssert.AreEqual(layoutBytes, read[ScoreClockLayout]);
            CollectionAssert.AreEqual(definitionBytes, read[OverlayCatalog.DefinitionPath]);
            Assert.IsTrue(read.ContainsKey(texturePath));
            Assert.IsTrue(result.Warnings.Any(w => w.Contains(ScoreClockLayout, StringComparison.Ordinal)), "boot.big files must carry the load-order warning until a game test settles it.");
            Assert.IsFalse(result.Warnings.Any(w => w.Contains(texturePath, StringComparison.Ordinal)), "Files from an inxp archive use the proven route.");
            Assert.IsNotNull(GfxMovie.IsMovie(Read(_catalog.Overlays.Single(o => o.Name == "Robocam").Movies.Single().Path)!));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public void CompositorBlendsAndClipsLikeTheGame()
    {
        static byte[] Pixel(OverlayLayer layer, uint backdrop) => OverlayCompositor.Compose(1, 1, backdrop, [layer]).Pixels[..3];
        var white = OverlayPicture.Solid(0xFFFFFF);
        var half = OverlayPicture.Solid(0x808080);
        OverlayLayer Over(OverlayPicture picture, OverlayBlend blend, uint tint = 0xFFFFFF, double alpha = 1, OverlayClip? clip = null) =>
            OverlayLayer.Axis(picture, 0, 0, 1, 1, 0, alpha, tint, blend, clip);

        CollectionAssert.AreEqual(new byte[] { 255, 0, 0 }, Pixel(Over(white, OverlayBlend.Normal, 0xFF0000), 0x0000FF), "A tint multiplies the picture.");
        CollectionAssert.AreEqual(new byte[] { 128, 128, 192 }, Pixel(Over(half, OverlayBlend.Add), 0x000040), "Add sums.");
        CollectionAssert.AreEqual(new byte[] { 64, 64, 64 }, Pixel(Over(half, OverlayBlend.Multiply), 0x808080).Select(b => (byte)(b + (b == 64 ? 0 : 0))).ToArray(), "Multiply darkens.");
        CollectionAssert.AreEqual(new byte[] { 192, 192, 192 }, Pixel(Over(half, OverlayBlend.Screen), 0x808080), "Screen lightens.");
        CollectionAssert.AreEqual(new byte[] { 128, 128, 128 }, Pixel(Over(white, OverlayBlend.Normal, alpha: 0.5), 0x000000).Select(b => (byte)(b == 127 ? 128 : b)).ToArray(), "Alpha fades.");
        CollectionAssert.AreEqual(new byte[] { 9, 9, 9 }, Pixel(Over(white, OverlayBlend.Normal, clip: new OverlayClip(5, 5, 6, 6)), 0x090909), "A clip cuts it off.");
    }

    [TestMethod]
    public void ScoreClockIsJoinedFromDefinitionToTextures()
    {
        var overlay = _catalog.Overlays.Single(o => o.Name == "ScoreClock");
        CollectionAssert.Contains(overlay.Definitions, "GameClock");
        Assert.IsTrue(overlay.Layouts.Any(f => f.Path == ScoreClockLayout));
        Assert.IsTrue(overlay.Art.Any(a => a.Path == "nhl/ui/kettle_assets/widgets/scoreclock/backgroundmid.gnf"), string.Join(" ", overlay.Art.Take(20).Select(a => a.Path)));
    }

    [TestMethod]
    public void EveryOverlayTextureAndMovieIsReachable()
    {
        var art = _catalog.Overlays.SelectMany(o => o.Art.Select(a => a.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var movies = _catalog.Overlays.SelectMany(o => o.Files.Where(f => f.Kind == OverlayFileKind.Movie).Select(f => f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = _vfs.Files.Where(f => f.Path.StartsWith("nhl/ui/") && f.Path.Contains("overlay"))
            .Where(f => f.Path.EndsWith(".gnf") ? !art.Contains(f.Path) : f.Path.EndsWith(".gfx") && !movies.Contains(f.Path)).Select(f => f.Path).ToList();
        Assert.IsEmpty(missing, string.Join(" ", missing.Take(10)));
    }

    [TestMethod]
    public void EveryDefinitionBelongsToAnOverlay()
    {
        var names = JsonEdit.Parse(Read(OverlayCatalog.DefinitionPath)!)["overlays"]!.AsArray().Select(o => o!["name"]!.ToString()).ToList();
        var linked = _catalog.Overlays.SelectMany(o => o.Definitions).ToHashSet();
        CollectionAssert.AreEquivalent(names.Distinct().ToList(), linked.ToList());
    }

    [TestMethod]
    public void OverlayTexturesAllDecode()
    {
        var failed = new List<string>();
        foreach (var path in _catalog.Overlays.SelectMany(o => o.Art.Select(a => a.Path)).Distinct())
        {
            var texture = GameTexture.TryOpen(Read(path) ?? []);
            if (texture?.Blocker != null || texture == null)
            {
                failed.Add(path);
                continue;
            }
            Assert.IsNotEmpty(texture.DecodeMip(texture.MipCount - 1).Pixels, path);
        }
        Assert.IsEmpty(failed, string.Join(" ", failed.Take(10)));
    }

    [TestMethod]
    public void MoviesListTheirTexturesBesideThem()
    {
        foreach (var file in _vfs.Files.Where(f => f.Path.Contains("swfassets/kettle/overlays") && f.Path.EndsWith(".gfx")))
        {
            var movie = GfxMovie.Parse(_vfs.ReadContent(file));
            Assert.IsNotEmpty(movie.Images, file.Path);
            var folder = file.Path[..(file.Path.LastIndexOf('/') + 1)];
            foreach (var image in movie.Images)
                Assert.IsNotNull(_vfs.Find(folder + image.FileName.ToLowerInvariant()), image.FileName);
        }
    }

    [TestMethod]
    public void EveryLayoutRoundTripsWithItsMeaningIntact()
    {
        var count = 0;
        foreach (var file in _vfs.Files.Where(f => f.Path.StartsWith("nhl/ui/") && f.Path.EndsWith(".layout") && f.StoredSize > 0))
        {
            var content = _vfs.ReadContent(file);
            if (string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(content)))
                continue;
            var written = LayoutDocument.Parse(content).ToBytes();
            Assert.IsTrue(JsonNode.DeepEquals(JsonEdit.Parse(content), JsonEdit.Parse(written)), file.Path);
            count++;
        }
        Assert.IsGreaterThan(1000, count);
    }

    [TestMethod]
    public void AnEditChangesOnlyTheValueAsked()
    {
        var content = Read(ScoreClockLayout)!;
        var document = LayoutDocument.Parse(content);
        var clock = document.Find("clock")!;
        LayoutDocument.Move(clock, 10, -2);
        var edited = JsonEdit.Parse(document.ToBytes());
        var original = JsonEdit.Parse(content);
        Assert.IsFalse(JsonNode.DeepEquals(original, edited));

        var again = LayoutDocument.Parse(document.ToBytes());
        LayoutDocument.Move(again.Find("clock")!, -10, 2);
        Assert.IsTrue(JsonNode.DeepEquals(original, JsonEdit.Parse(again.ToBytes())));
        StringAssert.Contains(Encoding.UTF8.GetString(document.ToBytes()), "\"x\": 510.0");
    }

    [TestMethod]
    public void TypedValuesKeepTheTypeTheGameHad()
    {
        Assert.AreEqual("\"12\"", JsonEdit.Value("12", JsonValue.Create("old"))!.ToJsonString());
        Assert.AreEqual("12.0", JsonEdit.Value("12", JsonEdit.Parse("3.5"))!.ToJsonString());
        Assert.AreEqual("12", JsonEdit.Value("12", JsonEdit.Parse("3"))!.ToJsonString());
        Assert.AreEqual("true", JsonEdit.Value("true", JsonEdit.Parse("false"))!.ToJsonString());
    }

    [TestMethod]
    public void DuplicateAndRemoveLeaveTheRestAlone()
    {
        var content = Read(ScoreClockLayout)!;
        var document = LayoutDocument.Parse(content);
        var before = document.All().Count();
        var name = document.Duplicate(document.Find("bgGradLeft")!);
        Assert.AreEqual(before + 1, document.All().Count());
        document.Remove(document.Find(name)!);
        Assert.IsTrue(JsonNode.DeepEquals(JsonEdit.Parse(content), document.RootNode));
    }

    [TestMethod]
    public void PlacerFollowsPositionsScaleAndMasks()
    {
        var document = LayoutDocument.Parse(Read(ScoreClockLayout)!);
        var placed = new LayoutPlacer(_ => (100, 40), _ => null).Place(document);
        var clockBg = placed.Single(p => p.Widget.Name == "bg" && p.Widget.Parent!.Name == "clock");
        Assert.AreEqual(94 + 7 + 500, clockBg.X, 0.01);
        Assert.AreEqual(74 - 4 + 2, clockBg.Y, 0.01);
        Assert.AreEqual(-1, placed.Single(p => p.Widget.Name == "bgGradRight").ScaleX, 0.01);
        Assert.IsFalse(placed.Any(p => p.Widget.Name == "contentsMask"));
    }

    [TestMethod]
    public void ColoursComeFromTheGamesConstants()
    {
        Assert.AreEqual(0x007EECu, _catalog.Colours["COBALT_BLUE"]);
    }

    [TestMethod]
    public void ReadingAnEditThroughTheCatalogFollowsTheProject()
    {
        var edited = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Read(ScoreClockLayout)!).Replace("\"type\": \"scoreclock_time_base\"", "\"type\": \"background_white\""));
        var catalog = OverlayCatalog.Build(p => p == ScoreClockLayout ? edited : Read(p), _vfs.Files.Select(f => f.Path));
        var art = catalog.Overlays.Single(o => o.Name == "ScoreClock").Art;
        Assert.IsTrue(art.Any(a => a.Usage == "background_white"));
    }
}
