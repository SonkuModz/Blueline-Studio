using System.Text.Json.Nodes;

namespace Blueline.Core.Overlays;

/// <summary>
/// Restyles the stock score clock layout to the proportions and colours of the Sportsnet bug (docs/SPORTSNET_BUG.md).
/// Only what a layout can change is changed. The game's `ScoreClockOverlay` code keeps its node names, start positions and
/// drop-down heights (RESEARCH 6.9), so those stay, and the parts that need live data it does not provide are left out.
/// Everything is designed at 1.953 times the Sportsnet size, because the code's own 248 pixel wide panels are fixed, and the whole
/// clock is then scaled down by <see cref="Scale"/> to the real size.
/// </summary>
public static class SportsnetBug
{
    /// <summary>Stock panel width (248) over the Sportsnet panel width (127 native pixels at 1920 by 1080).</summary>
    public const double Zoom = 248.0 / 127.0;

    public const double Scale = 1 / Zoom;

    /// <summary>Sportsnet's bug is about 440 by 84 at 1920 by 1080, so these are that times <see cref="Zoom"/>.</summary>
    public const double Tile = 164;
    public const double TeamRow = 111;
    public const double ClockWidth = 192;
    public const double Height = 164;
    public const double PanelLeft = 6;
    public const double GoalWidth = 857;

    /// <summary>Where the bug sits on the 1920 by 1080 screen: about 106 across and 68 down in the broadcast still.</summary>
    public const double ScreenX = 106;
    public const double ScreenY = 68;

    private const string Ink = "0x1E2F3F";
    private const string Light = "0xDCDADD";
    private const string Strip = "0xCFD0D2";
    private const string Rectangle = "Rectangle";
    private const string Label = "Label";
    private const string Asset = "Asset";
    private const string Marker = "snTile";

    /// <summary>True when the layout already carries this look.</summary>
    public static bool IsApplied(LayoutDocument document) => document.Find(Marker) != null;

    /// <summary>The names the game's code needs, so a layout missing any cannot take the look.</summary>
    private static readonly string[] Required =
    [
        "scoreClock", "contents", "contentsMask", "capLeft", "capRight", "away", "home", "clock", "time", "period", "label", "teamName", "score",
        "dropDowns", "dropDownLabels", "dropDownClockWarning", "goal", "bg", "bgGradLeft", "bgGradRight",
    ];

    public static string? Problem(LayoutDocument document)
    {
        var missing = Required.Where(n => document.Find(n) == null).ToList();
        return missing.Count == 0 ? null : "This layout does not look like the score clock (missing " + string.Join(", ", missing) + ").";
    }

    /// <summary>Changes the document in place. The caller keeps the original, which the project can revert to.</summary>
    public static void Apply(LayoutDocument document)
    {
        if (Problem(document) is { } problem)
            throw new InvalidOperationException(problem);
        if (IsApplied(document))
            throw new InvalidOperationException("This layout already has the Sportsnet look.");

        LayoutWidget W(string name, LayoutWidget? under = null) =>
            (under != null ? Walk(under).FirstOrDefault(w => w.Name == name) : document.Find(name)) ?? throw new InvalidOperationException("Missing " + name);

        var clock = W("scoreClock");
        Set(clock, "x", ScreenX);
        Set(clock, "y", ScreenY);
        Set(clock, "scaleX", Scale);
        Set(clock, "scaleY", Scale);

        // The chrome of the stock clock is not part of this look. Hidden parts stay hidden because the game's code only moves them.
        foreach (var name in new[] { "capLeft", "capRight", "bgGradLeft", "bgGradRight" })
            Hide(W(name));

        var contents = W("contents");
        Set(contents, "x", Tile - PanelLeft);
        var mask = W("contentsMask");
        Set(mask, "x", Tile - PanelLeft);
        Set(mask, "width", GoalWidth + 6);
        Set(mask, "height", 330);

        // Tile with the SN logo, left of the panels, and not masked so it stays when the panels slide.
        var tile = Make(Rectangle, Marker, ("x", 0), ("y", 0), ("width", Tile), ("height", Height), ("fillColor", Light));
        var shine = Make(Rectangle, "snTileShine", ("x", 0), ("y", 0), ("width", Tile), ("height", TeamRow / 2 + 2), ("fillColor", "0xFFFFFF"), ("fillAlpha", 0.3));
        var logo = Make(Asset, "snLogo", ("x", 7), ("y", 44.5), ("width", 150), ("height", 75), ("type", LogoAsset));
        InsertFirst(clock, tile, shine, logo);

        // The stock silver base is dark at the bottom. This bug's shots row is flat light grey with thin dividers and the word SHOTS.
        // The numbers beside it are live in the broadcast, but nothing in a layout can fill them, so they are left out.
        Hide(W("bg"));
        var strip = Make(Rectangle, "snShotsRow", ("x", PanelLeft), ("y", 4 + TeamRow + 2), ("width", 2 * 247 + 2), ("height", Height - TeamRow - 2), ("fillColor", Strip));
        var d1 = Make(Rectangle, "snShotsDivider1", ("x", PanelLeft + 135), ("y", 4 + TeamRow + 4), ("width", 2.5), ("height", Height - TeamRow - 6), ("fillColor", "0x9A9CA0"), ("fillAlpha", 0.45));
        var d2 = Make(Rectangle, "snShotsDivider2", ("x", PanelLeft + 356), ("y", 4 + TeamRow + 4), ("width", 2.5), ("height", Height - TeamRow - 6), ("fillColor", "0x9A9CA0"), ("fillAlpha", 0.45));
        var shots = Make(Label, "snShotsLabel", ("x", PanelLeft + 247 - 100), ("y", 124), ("width", 200), ("height", 40), ("text", "SHOTS"), ("fontFace", "NBCSN_Frank Bold"),
            ("fontSize", 46), ("fontColor", Ink), ("textAlignH", "CENTER"));
        Append(contents, strip);
        Append(contents, d1);
        Append(contents, d2);
        Append(contents, shots);

        foreach (var side in new[] { "away", "home" })
            RestylePanel(W(side), side == "away");

        RestyleClock(W("clock"));

        // Drop-downs hang from the bottom of the taller bug.
        foreach (var name in new[] { "dropDowns", "dropDownLabels", "dropDownClockWarning" })
            Set(W(name), "y", 4 + Height - 1);

        RestyleGoal(W("goal"));
        document.Refresh();
    }

