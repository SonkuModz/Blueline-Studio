namespace Blueline.Core.Overlays;

/// <summary>
/// The Sportsnet goal animation (docs/SPORTSNET_BUG.md section 3), measured frame by frame on goal.mp4 and written as a score
/// clock script. Frames below are the video's own frame numbers, so each line can be checked against the doc's table; the
/// script starts at <see cref="Start"/>, when the blue bar begins to grow. Distances are in the layout's design units
/// (video pixels times <see cref="Unit"/>), measured from the goal bar's left edge, which is the SN tile's right edge.
/// Not done here: the team name (FLAMES) and its glitch, the relief texture of the team name, and the italic GOAL,
/// because the game gives the clock no team nickname and has no italic cut of the font.
/// </summary>
public static class SportsnetGoal
{
    /// <summary>Video frame at which the blue bar starts.</summary>
    public const double Start = 9.2;

    /// <summary>Design units per video pixel: the video is zoomed 3.07 times and the design is 1.953 times native.</summary>
    public const double Unit = SportsnetBug.Zoom / 3.07;

    public const string Blue = "snGoalBlue";
    public const string White = "snGoalWhite";
    public const string Blue2 = "snGoalBlue2";
    public const string Tint = "tint";
    public const string Streak = "snGoalStreak";

    /// <summary>From f58 the bar keeps a large faint swirl shaped like the logo (video x about 640 to 960, y 270 to 470) and a thin pale
    /// streak low on the right (x 1340 to 1550, y 428 to 440). The swirl is the goal logo again, big and nearly transparent.</summary>
    /// <summary>Centred in the bar (seen in game 2026-11-15: the broadcast's left of centre place looked off).</summary>
    public const double SwirlX = SportsnetBug.GoalWidth / 2;
    public const double SwirlY = SportsnetBug.Height / 2;
    public static readonly double SwirlScale = Math.Round(320 * Unit / LogoArt, 3);
    public const double SwirlAlpha = 0.16;
    public static readonly (double X, double Y, double Width, double Height) StreakBox =
        (Math.Round((1340 - 404) * Unit, 1), Math.Round((428 - 226) * Unit, 1), Math.Round(210 * Unit, 1), Math.Round(12 * Unit, 1));
    public const double StreakAlpha = 0.35;

    public static readonly string[] Letters = ["snGoalG", "snGoalO", "snGoalA", "snGoalL"];

    /// <summary>Distance between the letters' centres. The broadcast's text box at f73 is 614 video px for four letters (97.5 units
    /// each); this font is wider, so a little more.</summary>
    public const double LetterSpacing = 100;

    /// <summary>Letter centres, centred on the bar (the user saw the broadcast's own 4 unit offset as off centre).</summary>
    public static readonly double[] LetterCentres = Enumerable.Range(0, 4).Select(i => SportsnetBug.GoalWidth / 2 + (i - 1.5) * LetterSpacing).ToArray();

    public const double LetterWidth = 140;

    /// <summary>
    /// How big the game draws the letters. Calibrated in game 2026-11-15: fontSize (180 or 240), box height and box width all drew the
    /// same size, with caps 59 percent of the bar; only the script's scaleX and scaleY changed it (L at 1.5 was 87 percent, too big).
    /// The broadcast's caps are 79 percent, so 1.34 high. The font is wider than the broadcast's condensed italic, so only 1.1 wide,
    /// which keeps the letters apart and gives them the narrower look.
    /// </summary>
    public const double LetterScaleX = 1.1;
    public const double LetterScaleY = 1.34;

    /// <summary>Unscaled, the caps start 3 units below the label's top and are 96 high (measured in game). The script scales the
    /// label from its top left corner, so this y puts the scaled caps' middle on the bar's middle.</summary>
    public const double CapTop = 3;
    public const double CapHeight = 96;
    public static readonly double LetterY = Math.Round(SportsnetBug.Height / 2 - (CapTop + CapHeight / 2) * LetterScaleY, 1);

    /// <summary>A letter's left edge for a cell centre, so its scaled box stays centred on it.</summary>
    public static double LetterLeft(double centre) => Math.Round(centre - LetterWidth * LetterScaleX / 2, 1);

    /// <summary>Ignored by the game for size (see <see cref="LetterScaleY"/>); kept at the size the calibration used.</summary>
    public const double LetterSize = 240;

    /// <summary>Logo centre: 685 video px in from the bar's left and 117 down from its top; 126 video px wide at f24.</summary>
    public const double LogoX = SportsnetBug.GoalWidth / 2;
    public const double LogoY = SportsnetBug.Height / 2;
    public const double LogoArt = 117;
    public static readonly double LogoScale = Math.Round(126 * Unit / LogoArt, 3);

    /// <summary>The bug's right edge stops at video x 515 when the goal bar has gone, and the rebuilt bug ends at 1497.</summary>
    public static readonly double MaskRetracted = Math.Round((515 - 404) * Unit + SportsnetBug.PanelLeft, 1);
    public static readonly double MaskRest = Math.Round((1497 - 404) * Unit + SportsnetBug.PanelLeft, 1);

    public static readonly ClockNode Goal = new("_mGoalGroup");
    public static readonly ClockNode Logo = new("_mGoalLogoGroup");
    public static readonly ClockNode Mask = new("_mContentsMask");
    public static ClockNode Bar(string name) => new("_mGoalGroup", name);
    public static ClockNode Letter(int i) => new("_mGoalGroup", Letters[i]);

    private static (string, ClockValue)[] P(params (string, ClockValue)[] values) => values;

