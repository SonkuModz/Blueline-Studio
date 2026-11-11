using System.Text.Json.Nodes;
using static Blueline.Core.Overlays.OverlayEase;

namespace Blueline.Core.Overlays;

public enum ScoreClockEvent
{
    InPlay, ShowClock, HideClock, GoalAway, GoalHome, PowerPlayAway, PowerPlayHome, TwoManAdvantage, FourOnFour,
    DelayedPenaltyAway, DelayedPenaltyHome, PenaltyDetailsAway, PenaltyDetailsHome, EmptyNetAway, EmptyNetHome,
    FullWidthMessage, ClockWarning, Comparison, Shootout,
}

public sealed record ScoreClockChoice(string Name, ScoreClockEvent Event)
{
    public override string ToString() => Name;
}

/// <summary>
/// The score clock's moments, transcribed from nhl.overlays.scoreclock::ScoreClockOverlay in nhl/ui/lib/nhluidata.swf
/// (RESEARCH section 6.10). Positions the game reads from the layout are read from the layout here too, so edits show.
/// Numbers the game's code has written in stay written in.
/// </summary>
public sealed class ScoreClockScene
{
    public const string LayoutPath = "nhl/ui/features/overlays/vvms/scoreclock/scoreclockoverlay.layout";
    private const double SingleHeight = 35;
    private const double DoubleHeight = 60;
    private const double LogoMaskFull = 71;
    private const double LogoMaskNormal = 55;
    private const double PowerPlayHold = 60;
    private const double MessageHold = 60;
    private const uint PenaltyFill = 15577628;
    private const uint PenaltyBar = 13148950;
    private const uint White = 0xFFFFFF;

    public static IReadOnlyList<ScoreClockChoice> Choices { get; } =
    [
        new("Still: in play", ScoreClockEvent.InPlay),
        new("Clock slides in", ScoreClockEvent.ShowClock),
        new("Clock slides out", ScoreClockEvent.HideClock),
        new("Goal, away team", ScoreClockEvent.GoalAway),
        new("Goal, home team", ScoreClockEvent.GoalHome),
        new("Power play, away team", ScoreClockEvent.PowerPlayAway),
        new("Power play, home team", ScoreClockEvent.PowerPlayHome),
        new("Two man advantage, 5 on 3", ScoreClockEvent.TwoManAdvantage),
        new("Four on four", ScoreClockEvent.FourOnFour),
        new("Delayed penalty, away team", ScoreClockEvent.DelayedPenaltyAway),
        new("Delayed penalty, home team", ScoreClockEvent.DelayedPenaltyHome),
        new("Two line drop-down, away team", ScoreClockEvent.PenaltyDetailsAway),
        new("Two line drop-down, home team", ScoreClockEvent.PenaltyDetailsHome),
        new("Empty net, away team", ScoreClockEvent.EmptyNetAway),
        new("Empty net, home team", ScoreClockEvent.EmptyNetHome),
        new("Full width message", ScoreClockEvent.FullWidthMessage),
        new("Clock message (icing, offside)", ScoreClockEvent.ClockWarning),
        new("Team stat comparison", ScoreClockEvent.Comparison),
        new("Shootout", ScoreClockEvent.Shootout),
    ];

    private readonly LayoutDocument _document;
    private readonly OverlaySample _sample;
    private readonly LayoutWidget? _clock, _contents, _capRight, _mask, _away, _home, _clockGroup, _goal, _dropDowns, _labels, _warning;

    public ScoreClockScene(LayoutDocument document, OverlaySample sample)
    {
        _document = document;
        _sample = sample;
        _clock = Find(document.Root, "scoreClock");
        _contents = Find(_clock, "contents");
        _capRight = Find(_clock, "capRight");
        _mask = Find(_clock, "contentsMask");
        _away = Find(_contents, "away");
        _home = Find(_contents, "home");
        _clockGroup = Find(_contents, "clock");
        _goal = Find(_contents, "goal");
        _dropDowns = Find(_contents, "dropDowns");
        _labels = Find(_contents, "dropDownLabels");
        _warning = Find(_contents, "dropDownClockWarning");
    }

    public static bool Handles(string path) => string.Equals(path, LayoutPath, StringComparison.OrdinalIgnoreCase);

    private static LayoutWidget? Find(LayoutWidget? parent, string name)
    {
        if (parent == null)
            return null;
        foreach (var child in parent.Children)
        {
            if (child.Name == name)
                return child;
            if (Find(child, name) is { } deeper)
                return deeper;
        }
        return null;
    }

    private static double Own(LayoutWidget? widget, string key, double fallback = 0) => widget == null ? fallback : JsonEdit.Number(widget.Node[key]) ?? fallback;

    private static string Hex(uint colour) => $"0x{colour & 0xFFFFFF:X6}";

    private SampleTeam Team(bool away) => away ? _sample.Away : _sample.Home;