    /// <summary>The texture that carries the SN logo. Stock art of the clock's score dividers, which this look does not use.</summary>
    public const string LogoAsset = "scoreclock_score_divider";

    private static void RestylePanel(LayoutWidget panel, bool away)
    {
        LayoutWidget Part(string name) => Walk(panel).First(w => w.Name == name && w != panel);
        Set(panel, "height", TeamRow);
        var tint = Part("tint");
        Set(tint, "height", TeamRow);
        foreach (var name in new[] { "blackAdditive", "goalAnimation" })
            Set(Part(name), "height", TeamRow);

        // The stock clock puts each team's logo in its panel. This bug shows no logo at rest.
        foreach (var name in new[] { "logoMask", "scoreDivider", "gradient", "highlightTop2" })
            Hide(Part(name));

        // The game colours the bottom glow with the team colour and adds it over the panel. Spread over the whole panel it brings
        // the panel up to the broadcast's brighter colour (the game's panels are darker than the team table, RESEARCH 6.9).
        var glow = Part("bottomGlow");
        Set(glow, "y", 0);
        var glowArt = Walk(glow).First(w => w.Name == "glow1");
        Set(glowArt, "width", 248);
        Set(glowArt, "height", TeamRow);

        // A lighter top half and a darker bottom edge, as the stock two step panels.
        var highlight = Part("highlight");
        Set(highlight, "height", TeamRow / 2);
        Set(highlight, "width", 248);
        Set(highlight, "fillAlpha", 0.04);
        var shade = Make(Rectangle, "snBottomShade", ("x", -1), ("y", TeamRow - 8), ("width", 248), ("height", 8), ("fillColor", "0x000000"), ("fillAlpha", 0.2));
        Append(panel, shade);

        // Measured on the broadcast: name starts 27 in, the score digit is centred 195 in, and both are centred up and down in the row.
        var name2 = Part("teamName");
        Set(name2, "x", 24);
        Set(name2, "y", 33);
        Set(name2, "width", 130);
        Set(name2, "height", 70);
        Set(name2, "fontFace", "NBCSN_Frank Medium");
        Set(name2, "fontSize", 56);
        Set(name2, "textAlignH", "LEFT");

        var score = Part("score");
        Set(score, "x", 150);
        Set(score, "y", 18);
        Set(score, "width", 78);
        Set(score, "height", 92);
        Set(score, "fontSize", 93);
    }