    /// <summary>The game's version runs this much slower than the broadcast (the user found the broadcast speed a little fast).</summary>
    public const double GameStretch = 1.2;

    /// <summary>The script, at the broadcast's speed or stretched by a factor (every start and length times it).</summary>
    public static List<ClockStep> Script(double stretch = 1)
    {
        var timeline = new List<(double Video, ClockStep Step)>();
        void At(double video, ClockStep step) => timeline.Add((video, step));
        void Anim(double video, ClockNode node, double frames, string easing, params (string, ClockValue)[] values) =>
            At(video, new ClockAnim(node, values, frames * stretch, easing));

        // Starting state, set again on every goal.
        At(Start, new ClockProps(Goal, P(("width", 0))));
        foreach (var bar in new[] { White, Blue2, Tint })
            At(Start, new ClockProps(Bar(bar), P(("width", 0))));
        At(Start, new ClockProps(Bar(Streak), P(("alpha", 0))));
        At(Start, new ClockProps(Logo, P(("x", LogoX), ("y", LogoY), ("alpha", 0), ("scaleX", LogoScale), ("scaleY", LogoScale))));
        for (var i = 0; i < 4; i++)
            At(Start, new ClockProps(Letter(i), P(("x", LetterLeft(LetterCentres[i])), ("y", LetterY), ("alpha", 0), ("scaleX", LetterScaleX), ("scaleY", LetterScaleY))));

        // As the stock script: drop-downs away, then the goal's colour and logo filled in.
        At(Start, new ClockCall("_runAnimHideAllDropdownsScriptInShowGoal"));
        At(Start, new ClockWait());
        At(Start, new ClockCall("_runAnimShowGoalFillAssets"));

        // f10 to f16: the blue bar grows over the whole bug (the goal group's own width clips it). Then three 8 frame wipes:
        // white band (7 fits its edge better than the 8 the doc gives), the blue behind it, the team colour.
        Anim(Start, Goal, 6.8, "quadIn", ("width", ClockValue.Of("_posWGoalMaskShow")));
        Anim(16.0, Bar(White), 7, "quadOut", ("width", SportsnetBug.GoalWidth));
        Anim(18.2, Bar(Blue2), 8, "quadOut", ("width", SportsnetBug.GoalWidth));
        Anim(21.1, Bar(Tint), 6.5, "quadOut", ("width", SportsnetBug.GoalWidth));

        // f23 to f41: the logo shows, grows slowly (126 to 192 px wide by f37), zooms through the camera and fades.
        Anim(22.5, Logo, 1, "linear", ("alpha", 1));
        var grown = Math.Round(LogoScale * 192 / 126, 3);
        Anim(24, Logo, 13, "quadIn", ("scaleX", grown), ("scaleY", grown));
        Anim(37, Logo, 4, "quadIn", ("scaleX", 6.8), ("scaleY", 6.8));
        Anim(39, Logo, 2, "linear", ("alpha", 0));

        // f58 on: the swirl and the streak, still, until the bar leaves.
        Anim(55, Logo, 1, "linear", ("x", SwirlX), ("y", SwirlY), ("scaleX", SwirlScale), ("scaleY", SwirlScale));
        Anim(57, Logo, 1, "linear", ("alpha", SwirlAlpha));
        Anim(57, Bar(Streak), 1, "linear", ("alpha", StreakAlpha));

        // f65 to f73: G, L, A, O fade in two frames apart, already in place.
        foreach (var (letter, video) in new[] { (0, 64.5), (3, 66.5), (2, 68.5), (1, 70.5) })
            Anim(video, Letter(letter), 2, "linear", ("alpha", 1));

        // f73 to f121: the letters spread by 85 video px in all, linear, centre fixed. f121 to f129: faster, 110 px more.
        double[] spread = [-1.5, -0.5, 0.5, 1.5];
        for (var i = 0; i < 4; i++)
        {
            var x = LetterLeft(LetterCentres[i]);
            Anim(73, Letter(i), 48, "linear", ("x", Math.Round(x + spread[i] * 85 / 3 * Unit, 1)));
            Anim(121, Letter(i), 8, "quadIn", ("x", Math.Round(x + spread[i] * 195 / 3 * Unit, 1)));
        }

        // f126 to f131: O, A, L, G fade out while the bug retracts to the left (video x 1751 to 515, f125.5 to f133.5).
        foreach (var (letter, video) in new[] { (1, 125.5), (2, 126.5), (3, 127.5), (0, 128.5) })
            Anim(video, Letter(letter), 2, "linear", ("alpha", 0));
        Anim(125.5, Mask, 8, "quadInOut", ("width", MaskRetracted));

        // Behind the mask: the new score goes in and the goal bar goes away. Then the bug builds again, f133.5 to f147.
        At(132, new ClockCall("_runFillTextForGoalStep"));
        Anim(133.5, Goal, 1, "linear", ("width", ClockValue.Of("_posWGoalMaskHide")));
        Anim(133.5, Mask, 13.5, "quadInOut", ("width", MaskRest));
        Anim(147.5, Mask, 1, "linear", ("width", ClockValue.Of("_posWContentsMaskShow")));
        At(148.5, new ClockCall("_runEndOfGoalAnimation"));
        At(148.5, new ClockWait());

        return ClockScript.FromTimeline(timeline.Select(t => ((t.Video - Start) * stretch, t.Step)));
    }

    /// <summary>Video frame for a script frame, and back.</summary>
    public static double ScriptFrame(double video) => video - Start;
}