    private uint Fill(bool away) => OverlaySample.PanelColour(Team(away).Primary);

    private sealed record Side(LayoutWidget? Bar, LayoutWidget? Glow, LayoutWidget? Group, LayoutWidget? Logo, LayoutWidget? Fade, LayoutWidget? Grad, LayoutWidget? LabelGroup, LayoutWidget? Label, uint Primary, uint Secondary, uint PanelFill, bool Away);

    private Side SideOf(bool away)
    {
        var panel = away ? _away : _home;
        var logo = Find(panel, "logoMask");
        var group = Find(_dropDowns, away ? "awayDropDown" : "homeDropDown");
        var labelGroup = Find(_labels, away ? "dropDownLabel1" : "dropDownLabel2");
        return new Side(Find(_dropDowns, away ? "awayHighlight" : "homeHighlight"), Find(panel, "bottomGlow"), group, logo, Find(logo, "logoFader"),
            Find(group, "topGrad"), labelGroup, Find(labelGroup, "label"), Team(away).Primary, Team(away).Secondary, Fill(away), away);
    }

    /// <summary>The clock at rest, as _beginLoad and _hideDropDowns leave it.</summary>
    private OverlayPose Resting()
    {
        var pose = new OverlayPose();
        void Set(LayoutWidget? widget, string key, JsonNode? value)
        {
            if (widget != null)
                pose.Set(widget, key, value);
        }

        Set(Find(_clock, "topMessage"), "alpha", 0);
        foreach (var label in new[] { _goal, _dropDowns, _labels, _warning, Find(_clock, "topMessage") }.Where(r => r != null).SelectMany(r => Walk(r!)).Where(w => w.Kind == "Label"))
            Set(label, "text", "");
        Set(Find(_clockGroup, "label"), "text", "");

        var warningBack = Find(_warning, "dropDown");
        var warningBar = Find(_warning, "highlight");
        Set(warningBack, "height", 0);
        Set(warningBar, "alpha", 0);
        Set(warningBar, "y", 0);
        Set(Find(_warning, "label"), "alpha", 0);

        foreach (var away in new[] { true, false })
        {
            var side = SideOf(away);
            Set(Find(away ? _away : _home, "goalAnimation"), "alpha", 0);
            Set(side.Bar, "alpha", 0);
            Set(side.Group, "height", 0);
            Set(side.LabelGroup, "alpha", 0);
            Set(side.Fade, "alpha", 0);
            Set(side.Fade, "color", Hex(side.Primary));
            Set(side.Grad, "alpha", 0);
            Set(Find(side.Bar, "tint"), "color", Hex(side.Secondary));
            Set(Find(side.Bar, "tintGrad"), "color", Hex(side.Secondary));
        }

        Set(Find(_dropDowns, "clockHighlight"), "alpha", 0);
        Set(Find(_dropDowns, "clockDropDown"), "height", 0);
        Set(Find(_dropDowns, "dropDown"), "alpha", 0);
        Set(Find(_labels, "dropDownLabelSubString"), "alpha", 0);
        Set(Find(_labels, "dropDownLabelStats"), "alpha", 0);
        Set(Find(_labels, "dropDownClockInfoLabels"), "alpha", 0);
        var stats = Find(_dropDowns, "statDropDown");
        Set(stats, "height", 0);
        Set(stats, "visible", false);
        foreach (var name in new[] { "shootoutDropDown", "shootoutDropDownInternational", "shootoutExtraRounds", "outOfTownScores" })
        {
            Set(Find(_dropDowns, name), "width", 0);
            Set(Find(_dropDowns, name), "visible", false);
        }
        Set(_goal, "width", 0);
        return pose;
    }

    private static IEnumerable<LayoutWidget> Walk(LayoutWidget widget)
    {
        yield return widget;
        foreach (var child in widget.Children.SelectMany(Walk))
            yield return child;
    }

    /// <summary>What to play. Null for the still view, which the file alone describes.</summary>
    public OverlayTimeline? Build(ScoreClockEvent choice)
    {
        var pose = Resting();
        OverlayScript? script = choice switch
        {
            ScoreClockEvent.ShowClock => ShowClock(pose),
            ScoreClockEvent.HideClock => HideClock(),
            ScoreClockEvent.GoalAway => Goal(pose, true),
            ScoreClockEvent.GoalHome => Goal(pose, false),
            ScoreClockEvent.PowerPlayAway => PowerPlay(pose, true, 4, 5),
            ScoreClockEvent.PowerPlayHome => PowerPlay(pose, false, 5, 4),
            ScoreClockEvent.TwoManAdvantage => PowerPlay(pose, false, 5, 3),
            ScoreClockEvent.FourOnFour => PowerPlay(pose, null, 4, 4),
            ScoreClockEvent.DelayedPenaltyAway => Short(pose, true, "DELAYED PENALTY", null, true, false),
            ScoreClockEvent.DelayedPenaltyHome => Short(pose, false, "DELAYED PENALTY", null, true, false),
            ScoreClockEvent.PenaltyDetailsAway => Short(pose, true, "P. PLAYER", "HOOKING 2:00", false, false),
            ScoreClockEvent.PenaltyDetailsHome => Short(pose, false, "P. PLAYER", "HOOKING 2:00", false, false),
            ScoreClockEvent.EmptyNetAway => Short(pose, true, "EMPTY NET", null, false, true),
            ScoreClockEvent.EmptyNetHome => Short(pose, false, "EMPTY NET", null, false, true),
            ScoreClockEvent.FullWidthMessage => FullWidth(pose, true),
            ScoreClockEvent.ClockWarning => ClockMessage(pose),
            ScoreClockEvent.Comparison => Comparison(pose),
            ScoreClockEvent.Shootout => Shootout(pose),
            _ => null,
        };
        return script == null ? null : new OverlayTimeline(script, pose);
    }

