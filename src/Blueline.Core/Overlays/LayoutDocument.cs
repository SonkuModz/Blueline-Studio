using System.Text.Json.Nodes;

namespace Blueline.Core.Overlays;

/// <summary>One element of a layout. Its properties are the JSON object itself, so anything the game stores can be edited.</summary>
public sealed class LayoutWidget(JsonObject node, LayoutWidget? parent)
{
    public JsonObject Node { get; } = node;
    public LayoutWidget? Parent { get; } = parent;
    public List<LayoutWidget> Children { get; } = [];
    public string Kind => JsonEdit.String(Node["commonName"]) ?? "";
    public string Name => JsonEdit.String(Node["name"]) ?? "";
    public string AssetType => JsonEdit.String(Node["type"]) ?? "";
    public string Label => Name.Length > 0 ? $"{Name}  ({ShortKind})" : ShortKind;
    private string ShortKind => Kind.Contains("::") ? Kind[(Kind.LastIndexOf(':') + 1)..] : Kind;
}

/// <summary>An editable `.layout` file.</summary>
public sealed class LayoutDocument
{
    private const string ChildrenKey = "children";

    private LayoutDocument(JsonNode root)
    {
        RootNode = root;
        Root = Build(root.AsObject(), null);
    }

    public JsonNode RootNode { get; }
    public LayoutWidget Root { get; private set; }

    public static LayoutDocument Parse(byte[] content) => new(JsonEdit.Parse(content));

    public byte[] ToBytes() => JsonEdit.ToBytes(RootNode);

    public IEnumerable<LayoutWidget> All() => Walk(Root);

    /// <summary>Rebuilds the widget list after the JSON changed shape.</summary>
    public void Refresh() => Root = Build(RootNode.AsObject(), null);

    public LayoutWidget? Find(string name) => All().FirstOrDefault(w => w.Name == name);

    private static IEnumerable<LayoutWidget> Walk(LayoutWidget widget)
    {
        yield return widget;
        foreach (var child in widget.Children.SelectMany(Walk))
            yield return child;
    }

    private static LayoutWidget Build(JsonObject node, LayoutWidget? parent)
    {
        var widget = new LayoutWidget(node, parent);
        if (node[ChildrenKey] is JsonArray children)
            foreach (var child in children.OfType<JsonObject>())
                widget.Children.Add(Build(child, widget));
        return widget;
    }

    /// <summary>Moves a widget by a distance, changing whichever of position or margin controls it on each axis.</summary>
    public static void Move(LayoutWidget widget, double dx, double dy)
    {
        Shift(widget.Node, JsonEdit.String(widget.Node["alignH"]), dx, "x", "marginLeft", "marginRight", "LEFT", "RIGHT");
        Shift(widget.Node, JsonEdit.String(widget.Node["alignV"]), dy, "y", "marginTop", "marginBottom", "TOP", "BOTTOM");
    }

    private static void Shift(JsonObject node, string? align, double by, string position, string nearMargin, string farMargin, string near, string far)
    {
        if (by == 0 || align == "FILL")
            return;
        if (align == near)
            Add(node, nearMargin, by);
        else if (align == far)
            Add(node, farMargin, -by);
        else
            Add(node, position, by);
    }

    private static void Add(JsonObject node, string key, double by) => node[key] = JsonEdit.Number((JsonEdit.Number(node[key]) ?? 0) + by);

    /// <summary>Adds a copy straight after the original. Returns its name.</summary>
    public string Duplicate(LayoutWidget widget)
    {
        if (widget.Parent?.Node[ChildrenKey] is not JsonArray siblings)
            throw new InvalidOperationException("The root of a layout cannot be copied.");
        var copy = widget.Node.DeepClone().AsObject();
        var name = (widget.Name.Length > 0 ? widget.Name : widget.Kind) + "_copy";
        while (Find(name) != null)
            name += "_";
        copy["name"] = name;
        siblings.Insert(siblings.IndexOf(widget.Node) + 1, copy);
        Refresh();
        return name;
    }

    public void Remove(LayoutWidget widget)
    {
        if (widget.Parent?.Node[ChildrenKey] is not JsonArray siblings)
            throw new InvalidOperationException("The root of a layout cannot be removed.");
        siblings.Remove(widget.Node);
        Refresh();
    }
}
