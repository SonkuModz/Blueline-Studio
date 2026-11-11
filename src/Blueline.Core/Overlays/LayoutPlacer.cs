using System.Text.Json.Nodes;

namespace Blueline.Core.Overlays;

/// <summary>A rectangle on screen that a widget is cut off at.</summary>
public readonly record struct OverlayClip(double Left, double Top, double Right, double Bottom)
{
    public OverlayClip Within(OverlayClip? outer) => outer is { } o
        ? new OverlayClip(Math.Max(Left, o.Left), Math.Max(Top, o.Top), Math.Min(Right, o.Right), Math.Min(Bottom, o.Bottom))
        : this;
}

/// <summary>Where a widget lands in the preview. <see cref="Target"/> is the widget of the edited file that a click on it selects.</summary>
public sealed record PlacedWidget(LayoutWidget Widget, LayoutWidget Target, double X, double Y, double Width, double Height,
    double ScaleX, double ScaleY, double ParentScaleX, double ParentScaleY, double Alpha, double Rotation,
    OverlayClip? Clip = null, uint Tint = 0xFFFFFF, OverlayBlend Blend = OverlayBlend.Normal);

/// <summary>How to preview a layout. Everything left out previews the file on its own, with no game screen and no sample data.</summary>
public sealed record PlacerOptions
{
    public double ScreenWidth { get; init; }
    public double ScreenHeight { get; init; }

    /// <summary>Also shows groups the game only brings up during play, such as the score clock's drop-downs.</summary>
    public bool ShowHidden { get; init; }

    /// <summary>A hidden widget that stays visible anyway, so whatever is selected can be seen.</summary>
    public LayoutWidget? Reveal { get; init; }

    /// <summary>Turns a colour value from a file, such as "$BLACK" or "0xFF0000", into 0xRRGGBB.</summary>
    public Func<string?, uint?>? Colour { get; init; }

    /// <summary>A colour the game would set on this widget at run time.</summary>
    public Func<LayoutWidget, uint?>? GameColour { get; init; }

    /// <summary>Where the game really draws a widget, given its parent's size, when that differs from the file.</summary>
    public Func<LayoutWidget, double, double, (double X, double Y, double Width, double Height)?>? GameBox { get; init; }

    /// <summary>The text a label shows when its file leaves it for the game to fill in.</summary>
    public Func<LayoutWidget, string?>? Text { get; init; }

    /// <summary>A widget's property with its named styles applied. Left out, only what the widget itself says counts.</summary>
    public Func<LayoutWidget, string, JsonNode?>? Styled { get; init; }

    /// <summary>An animation is setting the widgets' state, so a widget switched off stays off even with hidden parts shown.</summary>
    public bool Posed { get; init; }
}

/// <summary>
/// Works out a preview of a layout. It follows positions, sizes, margins, alignment, scale, stacks, clipping, masks,
/// tints and blend modes. What the game's own code does at run time is not known here, so hidden parts and text are
/// informed guesses (see RESEARCH section 6.9).
/// </summary>
public sealed class LayoutPlacer(Func<string, (double Width, double Height)?> assetSize, Func<string, LayoutDocument?> expand, PlacerOptions? options = null)
{
    private const int DeepestNesting = 12;
    private const double UnknownAssetSize = 32;
    private readonly PlacerOptions _options = options ?? new PlacerOptions();
    private readonly List<PlacedWidget> _placed = [];
    private readonly HashSet<string> _masks = [];
    private readonly Dictionary<string, OverlayClip> _maskAreas = [];
    private bool _measuringMasks;

    public IReadOnlyList<PlacedWidget> Place(LayoutDocument document)
    {
        _placed.Clear();
        _masks.Clear();
        _maskAreas.Clear();
        CollectMasks(document.Root, 0);

        // A mask can come after the widgets it cuts, so its area has to be known before anything is placed.
        if (_masks.Count > 0)
        {
            _measuringMasks = true;
            Start(document);
            _measuringMasks = false;
            _placed.Clear();
        }
        Start(document);
        return _placed;
    }

    private void Start(LayoutDocument document) =>
        Visit(document.Root, document.Root, new Frame(0, 0, 1, 1, _options.ScreenWidth, _options.ScreenHeight, 1, null, 0xFFFFFF, OverlayBlend.Normal), 0, null);

