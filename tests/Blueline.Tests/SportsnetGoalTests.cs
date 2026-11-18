using Blueline.Core.Overlays;
using Blueline.Formats.Flash;

namespace Blueline.Tests;

/// <summary>The Sportsnet goal script against the edges measured on goal.mp4 (docs/SPORTSNET_BUG.md section 3), and the code it
/// compiles to against the game's own swf.</summary>
[TestClass]
public sealed class SportsnetGoalTests
{
    private static readonly List<ClockStep> Script = SportsnetGoal.Script();

    private static double Field(string name) => name switch
    {
        "_posWGoalMaskShow" => SportsnetBug.GoalWidth,
        "_posWGoalMaskHide" => 0,
        "_posWContentsMaskShow" => SportsnetBug.GoalWidth + 6,
        _ => throw new KeyNotFoundException(name),
    };

    private static double At(ClockNode node, string property, double video, double before = 0) =>
        ClockScript.ValueAt(Script, node, property, SportsnetGoal.ScriptFrame(video), before, Field);

    /// <summary>Video x of an edge that is this many design units right of the goal bar's left edge (video x 404).</summary>
    private static double VideoX(double design) => 404 + design / SportsnetGoal.Unit;

    private static void Edge(string what, double measured, double design, double tolerance) =>
        Assert.AreEqual(measured, VideoX(design), tolerance, what);

    [TestMethod]
    public void BarsGrowWhereTheVideoHasThem()
    {
        // Blue, quadIn: f10 435 to f16 1751.
        foreach (var (f, x) in new[] { (11, 509.0), (12, 635.0), (13, 829.0), (14, 1093.0), (15, 1417.0), (16, 1751.0) })
            Edge("blue f" + f, x, At(SportsnetGoal.Goal, "width", f), 35);

        // White band's leading and trailing edges, then the team colour.
        foreach (var (f, x) in new[] { (18, 1066.0), (19, 1319.0), (20, 1489.0), (22, 1665.0), (24, 1737.0) })
            Edge("white f" + f, x, At(SportsnetGoal.Bar(SportsnetGoal.White), "width", f), 60);
        foreach (var (f, x) in new[] { (20, 933.0), (21, 1180.0), (22, 1375.0), (24, 1600.0), (26, 1700.0) })
            Edge("white trailing f" + f, x, At(SportsnetGoal.Bar(SportsnetGoal.Blue2), "width", f), 55);
        foreach (var (f, x) in new[] { (23, 1028.0), (24, 1398.0), (25, 1588.0), (26, 1670.0), (28, 1735.0) })
            Edge("team colour f" + f, x, At(SportsnetGoal.Bar(SportsnetGoal.Tint), "width", f), 65);
    }

    [TestMethod]
    public void BugRetractsAndBuildsAgainWhereTheVideoHasIt()
    {
        double Mask(double f) => At(SportsnetGoal.Mask, "width", f, SportsnetBug.GoalWidth + 6) - SportsnetBug.PanelLeft;
        Edge("rest before", 1751, Mask(120), 3);
        foreach (var (f, x) in new[] { (126, 1668.0), (128, 1509.0), (129, 1339.0), (130, 1075.0), (131, 803.0), (132, 591.0), (134, 515.0) })
            Edge("retract f" + f, x, Mask(f), 110);
        foreach (var (f, x) in new[] { (137, 667.0), (139, 881.0), (141, 1139.0), (143, 1287.0), (145, 1372.0), (148, 1443.0) })
            // What shows is the mask's edge or the bug's own end (video x 1497), whichever is less.
            Edge("rebuild f" + f, x, Math.Min(Mask(f), SportsnetGoal.MaskRest - SportsnetBug.PanelLeft), 110);
        Assert.AreEqual(SportsnetBug.GoalWidth + 6, At(SportsnetGoal.Mask, "width", 170, SportsnetBug.GoalWidth + 6), 0.001, "open again at the end");
        Assert.AreEqual(0, At(SportsnetGoal.Goal, "width", 140), 0.001, "goal bar gone before the bug comes back");
    }

    [TestMethod]
    public void LogoAndLettersComeAndGoOnTheirFrames()
    {
        Assert.AreEqual(0, At(SportsnetGoal.Logo, "alpha", 20, 1), 0.001);
        Assert.AreEqual(1, At(SportsnetGoal.Logo, "alpha", 30), 0.001);
        Assert.AreEqual(0, At(SportsnetGoal.Logo, "alpha", 45), 0.001);
        Assert.AreEqual(SportsnetGoal.SwirlAlpha, At(SportsnetGoal.Logo, "alpha", 60), 0.001, "the faint swirl behind GOAL");
        Assert.AreEqual(SportsnetGoal.SwirlScale, At(SportsnetGoal.Logo, "scaleX", 100), 0.001);
        Assert.AreEqual(SportsnetGoal.StreakAlpha, At(SportsnetGoal.Bar(SportsnetGoal.Streak), "alpha", 100), 0.001);
        Assert.AreEqual(0, At(SportsnetGoal.Bar(SportsnetGoal.Streak), "alpha", 50), 0.001);
        Assert.AreEqual(126, At(SportsnetGoal.Logo, "scaleX", 24) * SportsnetGoal.LogoArt / SportsnetGoal.Unit, 2, "126 video px wide at f24");
        Assert.AreEqual(192, At(SportsnetGoal.Logo, "scaleX", 37) * SportsnetGoal.LogoArt / SportsnetGoal.Unit, 2, "192 at f37");

        // G, L, A, O in, solid two frames after each starts; O, A, L, G out.
        var g = SportsnetGoal.Letter(0);
        var o = SportsnetGoal.Letter(1);
        Assert.AreEqual(0, At(g, "alpha", 64), 0.001);
        Assert.AreEqual(1, At(g, "alpha", 66.5), 0.001);
        Assert.AreEqual(0, At(o, "alpha", 70), 0.001);
        Assert.AreEqual(1, At(o, "alpha", 100), 0.001);
        Assert.AreEqual(0, At(o, "alpha", 128), 0.001);
        Assert.AreEqual(1, At(g, "alpha", 128), 0.001);

        // The spread: the text box grows 85 video px by f121 over three equal gaps, so G, an outer letter, moves 1.5 gaps (42.5 px).
        Assert.AreEqual(42.5 * SportsnetGoal.Unit, At(g, "x", 73) - At(g, "x", 121), 0.2);
    }

