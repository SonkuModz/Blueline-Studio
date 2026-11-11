using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Blueline.Formats.Scaleform;

namespace Blueline.Core.Overlays;

public enum OverlayFileKind { Definition, Navigation, Bundle, Layout, Script, Data, Requires, Movie }

public sealed record OverlayFile(string Path, OverlayFileKind Kind)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public override string ToString() => Name;
}

/// <summary>A picture an overlay draws. Usage is the asset name a layout asks for, or the movie that loads it.</summary>
public sealed record OverlayArt(string Path, string Usage)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>One on-screen graphic and everything it is made from.</summary>
public sealed class GameOverlay(string name, string group)
{
    public string Name { get; } = name;
    public string Group { get; set; } = group;
    public string? NavigationText { get; init; }
    public List<OverlayFile> Files { get; } = [];
    public List<OverlayArt> Art { get; } = [];
    public List<string> Videos { get; } = [];
    public List<string> Definitions { get; } = [];
    public string Summary => $"{Count(Art.Count, "texture")}, {Count(Files.Count(f => f.Kind == OverlayFileKind.Layout), "layout")}";
    public IEnumerable<OverlayFile> Layouts => Files.Where(f => f.Kind == OverlayFileKind.Layout);
    public IEnumerable<OverlayFile> Movies => Files.Where(f => f.Kind == OverlayFileKind.Movie);

    public static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";