    private static void RestyleClock(LayoutWidget clock)
    {
        LayoutWidget Part(string name) => Walk(clock).First(w => w.Name == name && w != clock);
        Set(clock, "y", 4);
        Hide(Part("bg"));
        Append(clock, Make(Rectangle, "snClockBase", ("x", 0), ("y", 0), ("width", ClockWidth), ("height", Height), ("fillColor", Light)));
        Append(clock, Make(Rectangle, "snClockShine", ("x", 0), ("y", 0), ("width", ClockWidth), ("height", TeamRow / 2 + 2), ("fillColor", "0xFFFFFF"), ("fillAlpha", 0.3)));
        MoveToFront(clock, "snClockBase", "snClockShine");

        void Text(string name, double y, double size, string face)
        {
            var label = Part(name);
            Set(label, "x", 0);
            Set(label, "y", y);
            Set(label, "width", ClockWidth);
            Set(label, "height", 70);
            Set(label, "fontFace", face);
            Set(label, "fontSize", size);
            Set(label, "fontColor", Ink);
            Set(label, "textAlignH", "CENTER");
        }

        Text("time", 33, 66, "NBCSN_Frank Medium");
        Set(Part("time"), "tracking", -3.5);
        Text("period", 112, 44, "NBCSN_Frank Medium");
        Set(Part("period"), "tracking", -1);

        // FINAL and similar messages are centred in the whole cell.
        Text("label", (Height - 50) / 2, 40, "NBCSN_Frank Bold");
        Set(Part("label"), "height", 50);

        var divider = Make(Rectangle, "snClockDivider", ("x", 14), ("y", 98), ("width", ClockWidth - 28), ("height", 2), ("fillColor", Ink), ("fillAlpha", 0.18));
        Append(clock, divider);
    }

    private static void RestyleGoal(LayoutWidget goal)
    {
        LayoutWidget Part(string name) => Walk(goal).First(w => w.Name == name && w != goal);
        Set(goal, "x", PanelLeft);
        Set(goal, "y", 4);
        Set(goal, "width", GoalWidth);
        Set(goal, "height", Height);
        Set(Part("tint"), "width", GoalWidth);
        Set(Part("tint"), "height", Height);
        Set(Part("highlight"), "width", GoalWidth);
        Set(Part("highlight"), "height", Height / 2);
        foreach (var name in new[] { "angles0", "angles1", "angles2" })
        {
            var group = Part(name);
            foreach (var angle in group.Children)
                Set(angle, "height", Height);
        }
        Set(Part("logoMask"), "width", GoalWidth);
        Set(Part("logoMask"), "height", Height);

        // This bug's goal bar has no logo in it.
        Hide(Part("logoHolder"));

        var text = Part("text");
        Set(text, "fontFace", "NBCSN_Frank Bold");
        Set(text, "fontSize", 150);
        Set(text, "y", 32);
        Set(text, "width", 520);
    }

    // JSON helpers

    private static IEnumerable<LayoutWidget> Walk(LayoutWidget widget)
    {
        yield return widget;
        foreach (var child in widget.Children.SelectMany(Walk))
            yield return child;
    }

    private static void Set(LayoutWidget widget, string key, object value) => widget.Node[key] = value switch
    {
        double number => JsonEdit.Number(number),
        int number => JsonEdit.Number(number),
        string text => JsonValue.Create(text),
        _ => throw new ArgumentException("Unsupported value", nameof(value)),
    };

    private static void Hide(LayoutWidget widget) => widget.Node["visible"] = JsonValue.Create(false);

    private static JsonObject Make(string kind, string name, params (string Key, object Value)[] properties)
    {
        var node = new JsonObject { ["commonName"] = kind, ["name"] = name };
        foreach (var (key, value) in properties)
            node[key] = value switch
            {
                double number => JsonEdit.Number(number),
                int number => JsonEdit.Number(number),
                string text => JsonValue.Create(text),
                _ => throw new ArgumentException("Unsupported value", nameof(properties)),
            };
        return node;
    }

    private static void InsertFirst(LayoutWidget parent, params JsonObject[] nodes)
    {
        var children = parent.Node["children"] as JsonArray ?? throw new InvalidOperationException("No children");
        for (var i = nodes.Length - 1; i >= 0; i--)
            children.Insert(0, nodes[i]);
    }

    /// <summary>Puts the named children first, so the text and the rest draw over them.</summary>
    private static void MoveToFront(LayoutWidget parent, params string[] names)
    {
        var children = (JsonArray)parent.Node["children"]!;
        var moved = names.Select(n => children.OfType<JsonObject>().First(c => JsonEdit.String(c["name"]) == n)).ToList();
        foreach (var node in moved)
            children.Remove(node);
        for (var i = moved.Count - 1; i >= 0; i--)
            children.Insert(0, moved[i]);
    }

    private static void Append(LayoutWidget parent, JsonObject node)
    {
        if (parent.Node["children"] is not JsonArray children)
            parent.Node["children"] = children = [];
        children.Add(node);
    }
}