    private readonly record struct Frame(double X, double Y, double ScaleX, double ScaleY, double Width, double Height, double Alpha, OverlayClip? Clip, uint Tint, OverlayBlend Blend);

    private void CollectMasks(LayoutWidget widget, int depth)
    {
        if (JsonEdit.String(widget.Node["mask"]) is { Length: > 0 } mask)
            _masks.Add(mask);
        foreach (var child in Parts(widget, depth))
            CollectMasks(child, depth + 1);
    }

    private IEnumerable<LayoutWidget> Parts(LayoutWidget widget, int depth)
    {
        if (depth >= DeepestNesting)
            return [];
        if (widget.Children.Count > 0)
            return widget.Children;
        return expand(widget.Kind)?.Root.Children ?? [];
    }

    private JsonNode? Value(LayoutWidget widget, string key) => _options.Styled != null ? _options.Styled(widget, key) : widget.Node[key];

    private double Num(LayoutWidget widget, string key, double fallback = 0) => JsonEdit.Number(Value(widget, key)) ?? fallback;

    private (double Width, double Height) Measure(LayoutWidget widget, int depth)
    {
        var width = JsonEdit.Number(Value(widget, "width"));
        var height = JsonEdit.Number(Value(widget, "height"));
        if (width is { } w && height is { } h)
            return (w, h);
        var natural = Natural(widget, depth);
        return (width ?? natural.Width, height ?? natural.Height);
    }

    private (double Width, double Height) Natural(LayoutWidget widget, int depth)
    {
        if (depth >= DeepestNesting)
            return (0, 0);
        switch (widget.Kind)
        {
            case "Asset":
                return assetSize(widget.AssetType) ?? (UnknownAssetSize, UnknownAssetSize);
            case "Label":
                var size = Num(widget, "fontSize", 20);
                var text = JsonEdit.String(Value(widget, "text")) is { Length: > 0 } t ? t : _options.Text?.Invoke(widget) ?? widget.Name;
                return (text.Length * size * 0.55, size + LabelPlacement.Gutter * 2);
        }

        var parts = Parts(widget, depth).ToList();
        if (parts.Count == 0)
            return (0, 0);
        var stack = StackOf(widget);
        double right = 0, bottom = 0;
        foreach (var part in parts)
        {
            var (w, h) = Measure(part, depth + 1);
            if (stack.Horizontal == true)
            {
                right += w + stack.Gap;
                bottom = Math.Max(bottom, h);
            }
            else if (stack.Horizontal == false)
            {
                bottom += h + stack.Gap;
                right = Math.Max(right, w);
            }
            else
            {
                right = Math.Max(right, Num(part, "x") + w);
                bottom = Math.Max(bottom, Num(part, "y") + h);
            }
        }
        return (right, bottom);
    }

    /// <summary>Horizontal is null when children are placed freely.</summary>
    private static (bool? Horizontal, double Gap) StackOf(LayoutWidget widget)
    {
        if (widget.Node["layout"] is not JsonObject layout || JsonEdit.String(layout["type"]) != "StackLayout")
            return (null, 0);
        var data = layout["data"] as JsonObject;
        var direction = data?.FirstOrDefault(p => p.Key.Contains("direction", StringComparison.OrdinalIgnoreCase) || p.Key.Contains("orientation", StringComparison.OrdinalIgnoreCase)).Value;
        var gap = data?.FirstOrDefault(p => p.Key.Contains("gap", StringComparison.OrdinalIgnoreCase) || p.Key.Contains("spacing", StringComparison.OrdinalIgnoreCase)).Value;
        var horizontal = direction != null && direction.ToString().Contains("HORIZ", StringComparison.OrdinalIgnoreCase);
        return (horizontal, JsonEdit.Number(gap) ?? 0);
    }

    /// <summary>True for groups the layout's author switched off: the game turns those on during play.</summary>
    private bool IsHidden(LayoutWidget widget)
    {
        if (_options.Posed && !_measuringMasks && Value(widget, "visible")?.ToJsonString() == "false")
            return true;
        if (_measuringMasks || _options.ShowHidden || IsOnPathTo(widget, _options.Reveal))
            return false;
        if (Value(widget, "visible")?.ToJsonString() == "false")
            return true;
        return _options.ScreenWidth > 0 && widget.Kind == "Group" && widget.Node["authoringData"]?["visible"]?.ToJsonString() == "false";
    }