    public void AddFile(string path, OverlayFileKind kind)
    {
        if (Files.All(f => !f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            Files.Add(new OverlayFile(path, kind));
    }

    public void AddArt(string path, string usage)
    {
        if (Art.All(a => !a.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            Art.Add(new OverlayArt(path, usage));
    }
}

/// <summary>
/// Joins the game's overlay files: `definition.overlays` to `.nav`, `.nav` to `.vvm`, `.vvm` to layouts and
/// scripts, layouts to textures through `definition.kettle.assetpaths`, and movies to their textures.
/// </summary>
public sealed class OverlayCatalog
{
    public const string DefinitionPath = "nhl/ui/definition.overlays";
    private const string UiRoot = "nhl/ui/";
    private const string NavFolder = "nhl/ui/features/overlays/";
    private const string AssetPathsFile = "nhl/ui/definition.kettle.assetpaths";
    private const string ManifestFile = "nhl/ui/generated_widget.manifest";
    private const string ConstantsFile = "nhl/ui/default.constants";
    private const string StylesFile = "nhl/ui/definition.styles";
    private const string FontsFile = "nhl/ui/fonts/definition.fonts";
    private const string SharedGroup = "Shared";
    private const string OtherGroup = "Other art";
    private static readonly string[] SharedScripts = ["nhl/ui/lua/overlaydatamanager/overlaydatamanager.lua", "nhl/ui/lua/overlaydatamanager/overlaydatamanager.lreq", "nhl/ui/lua/overlayutil/overlayutil.lua"];
    private static readonly Regex LoadBundle = new(@"""loadVVM""\s*,\s*\[\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex Colour = new(@"""([A-Za-z0-9_]+)""\s*:\s*""0x([0-9A-Fa-f]{6,8})""", RegexOptions.Compiled);

    private readonly Func<string, byte[]?> _read;
    private readonly Dictionary<string, string> _paths;
    private readonly Dictionary<string, string> _assets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _widgetLayouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _numberedAssets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fonts = new(StringComparer.OrdinalIgnoreCase);
    private const string StylesKey = "styles";

    /// <summary>From the Label entry of the game's widget manifest.</summary>
    private const double DefaultFontSize = 20;
    private JsonObject? _styles;
    private static readonly Regex GameImageScheme = new(@"^img://[^/]+/", RegexOptions.Compiled);
    private static readonly Regex AssetVariant = new(@"\[(loc [A-Z_]+|sku)\]", RegexOptions.Compiled);

    private OverlayCatalog(Func<string, byte[]?> read, IEnumerable<string> paths)
    {
        _read = read;
        _paths = paths.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public List<GameOverlay> Overlays { get; } = [];
    public Dictionary<string, uint> Colours { get; } = new(StringComparer.Ordinal);

    /// <summary>The game file a layout's asset name stands for, or null.</summary>
    public string? AssetPath(string assetName) => _assets.GetValueOrDefault(assetName);

    /// <summary>Like <see cref="AssetPath"/>, for names whose path holds a team or item number that the game fills in.</summary>
    public string? AssetPath(string assetName, int number)
    {
        if (_assets.TryGetValue(assetName, out var fixedPath))
            return fixedPath;
        if (!_numberedAssets.TryGetValue(assetName, out var pattern))
            return null;
        var path = UiRoot + AssetVariant.Replace(GameImageScheme.Replace(pattern, ""), "").Replace("#", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Exists(path) ? Canonical(path) : null;
    }

    /// <summary>A texture a movie names, which lives in the movie's folder, spelled the way the archive spells it, or null.</summary>
    public string? MoviePicturePath(string moviePath, string fileName)
    {
        var path = moviePath[..(moviePath.LastIndexOf('/') + 1)] + fileName;
        return Exists(path) ? Canonical(path) : null;
    }

    /// <summary>The game file of a font named in a layout, or null.</summary>
    public string? FontPath(string? fontFace) => _fonts.GetValueOrDefault(fontFace is { Length: > 0 } ? fontFace : "$Default");

    /// <summary>
    /// A widget's value for a property: its own if it has one, otherwise the last of its named styles that sets it.
    /// Names are looked up in the layout's own style table first and then in the game's shared one.
    /// </summary>
    public JsonNode? Styled(LayoutWidget widget, string key)
    {
        if (widget.Node[key] is { } own)
            return own;
        if (widget.Node[StylesKey] is not JsonArray names)
            return null;
        var root = widget;
        while (root.Parent != null)
            root = root.Parent;
        var local = root.Node["style-definitions"] as JsonObject;
        for (var i = names.Count - 1; i >= 0; i--)
            if (names[i]?.ToString() is { } name && (local?[name] ?? _styles?[name]) is JsonObject style && style[key] is { } value)
                return value;
        return null;
    }

    /// <summary>Everything about a label's text, with its styles applied and the game's defaults filled in.</summary>
    /// <param name="label">The label.</param>
    /// <param name="value">Where to read each property from, when something such as an animation changes them.</param>
    public LabelLook Look(LayoutWidget label, Func<LayoutWidget, string, JsonNode?>? value = null)
    {
        JsonNode? Read(string key) => value != null ? value(label, key) : Styled(label, key);
        return new LabelLook(
            JsonEdit.String(Read("fontFace")) ?? "",
            Math.Max(1, JsonEdit.Number(Read("fontSize")) ?? DefaultFontSize),
            JsonEdit.String(Read("fontColor")),
            JsonEdit.String(Read("textAlignH")),
            JsonEdit.String(Read("textAlignV")),
            JsonEdit.String(Read("resizeType")),
            Read("forceCaps")?.ToJsonString() == "true",
            JsonEdit.Number(Read("leading")) ?? 0,
            JsonEdit.Number(Read("tracking")) ?? 0);
    }

    /// <summary>The layout file behind a custom widget name, or null.</summary>
    public string? WidgetLayout(string widgetName) => _widgetLayouts.GetValueOrDefault(widgetName);

    /// <param name="read">Should return edited content where there is any, so the catalog follows the project.</param>
    public static OverlayCatalog Build(Func<string, byte[]?> read, IEnumerable<string> gamePaths)
    {
        var catalog = new OverlayCatalog(read, gamePaths);
        catalog.ReadTables();
        catalog.AddNavigations();
        catalog.AddDefinitions();
        catalog.AddRemainingArt();
        catalog.AddShared();
        catalog.Overlays.Sort((a, b) => GroupRank(a.Group) != GroupRank(b.Group) ? GroupRank(a.Group) - GroupRank(b.Group) : string.CompareOrdinal(a.Name, b.Name));
        return catalog;
    }

    private static int GroupRank(string group) => group switch
    {
        "Broadcast" => 0,
        "Three-on-three" => 1,
        "Pro-Am" => 2,
        "Gauntlet" => 3,
        "HUT" => 4,
        "Be a Pro" => 5,
        OtherGroup => 7,
        SharedGroup => 8,
        _ => 6,
    };

    private string? Text(string path) => _read(Canonical(path)) is { } content ? Encoding.UTF8.GetString(content) : null;

    private string Canonical(string path) => _paths.GetValueOrDefault(path, path);

    private bool Exists(string path) => _paths.ContainsKey(path);

    private void ReadTables()
    {
        if (Text(AssetPathsFile) is { } assets && JsonEdit.Parse(assets) is JsonObject root)
        {
            foreach (var table in new[] { "PARTS", "STATIC" })
                if (root[table] is JsonObject entries)
                    foreach (var (name, value) in entries)
                        if (JsonEdit.String(value) is { } relative)
                        {
                            if (Exists(UiRoot + relative))
                                _assets[name] = Canonical(UiRoot + relative);
                            else if (relative.Contains('#'))
                                _numberedAssets[name] = relative;
                        }
        }

        if (Text(FontsFile) is { } fonts && JsonEdit.Parse(fonts)["fonts"] is JsonArray fontList)
            foreach (var font in fontList.OfType<JsonObject>())
                if (JsonEdit.String(font["name"]) is { } name && JsonEdit.String(font["file"]) is { } file && Exists(UiRoot + "fonts/" + file))
                    _fonts[name] = Canonical(UiRoot + "fonts/" + file);

        if (Text(StylesFile) is { } styles)
            _styles = JsonEdit.Parse(styles)[StylesKey] as JsonObject;

        if (Text(ManifestFile) is { } manifest)
            CollectWidgets(JsonEdit.Parse(manifest));

        if (Text(ConstantsFile) is { } constants)
            foreach (Match match in Colour.Matches(constants))
                Colours[match.Groups[1].Value] = Convert.ToUInt32(match.Groups[2].Value, 16);
    }

    private void CollectWidgets(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
                CollectWidgets(item);
        }
        else if (node is JsonObject item)
        {
            if (JsonEdit.String(item["CommonName"]) is { } name && JsonEdit.String(item["extends"]) is { } extends && extends.StartsWith("Layout:", StringComparison.Ordinal))
                _widgetLayouts[name] = UiRoot + extends["Layout:".Length..].TrimStart('/');
            foreach (var (_, value) in item)
                CollectWidgets(value);
        }
    }

    private void AddNavigations()
    {
        var navigations = new List<(string Path, string Text, string Name, List<string> Bundles)>();
        foreach (var navPath in _paths.Values.Where(p => p.StartsWith(NavFolder, StringComparison.OrdinalIgnoreCase) && p.EndsWith(".nav", StringComparison.OrdinalIgnoreCase) && p.IndexOf('/', NavFolder.Length) < 0).OrderBy(p => p, StringComparer.Ordinal))
        {
            var text = Text(navPath);
            if (text == null)
                continue;
            var bundles = LoadBundle.Matches(text).Select(m => UiRoot + m.Groups[1].Value.TrimStart('/')).Where(Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (bundles.Count > 0)
                navigations.Add((navPath, text, JsonEdit.String(JsonEdit.Parse(text)["name"]) ?? Path.GetFileNameWithoutExtension(navPath), bundles));
        }

        // Variants such as the three-on-three score clock reuse one internal name, so their file names tell them apart.
        foreach (var navigation in navigations)
        {
            var stem = Path.GetFileNameWithoutExtension(navigation.Path);
            var shared = navigations.Count(n => n.Name == navigation.Name) > 1 && !stem.Equals(navigation.Name, StringComparison.OrdinalIgnoreCase);
            var name = shared ? $"{navigation.Name} ({stem})" : navigation.Name;
            var overlay = new GameOverlay(name, GroupOf(stem, navigation.Bundles[0])) { NavigationText = navigation.Text };
            overlay.AddFile(navigation.Path, OverlayFileKind.Navigation);
            foreach (var bundle in navigation.Bundles)
                AddBundle(overlay, Canonical(bundle));
            Overlays.Add(overlay);
        }
    }

    private static string GroupOf(string name, string path)
    {
        bool Has(string word) => name.Contains(word, StringComparison.OrdinalIgnoreCase) || path.Contains(word, StringComparison.OrdinalIgnoreCase);
        if (Has("threes") || Has("3on3"))
            return "Three-on-three";
        if (Has("proam"))
            return "Pro-Am";
        if (Has("gauntlet"))
            return "Gauntlet";
        if (Has("hut"))
            return "HUT";
        if (Has("beapro"))
            return "Be a Pro";
        return "Broadcast";
    }

    private void AddBundle(GameOverlay overlay, string bundlePath)
    {
        overlay.AddFile(bundlePath, OverlayFileKind.Bundle);
        if (Text(bundlePath) is not { } text || JsonEdit.Parse(text) is not JsonObject bundle)
            return;

        string Beside(string name) => name.StartsWith('/') ? UiRoot + name.TrimStart('/') : bundlePath[..(bundlePath.LastIndexOf('/') + 1)] + name;

        if (bundle["dds"] is JsonArray data)
            foreach (var name in data.Select(JsonEdit.String).OfType<string>().Where(n => Exists(Beside(n))))
                overlay.AddFile(Canonical(Beside(name)), OverlayFileKind.Data);

        if (bundle["vms"] is JsonArray scripts)
        {
            foreach (var pair in scripts.OfType<JsonArray>().Where(p => p.Count > 1))
            {
                if (JsonEdit.String(pair[1]) is not { } name || !Exists(Beside(name)))
                    continue;
                overlay.AddFile(Canonical(Beside(name)), OverlayFileKind.Script);
                var requires = Path.ChangeExtension(Beside(name), ".lreq");
                if (Exists(requires))
                    overlay.AddFile(Canonical(requires), OverlayFileKind.Requires);
            }
        }

        if (bundle["views"] is JsonArray views)
            foreach (var view in views.OfType<JsonArray>().Where(v => v.Count > 1))
                if (view[1] is JsonObject settings && JsonEdit.String(settings["layout"]) is { } layout && Exists(Beside(layout)))
                    AddLayout(overlay, Canonical(Beside(layout)));
    }

    private void AddLayout(GameOverlay overlay, string layoutPath)
    {
        if (overlay.Layouts.Any(f => f.Path.Equals(layoutPath, StringComparison.OrdinalIgnoreCase)))
            return;
        overlay.AddFile(layoutPath, OverlayFileKind.Layout);
        if (Text(layoutPath) is { } text)
            WalkLayout(overlay, JsonEdit.Parse(text));
    }

    private void WalkLayout(GameOverlay overlay, JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
                WalkLayout(overlay, item);
            return;
        }
        if (node is not JsonObject widget)
            return;

        if (JsonEdit.String(widget["commonName"]) is { } kind)
        {
            if (JsonEdit.String(widget["type"]) is { Length: > 0 } asset && _assets.TryGetValue(asset, out var path))
                AddArtOrMovie(overlay, path, asset);
            if (JsonEdit.String(widget["movie"]) is { Length: > 0 } movie && Exists(movie) && !overlay.Videos.Contains(movie))
                overlay.Videos.Add(Canonical(movie));
            if (_widgetLayouts.TryGetValue(kind, out var custom) && Exists(custom))
                AddLayout(overlay, Canonical(custom));
        }

        foreach (var (_, value) in widget)
            WalkLayout(overlay, value);
    }

    private void AddArtOrMovie(GameOverlay overlay, string path, string usage)
    {
        if (path.EndsWith(".gfx", StringComparison.OrdinalIgnoreCase))
            AddMovie(overlay, path);
        else
            overlay.AddArt(path, usage);
    }

    private void AddMovie(GameOverlay overlay, string moviePath)
    {
        overlay.AddFile(moviePath, OverlayFileKind.Movie);
        if (_read(moviePath) is not { } content || !GfxMovie.IsMovie(content))
            return;
        var folder = moviePath[..(moviePath.LastIndexOf('/') + 1)];
        foreach (var image in GfxMovie.Parse(content).Images)
            if (Exists(folder + image.FileName))
                overlay.AddArt(Canonical(folder + image.FileName), Path.GetFileName(moviePath));
    }

    private void AddDefinitions()
    {
        if (Text(DefinitionPath) is not { } text || JsonEdit.Parse(text)["overlays"] is not JsonArray definitions)
            return;

        foreach (var definition in definitions.OfType<JsonObject>())
        {
            if (JsonEdit.String(definition["name"]) is not { } name)
                continue;
            var show = JsonEdit.String(definition["showAction"]);
            var matches = Overlays.Where(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0 && show != null)
                matches = Overlays.Where(o => o.NavigationText?.Contains($"\"{show}\"", StringComparison.Ordinal) == true).ToList();
            if (matches.Count == 0)
            {
                var alone = new GameOverlay(name, GroupOf(name, ""));
                matches.Add(alone);
                Overlays.Add(alone);
            }
            foreach (var overlay in matches)
            {
                overlay.Definitions.Add(name);
                overlay.AddFile(DefinitionPath, OverlayFileKind.Definition);
            }
        }
    }

    /// <summary>Art found in overlay folders that no layout names. Movies load some of it from code, so it joins an overlay of the same name or stands alone.</summary>
    private void AddRemainingArt()
    {
        var claimed = new HashSet<string>(Overlays.SelectMany(o => o.Art.Select(a => a.Path)), StringComparer.OrdinalIgnoreCase);
        var candidates = _paths.Values.Where(p => p.StartsWith(UiRoot, StringComparison.OrdinalIgnoreCase) && p.Contains("overlay", StringComparison.OrdinalIgnoreCase)
            && (p.EndsWith(".gnf", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".gfx", StringComparison.OrdinalIgnoreCase)) && !claimed.Contains(p));

        foreach (var folder in candidates.GroupBy(p => p[..p.LastIndexOf('/')], StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var leaf = folder.Key[(folder.Key.LastIndexOf('/') + 1)..];
            var overlay = Overlays.FirstOrDefault(o => o.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase));
            if (overlay == null)
            {
                overlay = new GameOverlay(TidyFolder(folder.Key), OtherGroup);
                Overlays.Add(overlay);
            }

            foreach (var movie in folder.Where(p => p.EndsWith(".gfx", StringComparison.OrdinalIgnoreCase)))
                AddMovie(overlay, movie);
            foreach (var texture in folder.Where(p => p.EndsWith(".gnf", StringComparison.OrdinalIgnoreCase)))
                overlay.AddArt(texture, "Same folder");
        }
    }

    private static string TidyFolder(string folder)
    {
        var parts = folder.Split('/');
        var at = Array.FindIndex(parts, p => p.Contains("overlay", StringComparison.OrdinalIgnoreCase));
        return string.Join(" / ", parts.Skip(at >= 0 && at < parts.Length - 1 ? at + 1 : Math.Max(0, parts.Length - 2)));
    }

    private void AddShared()
    {
        var shared = new GameOverlay("Tables and scripts every overlay uses", SharedGroup);
        shared.AddFile(DefinitionPath, OverlayFileKind.Definition);
        shared.AddFile(AssetPathsFile, OverlayFileKind.Data);
        shared.AddFile(StylesFile, OverlayFileKind.Data);
        shared.AddFile(ConstantsFile, OverlayFileKind.Data);
        foreach (var script in SharedScripts.Where(Exists))
            shared.AddFile(Canonical(script), script.EndsWith(".lreq", StringComparison.Ordinal) ? OverlayFileKind.Requires : OverlayFileKind.Script);
        foreach (var nav in _paths.Values.Where(p => p.StartsWith(NavFolder, StringComparison.OrdinalIgnoreCase) && p.EndsWith(".nav", StringComparison.OrdinalIgnoreCase) && p.IndexOf('/', NavFolder.Length) < 0).OrderBy(p => p, StringComparer.Ordinal))
            if (Overlays.All(o => o.Files.All(f => !f.Path.Equals(nav, StringComparison.OrdinalIgnoreCase))))
                shared.AddFile(nav, OverlayFileKind.Navigation);
        Overlays.Add(shared);
    }
}