    [TestMethod]
    public void ScaledLettersAreCentredOnTheBar()
    {
        // Middle of the scaled caps on the bar's middle, top to bottom.
        var capMiddle = SportsnetGoal.LetterY + (SportsnetGoal.CapTop + SportsnetGoal.CapHeight / 2) * SportsnetGoal.LetterScaleY;
        Assert.AreEqual(SportsnetBug.Height / 2, capMiddle, 0.1);
        Assert.AreEqual(0.79, SportsnetGoal.CapHeight * SportsnetGoal.LetterScaleY / SportsnetBug.Height, 0.02, "caps 79 percent of the bar, as broadcast");

        // Side to side: the four scaled boxes, as placed when they fade in (f73), are centred on the bar.
        var lefts = Enumerable.Range(0, 4).Select(i => At(SportsnetGoal.Letter(i), "x", 73)).ToList();
        var middle = (lefts[0] + lefts[3] + SportsnetGoal.LetterWidth * SportsnetGoal.LetterScaleX) / 2;
        Assert.AreEqual(SportsnetBug.GoalWidth / 2, middle, 0.2);
        Assert.AreEqual(SportsnetGoal.LetterScaleY, At(SportsnetGoal.Letter(0), "scaleY", 80, 1), 0.001);
    }

    [TestMethod]
    public void StretchedScriptIsTheSameShapeSlower()
    {
        var slow = SportsnetGoal.Script(SportsnetGoal.GameStretch);
        Assert.AreEqual(ClockScript.Length(Script) * SportsnetGoal.GameStretch, ClockScript.Length(slow), 0.01);
        var f = SportsnetGoal.ScriptFrame(13);
        Assert.AreEqual(ClockScript.ValueAt(Script, SportsnetGoal.Goal, "width", f, 0, Field),
            ClockScript.ValueAt(slow, SportsnetGoal.Goal, "width", f * SportsnetGoal.GameStretch, 0, Field), 0.01);
    }

    [TestMethod]
    public void NoTwoAnimationsOfOneValueOverlap()
    {
        // The engine runs overlapping animations of one value in some order of its own; the script never relies on it.
        var anims = ClockScript.Starts(Script).Where(s => s.Step is ClockAnim).Select(s => (s.Start, Anim: (ClockAnim)s.Step)).ToList();
        foreach (var (start, a) in anims)
            foreach (var (start2, b) in anims)
                if (!ReferenceEquals(a, b) && a.Node == b.Node && start2 > start && start2 < start + a.Frames - 1e-6)
                    Assert.IsFalse(a.Values.Any(v => b.Values.Any(w => w.Property == v.Property)), $"{a.Node} at {start} and {start2}");
    }

    [TestMethod]
    public void CompilesIntoTheGamesCodeAndDecodes()
    {
        var swfBytes = ReadUiCodeOrSkip();
        var built = ClockScript.CompileInto(swfBytes, "_createScriptAnimGoal", Script);
        var (abc, _) = FlashTests.OpenClock(SwfFile.Parse(built));
        var body = abc.BodyOf(abc.MethodOf(abc.Class(ClockScript.ClockClass), "_createScriptAnimGoal"));
        var code = AbcCode.Decode(body.Code);
        Assert.AreEqual(body.Code.Length, code.Sum(i => i.Length));
        Assert.AreEqual("returnvalue", code[^1].Name);

        int Count(string method) => code.Count(i => i.Name == "callpropvoid" && abc.NameOf(i.Operands[0]) == method);
        Assert.AreEqual(Script.Count(s => s is ClockAnim), Count("addAnim"));
        Assert.AreEqual(Script.Count(s => s is ClockProps), Count("addProps"));
        Assert.AreEqual(Script.Count(s => s is ClockDelay), Count("addDelay"));
        Assert.AreEqual(Script.Count(s => s is ClockCall), Count("addCall"));
        Assert.IsTrue(body.MaxStack is >= 8 and <= 20, "max stack " + body.MaxStack);
        Assert.IsTrue(code.Where(i => i.Name == "pushstring").Any(i => abc.Strings[i.Operands[0]] == SportsnetGoal.Letters[3]), "finds the new nodes by name");

        // Compiling again from the built file gives the same bytes: nothing is added twice.
        Assert.IsTrue(ClockScript.CompileInto(built, "_createScriptAnimGoal", Script).AsSpan().SequenceEqual(built));
    }

    private static byte[] ReadUiCodeOrSkip()
    {
        string? game = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && game == null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "NHL 19", "CUSA11126", "eboot.bin")))
                game = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
        if (game == null)
            Assert.Inconclusive("The game is not here.");
        using var vfs = Blueline.Core.VirtualFileSystem.Mount(Blueline.Core.GameInstall.Open(game, useModsFolder: false));
        return vfs.ReadContent(vfs.Find(ClockScript.UiCode)!);
    }
}