    private OverlayScript ShowClock(OverlayPose pose)
    {
        var away = SideOf(true);
        var home = SideOf(false);
        var awayDivider = Find(_away, "scoreDivider");
        var homeDivider = Find(_home, "scoreDivider");
        LayoutWidget?[] text =
        [
            Find(_away, "teamName"), Find(_away, "score"), Find(_home, "teamName"), Find(_home, "score"),
            Find(_clockGroup, "period"), Find(_clockGroup, "time"), Find(_clockGroup, "label"),
        ];
        void Set(LayoutWidget? widget, string key, JsonNode? value)
        {
            if (widget != null)
                pose.Set(widget, key, value);
        }

        var clockShow = Own(_clock, "x");
        var capRightShow = Own(_capRight, "x");
        var maskWidth = Own(_mask, "width", 753);
        var awayShow = Own(_away, "x");
        var homeShow = Own(_home, "x");
        var clockTimeShow = Own(_clockGroup, "x");

        Set(_clock, "alpha", 0);
        Set(_clock, "x", clockShow - 380);
        Set(_capRight, "x", 6);
        Set(_mask, "width", 1);
        Set(_away, "x", -247);
        Set(_home, "x", -247);
        Set(_clockGroup, "x", -249);
        Set(awayDivider, "alpha", 0);
        Set(homeDivider, "alpha", 0);
        Set(away.Logo, "alpha", 0);
        Set(home.Logo, "alpha", 0);
        foreach (var label in text)
            Set(label, "alpha", 0);

        var s = new OverlayScript(0.75);
        s.To(_clock, "alpha", 1, 2, QuadInOut);
        s.To(_clock, "x", clockShow, 14, QuadOut);
        s.Delay(3);
        s.To(_capRight, "x", capRightShow, 11, QuadInOut);
        s.To(_mask, "width", maskWidth, 11, QuadInOut);
        s.Delay(6);
        s.To(_away, "x", awayShow, 8, QuadOut);
        s.Delay(3);
        s.To(_home, "x", homeShow, 8, QuadOut);
        s.Delay(3);
        s.To(away.Logo, "alpha", 1, 10, QuadOut);
        s.To(_clockGroup, "x", clockTimeShow, 8, QuadOut);
        s.Delay(1.5);
        s.Add(ShowText(text, 6, 2, 10));
        s.Delay(1.5);
        s.To(home.Logo, "alpha", 1, 10, QuadOut);
        s.To(awayDivider, "alpha", 1, 8, QuadInOut);
        s.Delay(3);
        s.To(homeDivider, "alpha", 1, 8, QuadInOut);
        return s.Wait();
    }

    private OverlayScript HideClock()
    {
        var s = new OverlayScript();
        s.Delay(15);
        s.To(_clock, "x", Own(_clock, "x") + 257, 12, QuadInOut);
        s.To(Find(_clock, "capLeft"), "x", 746, 12, QuadInOut);
        s.To(_capRight, "x", Own(_capRight, "x"), 12, QuadInOut);
        s.To(_mask, "x", 753, 12, QuadInOut);
        s.Delay(8);
        s.To(_clock, "alpha", 0, 4, QuadInOut);
        return s.Wait();
    }

    private static OverlayScript ShowText(IReadOnlyList<LayoutWidget?> targets, double frames, double stagger, double offset)
    {
        OverlayScript Fade()
        {
            var inner = new OverlayScript();
            foreach (var target in targets)
            {
                inner.To(target, "alpha", 1, frames, QuadOut);
                inner.Delay(stagger);
            }
            return inner;
        }

        OverlayScript Slide()
        {
            var inner = new OverlayScript();
            foreach (var target in targets)
            {
                inner.FromOffset(target, "x", -offset, frames, QuadOut);
                inner.Delay(stagger);
            }
            return inner;
        }

        return new OverlayScript().Add(Fade()).Add(Slide());
    }

