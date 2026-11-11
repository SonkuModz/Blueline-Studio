using System.Text;
using System.Text.RegularExpressions;

namespace Blueline.Core.Overlays;

public sealed record SampleTeam(int ArtId, string Abbreviation, uint Primary, int Score, uint Secondary = 0xFFFFFF);

/// <summary>
/// Made-up game data for the preview. Layouts leave scores, names and team colours blank for the game's code to set,
/// so these are filled in from the widget's name. The rules are a best guess and never touch the file being edited.
/// </summary>
public sealed partial class OverlaySample(SampleTeam away, SampleTeam home)
{
    public SampleTeam Away { get; } = away;
    public SampleTeam Home { get; } = home;

    /// <summary>The side of the ice a widget belongs to, found from its own name and its parents', or null.</summary>
    public SampleTeam? Side(LayoutWidget widget)
    {
        for (var at = widget; at != null; at = at.Parent)
        {
            if (at.Name.Contains("home", StringComparison.OrdinalIgnoreCase))
                return Home;
            if (at.Name.Contains("away", StringComparison.OrdinalIgnoreCase))
                return Away;
        }
        return null;
    }

    /// <summary>Measured from an in-game score clock: panels sit at about half the brightness of the team table's colour.</summary>
    private const double PanelBrightness = 0.52;

    public static uint PanelColour(uint primary) =>
        ((uint)(((primary >> 16) & 255) * PanelBrightness) << 16) | ((uint)(((primary >> 8) & 255) * PanelBrightness) << 8) | (uint)((primary & 255) * PanelBrightness);

    /// <summary>Team colour for a side's panels and glow. Colours written on them in the file are the author's stand-ins, which the game replaces.</summary>
    public uint? Colour(LayoutWidget widget)
    {
        if (Side(widget) is not { } side)
            return null;
        if (widget.Name.Equals("tint", StringComparison.OrdinalIgnoreCase))
            return PanelColour(side.Primary);
        return widget.Name.Equals("bottomGlow", StringComparison.OrdinalIgnoreCase) ? side.Primary : null;
    }

    /// <summary>
    /// The box the game really draws a small logo in, when that differs from the file. The layout says 128 pixels,
    /// but the score clock's code scales and places each team's logo from its own table.
    /// </summary>
    public (double X, double Y, double Width, double Height)? GameBox(LayoutWidget widget, double parentWidth, double parentHeight)
    {
        if (widget.AssetType != "team_logo_small_shiny" || widget.Parent?.Node["clipToBounds"]?.ToJsonString() != "true" || Side(widget) is not { } side)
            return null;
        var fit = ScoreClockLogos.For(side.ArtId);
        var size = (JsonEdit.Number(widget.Node["width"]) ?? 128) * fit.Scale;
        return (fit.X, fit.Y, size, size);
    }

    /// <summary>The number that goes in place of # in the widget's asset path.</summary>
    public int AssetNumber(LayoutWidget widget)
    {
        if (widget.AssetType.Contains("team_logo", StringComparison.OrdinalIgnoreCase) || widget.AssetType.Contains("teamlogo", StringComparison.OrdinalIgnoreCase))
            return (Side(widget) ?? Home).ArtId;
        return (int)(JsonEdit.Number(widget.Node["id"]) ?? 0);
    }

    /// <summary>What a label shows when the file leaves it empty.</summary>
    public string? Text(LayoutWidget widget)
    {
        var name = widget.Name.ToLowerInvariant();
        var side = Side(widget);
        if (name.Contains("score") || name.Contains("goals"))
            return (side?.Score ?? 2).ToString();
        if (name.Contains("teamname") || name.Contains("abbr") || name.Contains("teamshort"))
            return (side ?? Home).Abbreviation;
        if (name.Contains("period"))
            return "1ST";
        if (name.Contains("time") || name.Contains("clock"))
            return "12:34";
        if (name.Contains("number"))
            return "27";
        if (name == "label")
        {
            // A plain "label" next to named ones is the spare field for messages such as FINAL, which are empty during play.
            if (widget.Parent is { } parent && parent.Children.Any(c => c.Kind == "Label" && c.Name != "label"))
                return "";
            return widget.Parent is { } owner ? Words(owner.Name) : "";
        }
        return Words(widget.Name);
    }

    private static string Words(string name) => CamelBreak().Replace(WidgetPrefix().Replace(name, ""), " ").Replace('_', ' ').ToUpperInvariant();

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelBreak();

    /// <summary>Authors start text widgets' names with "txt" or "lbl", which is not part of what they say.</summary>
    [GeneratedRegex("^(txt|lbl)_?(?=[A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex WidgetPrefix();
}