    private static bool IsOnPathTo(LayoutWidget widget, LayoutWidget? target)
    {
        for (var at = target; at != null; at = at.Parent)
            if (ReferenceEquals(at, widget))
                return true;
        return false;
    }

    private uint TintOf(LayoutWidget widget, uint inherited)
    {
        var own = _options.GameColour?.Invoke(widget) ?? _options.Colour?.Invoke(JsonEdit.String(Value(widget, "color")) ?? JsonEdit.String(Value(widget, "fillColor")));
        return own is { } colour ? OverlayCompositor.Multiply(inherited, colour) : inherited;
    }

    private static OverlayBlend BlendOf(LayoutWidget widget, OverlayBlend inherited) =>
        (JsonEdit.String(widget.Node["blendMode"]) ?? JsonEdit.String(widget.Node["blendmode"])) switch
        {
            "ADD" => OverlayBlend.Add,
            "MULTIPLY" => OverlayBlend.Multiply,
            "SCREEN" => OverlayBlend.Screen,
            _ => inherited,
        };

    private void Visit(LayoutWidget widget, LayoutWidget target, Frame parent, int depth, (bool? Horizontal, double Offset)? stacked)
    {
        if (depth >= DeepestNesting || IsHidden(widget))
            return;

        var (width, height) = Measure(widget, depth);
        var localX = Num(widget, "x");
        var localY = Num(widget, "y");
        if (stacked is { Horizontal: true } across)
            localX += across.Offset;
        if (stacked is { Horizontal: false } down)
            localY += down.Offset;

        (localX, width) = Align(JsonEdit.String(widget.Node["alignH"]), localX, width, parent.Width, Num(widget, "marginLeft"), Num(widget, "marginRight"));
        (localY, height) = Align(JsonEdit.String(widget.Node["alignV"]), localY, height, parent.Height, Num(widget, "marginTop"), Num(widget, "marginBottom"));

        if (_options.GameBox?.Invoke(widget, parent.Width, parent.Height) is { } box)
            (localX, localY, width, height) = box;

        var screenX = parent.X + localX * parent.ScaleX;
        var screenY = parent.Y + localY * parent.ScaleY;
        var scaleX = parent.ScaleX * Num(widget, "scaleX", 1);
        var scaleY = parent.ScaleY * Num(widget, "scaleY", 1);
        var alpha = parent.Alpha * Num(widget, "alpha", 1);
        var tint = TintOf(widget, parent.Tint);
        var blend = BlendOf(widget, parent.Blend);

        var area = new OverlayClip(Math.Min(screenX, screenX + width * scaleX), Math.Min(screenY, screenY + height * scaleY),
            Math.Max(screenX, screenX + width * scaleX), Math.Max(screenY, screenY + height * scaleY));
        if (_measuringMasks && _masks.Contains(widget.Name))
            _maskAreas.TryAdd(widget.Name, area);

        var clip = parent.Clip;
        if (JsonEdit.String(widget.Node["mask"]) is { Length: > 0 } mask && _maskAreas.TryGetValue(mask, out var maskArea))
            clip = maskArea.Within(clip);

        if (!_masks.Contains(widget.Name))
            _placed.Add(new PlacedWidget(widget, target, screenX, screenY, width, height, scaleX, scaleY, parent.ScaleX, parent.ScaleY, alpha, Num(widget, "rotation"), clip, tint, blend));

        if (widget.Node["clipToBounds"]?.ToJsonString() == "true")
            clip = area.Within(clip);

        var inner = new Frame(screenX, screenY, scaleX, scaleY, width, height, alpha, clip, tint, blend);
        var ownChildren = widget.Children.Count > 0;
        var stack = StackOf(widget);
        double cursor = 0;
        foreach (var part in Parts(widget, depth))
        {
            Visit(part, ownChildren ? part : target, inner, depth + 1, stack.Horizontal == null ? null : (stack.Horizontal, cursor));
            var (w, h) = Measure(part, depth + 1);
            cursor += (stack.Horizontal == true ? w : h) + stack.Gap;
        }
    }

    private static (double Position, double Size) Align(string? align, double position, double size, double parent, double near, double far) => align switch
    {
        "LEFT" or "TOP" => (near, size),
        "RIGHT" or "BOTTOM" => (parent - size - far, size),
        "CENTER" => ((parent - size) / 2 + position, size),
        "FILL" => (near, Math.Max(0, parent - near - far)),
        _ => (position, size),
    };
}