    private static OverlayScript HideText(IReadOnlyList<LayoutWidget?> targets, double frames, double stagger, double offset)
    {
        var fade = new OverlayScript();
        var slide = new OverlayScript();
        foreach (var target in targets)
        {
            fade.To(target, "alpha", 0, frames, QuadIn);
            slide.By(target, "x", offset, frames, QuadIn);
            fade.Delay(stagger);
            slide.Delay(stagger);
        }
        return new OverlayScript().Add(fade).Add(slide);
    }

    private OverlayScript Goal(OverlayPose pose, bool away)
    {
        var side = SideOf(away);
        var bar = side.Bar;
        var text = Find(_goal, "text");
        var logoHolder = Find(_goal, "logoHolder");
        var logo = Find(logoHolder, "logo");
        var logoX = Own(logoHolder, "x");
        var logoScale = Own(logoHolder, "scaleX", 1);
        var goalWidth = Own(_goal, "width", 746);
        var scoring = Team(away);

        var s = new OverlayScript();
        s.Set(logoHolder, "x", logoX - 40).Set(logoHolder, "alpha", 0).Set(logoHolder, "scaleX", logoScale * 0.75).Set(logoHolder, "scaleY", logoScale * 0.75);
        s.Set(text, "x", 300).Set(text, "tracking", 10).Set(text, "alpha", 0.4);
        s.Set(text, "text", "GOAL").Set(text, "fontSize", 60).Set(text, "y", 3);
        s.Set(Find(side.Group, "tint"), "color", Hex(side.Primary));
        s.Set(Find(_goal, "glow"), "color", Hex(side.Primary));
        s.Set(Find(_goal, "glowSmall"), "color", Hex(side.Primary));
        s.Set(Find(_goal, "bottomGlow"), "color", Hex(side.Primary));
        s.Set(Find(_goal, "tint"), "fillColor", Hex(side.PanelFill));
        s.Set(logo, "id", scoring.ArtId);
        s.Set(Find(_goal, "highlightTop"), "color", Hex(side.Primary));

        s.To(bar, "alpha", 1, 30, QuadInOut).Wait();
        s.DelayMs(15);
        s.To(_goal, "width", goalWidth, 10, QuadOut);
        s.To(bar, "width", 743, 10, QuadOut).To(bar, "x", 0, 10, QuadOut);
        foreach (var name in new[] { "angles0", "angles1", "angles2" })
            s.Start(GoalStreaks(Find(_goal, name), side.Primary));
        s.To(text, "tracking", 25, 85, Linear).To(text, "x", 348, 85, Linear).To(text, "alpha", 1, 85, Linear);
        s.Delay(3);
        s.To(logoHolder, "x", logoX, 10, QuadOut).To(logoHolder, "alpha", 1, 10, QuadOut);
        s.Delay(2);
        s.To(logoHolder, "scaleX", logoScale, 82, QuadIn).To(logoHolder, "scaleY", logoScale, 82, QuadIn);
        s.Delay(10);
        s.Start(GoalAngles(Find(_goal, "angles2"), 200, -10));
        s.Delay(67);
        s.To(text, "tracking", 10, 10, QuadIn).To(text, "x", 250, 10, QuadIn).To(text, "alpha", 0, 10, QuadIn);
        s.Delay(5);
        s.Set(Find(away ? _away : _home, "score"), "text", (scoring.Score + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        s.To(_goal, "width", 0, 7, QuadInOut);
        s.To(logoHolder, "x", logoX - 40, 10, QuadOut).To(logoHolder, "alpha", 0, 10, QuadOut);
        s.Start(EndOfGoal(Find(away ? _away : _home, "goalAnimation"), side.Primary));
        s.To(bar, "width", away ? 248 : 247, 10, QuadOut).To(bar, "x", away ? 0 : 248, 10, QuadOut);
        s.Delay(10);
        s.To(bar, "alpha", 0, 10, QuadInOut);
        return s.Wait();
    }

    private static OverlayScript GoalStreaks(LayoutWidget? group, uint colour)
    {
        var a0 = Find(group, "angle0");
        var a1 = Find(group, "angle1");
        var a2 = Find(group, "angle2");
        var a3 = Find(group, "angle3");
        var a4 = Find(group, "angle4");
        var a5 = Find(group, "angle5");
        var s = new OverlayScript();
        var tint = Hex(colour);
        s.Set(a0, "x", -46).Set(a0, "color", tint);
        s.Set(a1, "x", -60).Set(a1, "color", tint);
        s.Set(a2, "x", -80).Set(a2, "color", tint);
        s.Set(a3, "x", -463).Set(a3, "color", tint);
        s.Set(a4, "x", -483).Set(a4, "color", tint);
        s.Set(a5, "x", -473).Set(a5, "width", 450);
        s.To(a0, "x", 1061, 11, Linear).Delay(3);
        s.To(a1, "x", 1061, 11, Linear).Delay(3);
        s.To(a2, "x", 982, 11, QuadOut).Delay(3);
        s.To(a3, "x", 918, 11, QuadOut).Delay(1);
        s.To(a4, "x", 889, 11, QuadOut).Delay(1);
        s.To(a5, "x", 437, 11, QuadOut).Delay(9);
        s.To(a3, "x", 957, 72, Linear).Delay(1);
        s.To(a4, "x", 965, 72, Linear).Delay(1);
        s.To(a5, "x", 417, 72, Linear).To(a5, "width", 520, 72, Linear);
        return s;
    }

    private static OverlayScript GoalAngles(LayoutWidget? group, double travel, double frameChange)
    {
        var a0 = Find(group, "angle0");
        var a1 = Find(group, "angle1");
        var a2 = Find(group, "angle2");
        var s = new OverlayScript();
        s.Set(a0, "x", 120).Set(a0, "width", 30);
        s.Set(a1, "x", 71).Set(a1, "width", 41);
        s.Set(a2, "x", 0).Set(a2, "width", 85);

        // The game adds a random shift of up to 20 pixels to each. The preview leaves it out so a frame always looks the same.
        var frames = 85 + frameChange;
        s.To(a0, "x", 580 + travel, frames, QuadOut).To(a0, "width", 45, frames, QuadOut);
        s.To(a1, "x", 493 + travel, frames, QuadOut).To(a1, "width", 75, frames, QuadOut);
        s.To(a2, "x", 561 + travel, frames, QuadOut).To(a2, "width", 44, frames, QuadOut);
        return s;
    }

    private static OverlayScript EndOfGoal(LayoutWidget? clip, uint colour)
    {
        var angles0 = Find(clip, "angles0");
        var angles1 = Find(clip, "angles1");
        var a0 = clip?.Children.FirstOrDefault(c => c.Name == "angle0");
        var a1 = clip?.Children.FirstOrDefault(c => c.Name == "angle1");
        var a2 = clip?.Children.FirstOrDefault(c => c.Name == "angle2");
        var s = new OverlayScript();
        var tint = Hex(colour);
        foreach (var part in new[] { a1, a2, Find(angles0, "angle0"), Find(angles0, "angle1"), Find(angles0, "angle2"), Find(angles1, "angle0"), Find(angles1, "angle1"), Find(angles1, "angle2") })
            s.Set(part, "color", tint);
        s.Set(angles0, "x", 248).Set(angles1, "x", 459).Set(a0, "x", 255).Set(a1, "x", 246).Set(a2, "x", 256);
        s.Set(clip, "alpha", 1);

        // Both of these move angles0 in the game's code, so angles1 stays where it was put.
        s.To(angles0, "x", -488, 28, QuadOut);
        s.To(angles0, "x", -441, 28, QuadOut);
        s.To(a0, "x", 55, 45, QuadOut);
        s.To(a1, "x", 115, 45, Linear);
        s.To(a2, "x", 92, 45, Linear);
        s.Delay(30);
        s.To(clip, "alpha", 0, 15, QuadInOut);
        return s;
    }

    /// <param name="away">Which team has it, or null for an even strength drop-down under the clock.</param>
    private OverlayScript PowerPlay(OverlayPose pose, bool? away, int homeStrength, int awayStrength)
    {
        var side = away is { } team ? SideOf(team) : null;
        var bar = side?.Bar ?? Find(_dropDowns, "clockHighlight");
        var group = side?.Group ?? Find(_dropDowns, "clockDropDown");
        var labels = Find(_labels, "dropDownClockInfoLabels");
        var labelGroup = Find(labels, "clockLabel");
        var timeGroup = Find(labels, "clockTime");
        var title = Find(labelGroup, "label");
        var clockTime = Find(timeGroup, "label");
        var groupX = Own(group, "x");
        void Set(LayoutWidget? widget, string key, JsonNode? value)
        {
            if (widget != null)
                pose.Set(widget, key, value);
        }

        Set(clockTime, "text", "1:45");
        var gap = Math.Abs(homeStrength - awayStrength);
        if (gap > 1 || homeStrength == awayStrength)
        {
            Set(timeGroup, "x", 157);
            Set(labelGroup, "x", 0);
            Set(title, "width", 125);
            Set(title, "textAlignH", "CENTER");
            Set(title, "text", $"{Math.Max(homeStrength, awayStrength)} ON {Math.Min(homeStrength, awayStrength)}");
        }
        else
        {
            Set(timeGroup, "x", 177);
            Set(labelGroup, "x", 10);
            Set(title, "width", 250);
            Set(title, "textAlignH", "LEFT");
            Set(title, "text", "POWER PLAY");
        }

        var show = new OverlayScript();
        if (side != null)
        {
            show.Set(side.Fade, "alpha", 0).Set(side.Grad, "alpha", 0);
            show.Set(Find(bar, "tint"), "color", Hex(side.Secondary)).Set(Find(bar, "tintGrad"), "color", Hex(side.Secondary));
            show.Set(Find(group, "tint"), "color", Hex(side.Primary)).Set(Find(group, "glow"), "color", Hex(side.Primary));
            show.Set(labels, "color", Hex(White));
        }
        else
        {
            show.Set(labels, "color", "$DARK_GRAY");
        }
        show.Set(labels, "alpha", 0);
        show.To(bar, "alpha", 1, 10, QuadInOut).Wait();
        show.To(bar, "y", SingleHeight, 5, QuadInOut);
        show.To(group, "height", SingleHeight, 5, QuadInOut);
        if (side != null)
        {
            show.To(side.Logo, "height", LogoMaskFull, 5, QuadInOut);
            show.To(side.Fade, "alpha", 1, 5, QuadInOut);
            show.To(side.Glow, "alpha", 0, 5, QuadInOut);
        }
        show.Delay(3);
        show.Set(labels, "x", groupX - 20);
        show.To(labels, "alpha", 1, 5, QuadInOut).To(labels, "x", groupX, 5, QuadInOut);
        show.Wait();

        var hide = new OverlayScript();
        hide.To(labels, "alpha", 0, 5, QuadInOut).To(labels, "x", groupX - 10, 5, QuadInOut);
        hide.Delay(2);
        hide.To(bar, "y", 0, 5, QuadInOut);
        hide.To(group, "height", 0, 5, QuadInOut);
        if (side != null)
            hide.To(side.Logo, "height", LogoMaskNormal, 5, QuadInOut).To(side.Fade, "alpha", 0, 5, QuadInOut);
        hide.Wait();
        if (side != null)
            hide.To(side.Glow, "alpha", 1, 10, QuadInOut);
        hide.To(bar, "alpha", 0, 15, QuadInOut);

        return new OverlayScript().Add(show).Wait().Delay(PowerPlayHold).Add(hide).Wait();
    }

    private OverlayScript Short(OverlayPose pose, bool away, string first, string? second, bool penalty, bool emptyNet)
    {
        var side = SideOf(away);
        var lower = Find(_labels, "dropDownLabelSubString");
        var lowerLabel = Find(lower, "label");
        var height = second != null ? DoubleHeight : SingleHeight;
        var groupX = Own(side.Group, "x");
        var fill = penalty ? PenaltyFill : side.Primary;
        var barColour = penalty ? PenaltyBar : side.Secondary;
        var ink = penalty ? 0u : White;
        void Set(LayoutWidget? widget, string key, JsonNode? value)
        {
            if (widget != null)
                pose.Set(widget, key, value);
        }

        Set(side.Label, "text", first);
        Set(side.Label, "textAlignH", "CENTER");
        Set(side.Label, "fontColor", Hex(ink));
        Set(Find(side.Bar, "tint"), "color", Hex(barColour));
        Set(Find(side.Bar, "tintGrad"), "color", Hex(barColour));
        Set(Find(side.Group, "tint"), "color", Hex(fill));
        Set(Find(side.Group, "glow"), "color", Hex(penalty ? PenaltyFill : side.Primary));
        if (second != null)
            Set(lowerLabel, "text", second);

        var show = new OverlayScript();
        if (second != null)
            show.Delay(15);
        show.Set(side.LabelGroup, "alpha", 0);
        if (second != null)
            show.Set(lower, "alpha", 0).Set(lower, "x", groupX - 20).Set(lower, "width", Own(side.Group, "width"));
        if (emptyNet)
            show.Delay(15);
        show.To(side.Bar, "alpha", 1, 10, QuadInOut).Wait();
        show.To(side.Bar, "y", height, 5, QuadInOut);
        show.To(side.Group, "height", height, 5, QuadInOut);
        if (penalty)
        {
            show.To(side.Grad, "alpha", 1, 5, QuadInOut);
        }
        else
        {
            show.To(side.Logo, "height", LogoMaskFull, 5, QuadInOut);
            show.To(side.Fade, "alpha", 1, 5, QuadInOut);
            show.To(side.Glow, "alpha", 0, 5, QuadInOut);
        }
        show.Delay(3);
        show.Set(side.LabelGroup, "x", groupX - 20).Set(side.LabelGroup, "width", Own(side.Group, "width"));
        show.To(side.LabelGroup, "alpha", 1, 5, QuadInOut).To(side.LabelGroup, "x", groupX, 5, QuadInOut);
        if (second != null)
        {
            show.Delay(3);
            show.To(lower, "x", groupX, 5, QuadOut).To(lower, "alpha", 1, 5, QuadOut);
        }
        show.Wait();

        var hide = new OverlayScript();
        if (second != null)
        {
            hide.To(lower, "x", groupX - 20, 5, QuadOut).To(lower, "alpha", 0, 5, QuadOut);
            hide.Delay(2);
        }
        hide.To(side.LabelGroup, "alpha", 0, 5, QuadInOut).To(side.LabelGroup, "x", groupX - 10, 5, QuadInOut);
        hide.Delay(2);
        hide.To(side.Bar, "y", 0, 5, QuadInOut);
        hide.To(side.Group, "height", 0, 5, QuadInOut);
        if (penalty)
            hide.To(side.Grad, "alpha", 0, 5, QuadInOut);
        else
            hide.To(side.Logo, "height", LogoMaskNormal, 5, QuadInOut).To(side.Fade, "alpha", 0, 5, QuadInOut);
        hide.Wait();
        hide.Set(side.Label, "fontColor", Hex(White));
        hide.To(side.Glow, "alpha", 1, 10, QuadInOut);
        if (penalty)
            hide.To(side.Bar, "alpha", 0, 5, QuadInOut).To(side.Grad, "alpha", 0, 5, QuadInOut);
        else
            hide.To(side.Bar, "alpha", 0, 15, QuadInOut);

        return new OverlayScript().Add(show).Wait().Delay(MessageHold).Add(hide).Wait();
    }

    private OverlayScript FullWidth(OverlayPose pose, bool away)
    {
        var side = SideOf(away);
        var longGroup = Find(_dropDowns, "dropDown");
        var labelGroup = Find(_labels, "dropDownLabel1");
        var label = Find(labelGroup, "label");
        const double LabelX = 238;
        void Set(LayoutWidget? widget, string key, JsonNode? value)
        {
            if (widget != null)
                pose.Set(widget, key, value);
        }

        Set(label, "text", "FULL WIDTH MESSAGE");
        Set(label, "textAlignH", "LEFT");
        Set(label, "fontColor", Hex(White));
        Set(Find(side.Group, "tint"), "color", Hex(side.Primary));
        Set(Find(side.Group, "glow"), "color", Hex(side.Primary));
        Set(Find(longGroup, "tint"), "color", Hex(side.Primary));
        Set(Find(longGroup, "glow"), "color", Hex(side.Primary));

        var homeWidth = away ? 248 : 247;
        var homeX = away ? 0 : 248;
        var s = new OverlayScript();
        s.Set(longGroup, "x", Own(side.Bar, "x")).Set(longGroup, "width", Own(side.Bar, "width"));
        s.Set(labelGroup, "x", LabelX - 20).Set(labelGroup, "width", 743).Set(labelGroup, "alpha", 0);
        s.To(side.Bar, "alpha", 1, 10, QuadInOut).Wait();
        s.To(side.Bar, "y", SingleHeight, 5, QuadInOut);
        s.To(side.Group, "height", SingleHeight, 5, QuadInOut);
        s.To(side.Logo, "height", LogoMaskFull, 5, QuadInOut);
        s.To(side.Fade, "alpha", 1, 5, QuadInOut);
        s.To(side.Glow, "alpha", 0, 5, QuadInOut);
        s.Wait();
        s.Set(longGroup, "alpha", 1).Set(longGroup, "height", SingleHeight);
        s.To(longGroup, "width", 742, 7, QuadInOut).To(longGroup, "x", 0, 7, QuadInOut);
        s.To(side.Bar, "width", 742, 7, QuadInOut).To(side.Bar, "x", 0, 7, QuadInOut);
        s.Wait();
        s.To(labelGroup, "alpha", 1, 5, QuadInOut).To(labelGroup, "x", LabelX, 5, QuadInOut);
        s.Wait();
        s.Delay(150).Wait();
        s.To(labelGroup, "alpha", 0, 5, QuadInOut).To(labelGroup, "x", LabelX - 20, 5, QuadInOut);
        s.Delay(3);
        s.To(longGroup, "width", homeWidth, 7, QuadInOut).To(longGroup, "x", homeX, 7, QuadInOut);
        s.To(side.Bar, "width", homeWidth, 7, QuadInOut).To(side.Bar, "x", homeX, 7, QuadInOut);
        s.Wait();
        s.Set(longGroup, "alpha", 0);
        s.To(side.Bar, "y", 0, 5, QuadInOut);
        s.To(side.Group, "height", 0, 5, QuadInOut);
        s.To(longGroup, "height", 0, 5, QuadInOut);
        s.To(side.Logo, "height", LogoMaskNormal, 5, QuadInOut);
        s.To(side.Fade, "alpha", 0, 5, QuadInOut);
        s.Wait();
        s.To(side.Glow, "alpha", 1, 10, QuadInOut);
        s.To(side.Bar, "alpha", 0, 15, QuadInOut);
        return s.Wait();
    }

    private OverlayScript ClockMessage(OverlayPose pose)
    {
        var back = Find(_warning, "dropDown");
        var bar = Find(_warning, "highlight");
        var label = Find(_warning, "label");
        if (label != null)
            pose.Set(label, "text", "ICING");

        var s = new OverlayScript();
        s.To(bar, "alpha", 1, 5, QuadInOut).Wait();
        s.To(back, "height", SingleHeight, 5, QuadInOut);
        s.To(bar, "y", 35, 5, QuadInOut);
        s.Add(ShowText([label], 5, 2, 20)).Wait();
        s.Delay(MessageHold);
        s.Add(HideText([label], 5, 0, 20)).Wait();
        s.To(back, "height", 0, 5, QuadInOut);
        s.To(bar, "y", 0, 5, QuadInOut);
        s.Wait();
        s.To(bar, "alpha", 0, 10, QuadInOut);
        return s.Wait();
    }

    private OverlayScript Comparison(OverlayPose pose)
    {
        var group = Find(_dropDowns, "statDropDown");
        var awayBar = Find(_dropDowns, "awayHighlight");
        var homeBar = Find(_dropDowns, "homeHighlight");
        var labels = Find(_labels, "dropDownLabelStats");
        var left = Find(labels, "statLeft");
        var right = Find(labels, "statRight");
        var middle = Find(labels, "statLabel");
        LayoutWidget?[] array = [left, middle, right];
        void Text(LayoutWidget? parent, string text)
        {
            if (Find(parent, "label") is { } label)
                pose.Set(label, "text", text);
        }

        Text(left, "12");
        Text(middle, "SHOTS ON GOAL");
        Text(right, "9");

        var s = new OverlayScript();
        s.Set(group, "height", 0).Set(group, "visible", true);
        foreach (var part in array)
            s.Set(part, "alpha", 0);
        s.Set(labels, "alpha", 1);
        s.To(awayBar, "alpha", 1, 10, QuadInOut).To(homeBar, "alpha", 1, 10, QuadInOut).Wait();
        s.To(group, "height", 45, 5, QuadInOut);
        s.To(awayBar, "y", 37, 5, QuadInOut).To(homeBar, "y", 37, 5, QuadInOut).Wait();
        s.To(awayBar, "width", 86, 5, QuadInOut);
        s.To(homeBar, "width", 86, 5, QuadInOut).To(homeBar, "x", 408, 5, QuadInOut);
        s.DelayMs(3);
        s.Set(left, "x", 3).Set(right, "x", 411).Set(middle, "x", 88);
        s.Add(ShowText(array, 5, 2, 20)).Wait();
        s.Delay(120).Wait();
        s.To(awayBar, "width", 248, 5, QuadInOut);
        s.To(homeBar, "width", 247, 5, QuadInOut).To(homeBar, "x", 248, 5, QuadInOut);
        s.Add(HideText(array, 5, 0, 20)).Wait();
        s.To(group, "height", 0, 5, QuadInOut);
        s.To(awayBar, "y", 0, 5, QuadInOut).To(homeBar, "y", 0, 5, QuadInOut).Wait();
        s.To(awayBar, "alpha", 0, 10, QuadInOut).To(homeBar, "alpha", 0, 10, QuadInOut).Wait();
        s.Set(group, "visible", false);
        return s;
    }

    private OverlayScript Shootout(OverlayPose pose)
    {
        var group = Find(_dropDowns, "shootoutDropDown");
        var names = new[] { ("goalIndicatorAway0", 17.0, true), ("goalIndicatorAway1", 73, false), ("goalIndicatorAway2", 127, true), ("goalIndicatorHome0", 266, false), ("goalIndicatorHome1", 320, true), ("goalIndicatorHome2", 374, false) };
        var totals = new[] { ("goalsAway", 177.0, "2"), ("goalsHome", 425, "1") };
        var show = new OverlayScript(0.8);
        var parts = new List<LayoutWidget?>();
        foreach (var (name, x, scored) in names)
        {
            var indicator = Find(group, name);
            parts.Add(indicator);
            show.Set(indicator, "x", x).Set(indicator, "alpha", 0);
            if (Find(indicator, scored ? "check" : "cross") is { } mark)
                pose.Set(mark, "visible", true);
        }
        foreach (var (name, x, text) in totals)
        {
            var total = Find(group, name);
            parts.Add(total);
            show.Set(total, "x", x).Set(total, "alpha", 0);
            if (Find(total, "label") is { } label)
                pose.Set(label, "text", text);
        }

        show.Set(group, "width", 0).Set(group, "visible", true);
        show.To(group, "width", 494, 8, QuadOut);
        show.DelayMs(2);
        show.Add(ShowText(parts, 5, 1, 10));
        show.Wait();

        var hide = new OverlayScript(0.8);
        hide.Add(HideText(parts, 5, 1, -10)).Wait();
        hide.To(group, "width", 0, 8, QuadInOut).Wait();
        hide.Set(group, "visible", false);
        return new OverlayScript().Add(show).Wait().Delay(MessageHold).Add(hide).Wait();
    }
}
