using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core;
using Blueline.Core.Teams;
using Blueline.Formats.Models;
using Blueline.Formats.Tdb;
using Blueline.Formats.Textures;

namespace Blueline.App.Modules;

/// <summary>
/// The uniform workspace: a kit on the game's meshes, large, with the glove and helmet colour editor beside it. Choosing a
/// zone in the editor shows it on the model (the rest dims and the zone gets an outline), and clicking the gloves or helmet
/// selects the zone under the mouse. The meshes are built once per kit; a zone change only re-tints the gear textures.
/// </summary>
public partial class UniformStudio : UserControl
{
    private const double FrontYaw = 0.45, FrontPitch = 0.12, Closeness = 4.0, GloveCloseness = 4.2, HelmetCloseness = 3.2;
    private const int Outline = 0x00E5FF;

    private readonly AppServices _services;
    private readonly GearColoursPanel _panel;
    private TeamInfo? _team;
    private IReadOnlyList<UniformKit> _kits = [];
    private UniformKit? _kit;
    private string? _key;
    private int _generation;
    private bool _everShown;
    private bool _picking;
    private (GearPart Part, int Zone)? _focused;
    private (GearPart Part, int Zone) _highlight;
    private readonly Dictionary<GearPart, int> _tintGeneration = [];
    private readonly Dictionary<string, PieceMaps> _maps = [];

    /// <summary>The gear pieces of the model on screen: which viewport part each is, and the maps to tint it with.</summary>
    private List<GearPiece> _pieces = [];

    /// <summary>The zone colours each part had when the model was loaded, used when the gear table is not open in the editor.</summary>
    private Dictionary<GearPart, int[]?> _loadedZones = [];

    private GearKit? _gearKit;

    private sealed record PieceMaps(RgbaImage Detail, RgbaImage? Ambient, IReadOnlyList<RgbaImage?> Masks, GearZoneMap Map);
    private sealed record GearPiece(int PartIndex, GearPart Part, PieceMaps Maps);

    public UniformStudio(AppServices services, GearColoursPanel panel)
    {
        _services = services;
        _panel = panel;
        InitializeComponent();
        SizeBox.ItemsSource = Enum.GetValues<UniformSize>();
        TuckBox.ItemsSource = Enum.GetValues<UniformTuck>();
        PantsBox.ItemsSource = Enum.GetValues<UniformPants>();
        SizeBox.SelectedIndex = TuckBox.SelectedIndex = PantsBox.SelectedIndex = 0;
        EditorHost.Content = panel;
        panel.CloseButtonVisible = false;
        panel.WantedKit = gear => _team != null && _kit != null ? gear.KitFor(_team, _kit) : null;
        panel.ZoneHighlighted += OnZoneHighlighted;
        panel.KitChanged += OnPanelKit;
        panel.Edited += () => Dispatcher.BeginInvoke(RetintAll);
        Viewport.Picked += OnPicked;
    }

    public event Action? BackRequested;

    /// <summary>Raised when a kit is chosen here, so the gallery can select its jersey sheet.</summary>
    public event Action<UniformKit>? KitChosen;

    public ModelViewport View => Viewport;
    private bool _loading;
    private int _tints;

    /// <summary>True while the player is being dressed or the gear re-tinted.</summary>
    public bool Busy => _loading || _tints > 0;
    public UniformKit? Kit => _kit;
    public bool IsOpen => Visibility == Visibility.Visible;

    // ---- What is shown

    public void SetGearModels(IReadOnlyList<GearModel> gloves, IReadOnlyList<GearModel> helmets)
    {
        GloveBox.ItemsSource = gloves;
        GloveBox.SelectedItem = gloves.FirstOrDefault(m => m.Name == GearPreview.DefaultGloves) ?? gloves.FirstOrDefault();
        HelmetBox.ItemsSource = helmets;
        HelmetBox.SelectedItem = helmets.FirstOrDefault(m => m.Name == GearPreview.DefaultHelmet) ?? helmets.FirstOrDefault();
    }

    public void SetOptions(UniformSize size, UniformTuck tuck, UniformPants pants)
    {
        SizeBox.SelectedItem = size;
        TuckBox.SelectedItem = tuck;
        PantsBox.SelectedItem = pants;
    }

    /// <summary>Shows a team's kits, keeping the same kind of kit (home, away) where the new team has one.</summary>
    public void SetTeam(TeamInfo? team, IReadOnlyList<UniformKit> kits, UniformKit? kit = null)
    {
        var sameTeam = team == _team;
        _team = team;
        _kits = kits;
        var wanted = kit ?? (sameTeam && _kit != null && kits.Contains(_kit) ? _kit : null)
                     ?? kits.FirstOrDefault(k => k.Brand == _kit?.Brand && k.Kit == _kit?.Kit)
                     ?? kits.FirstOrDefault(k => k.Brand == "adidas" && k.Kit == "home") ?? kits.FirstOrDefault();
        BuildKitChips();
        SetKit(wanted);
    }

    public void SetKit(UniformKit? kit)
    {
        _kit = kit;
        foreach (var chip in KitChips.Children.OfType<ToggleButton>())
            chip.IsChecked = Equals(chip.Tag, kit);
        _settingOlder = true;
        OlderBox.SelectedItem = kit != null && kit.Brand != "adidas" ? kit : null;
        _settingOlder = false;
        if (kit != null && _panel.Gear is { } gear && _team != null)
            _panel.SelectKit(gear.KitFor(_team, kit));
        Reload();
    }

    /// <summary>Drops what was read, after the project or the game changed.</summary>
    public void Invalidate()
    {
        _key = null;
        _maps.Clear();
        if (IsOpen)
            Reload();
    }

    private bool _settingOlder;

    private void BuildKitChips()
    {
        KitChips.Children.Clear();
        var older = _kits.Where(k => k.Brand != "adidas").OrderBy(k => k.Brand).ThenBy(k => KitOrder(k.Kit)).ToList();
        _settingOlder = true;
        OlderBox.ItemsSource = older;
        OlderBox.SelectedItem = null;
        _settingOlder = false;
        OlderBox.Visibility = OlderLabel.Visibility = older.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var kit in _kits.Where(k => k.Brand == "adidas").OrderBy(k => KitOrder(k.Kit)))
        {
            var chip = new ToggleButton
            {
                Content = kit.Label[(kit.Label.IndexOf(", ", StringComparison.Ordinal) + 2)..],
                Tag = kit,
                Style = (Style)FindResource("Chip"),
                Margin = new Thickness(0, 0, 4, 4),
                ToolTip = kit.Jersey,
            };
            chip.Click += (_, _) =>
            {
                SetKit(kit);
                KitChosen?.Invoke(kit);
            };
            KitChips.Children.Add(chip);
        }
        if (_kits.Count > 0 && KitChips.Children.Count == 0)
            KitChips.Children.Add(new TextBlock { Text = "No current (adidas) kits.", Style = (Style)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4) });
        if (_kits.Count == 0)
            KitChips.Children.Add(new TextBlock { Text = "This team has no uniform sheets.", Style = (Style)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center });
    }

    private void OnOlderKit(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingOlder && OlderBox.SelectedItem is UniformKit kit && kit != _kit)
        {
            SetKit(kit);
            KitChosen?.Invoke(kit);
        }
    }

    private static int KitOrder(string kit) => kit switch { "home" => 0, "away" => 1, "two" => 2, "three" => 3, _ => 4 };

    private void OnOption(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            Reload();
    }

    private void OnBack(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    // ---- Loading the player

    private void Reload()
    {
        if (!IsOpen)
            return;
        if (_kit is not { } kit)
        {
            _key = null;
            _pieces = [];
            Viewport.ShowMessage(_team == null ? "Choose a team." : "This team has no uniform sheets to draw.");
            Note.Text = "";
            return;
        }
        var workspace = _services.Workspace;
        if (workspace.Vfs == null || SizeBox.SelectedItem is not UniformSize size || TuckBox.SelectedItem is not UniformTuck tuck || PantsBox.SelectedItem is not UniformPants pants)
            return;
        var gloves = GloveBox.SelectedItem as GearModel;
        var helmet = HelmetBox.SelectedItem as GearModel;
        var key = $"{kit.Key}|{size}|{tuck}|{pants}|{gloves}|{helmet}";
        if (key == _key)
            return;
        _key = key;
        var generation = ++_generation;
        _loading = true;
        var keepView = _everShown;
        if (!keepView)
            Viewport.ShowMessage("Dressing the player");

        // The gear colours are taken on this thread: the editor's table can change at any time.
        var team = _team;
        var gearTeam = team != null ? GearColours.GearTeamOf(team) : null;
        var live = _panel.Team == team ? _panel.Gear : null;
        GearKit? gearKit = null;
        Dictionary<GearPart, int[]?>? zones = null;
        if (live != null && team != null)
        {
            gearKit = live.KitFor(team, kit);
            zones = Enum.GetValues<GearPart>().ToDictionary(p => p, p => gearKit != null && gearTeam is { } t ? live.Resolve(t, gearKit, p).Row?.Zones.ToArray() : null);
        }
        var cached = new Dictionary<string, PieceMaps>(_maps);
        byte[]? Read(string path) => workspace.ReadCurrent(path);

        Task.Run(() =>
        {
            if (zones == null && team != null && gearTeam is { } t && Read(TeamCatalog.DatabasePath) is { } data)
            {
                var file = TdbFile.Parse((byte[])data.Clone());
                if (Read(TeamCatalog.MetaPath) is { } meta)
                    file.ApplyMeta(System.Text.Encoding.UTF8.GetString(meta));
                var gear = GearColours.Open(file);
                gearKit = gear.KitFor(team, kit);
                zones = Enum.GetValues<GearPart>().ToDictionary(p => p, p => gearKit != null ? gear.Resolve(t, gearKit, p).Row?.Zones.ToArray() : null);
            }
            zones ??= [];

            var models = UniformKits.ModelsFor(kit.Brand, size, tuck, pants);
            var jersey = SheetBitmap(Read(kit.Jersey));
            var parts = new List<ModelPart>();
            void Add(string path, BitmapSource? texture)
            {
                if (Read(path) is { } mesh)
                    parts.Add(new ModelPart(McdFile.Parse(mesh), texture));
            }
            Add(models.Jersey, jersey);
            Add(models.JerseyTie, jersey);
            Add(models.Pant, kit.Pant == null ? null : SheetBitmap(Read(kit.Pant)));
            Add(models.Sock, kit.Sock == null ? null : SheetBitmap(Read(kit.Sock)));
            foreach (var path in models.Gear)
                Add(path, null);

            var pieces = new List<GearPiece>();
            var read = new Dictionary<string, PieceMaps>();
            foreach (var model in new[] { gloves, helmet }.OfType<GearModel>())
                foreach (var (mesh, prefix) in model.Pieces)
                {
                    if (Read(mesh) is not { } meshData)
                        continue;
                    if (!cached.TryGetValue(prefix, out var maps) && Decode(Read(model.DetailMap(prefix))) is { } detail)
                    {
                        var masks = Enumerable.Range(1, 4).Select(n => Decode(Read(GearModel.MaskMap(prefix, n)))).ToList();
                        maps = new PieceMaps(detail, Decode(Read(GearModel.AmbientMap(prefix))), masks, GearZoneMap.Build(masks));
                    }
                    if (maps != null)
                        read[prefix] = maps;
                    var colours = zones.GetValueOrDefault(model.Part);
                    var texture = maps != null && colours != null ? ToBitmap(GearPreview.Tint(maps.Detail, maps.Ambient, maps.Masks, colours)) : null;
                    if (maps != null)
                        pieces.Add(new GearPiece(parts.Count, model.Part, maps));
                    parts.Add(new ModelPart(McdFile.Parse(meshData), texture));
                }
            return (ModelViewport.Prepare(parts), pieces, read, zones, gearKit);
        }).ContinueWith(task =>
        {
            if (generation != _generation)
                return;
            _loading = false;
            if (task.IsFaulted)
            {
                _key = null;
                _pieces = [];
                _services.Log.Error($"3D uniform {kit.Key}", task.Exception!.GetBaseException());
                Viewport.ShowMessage("This uniform could not be drawn: " + task.Exception.GetBaseException().Message);
                Note.Text = "";
                return;
            }
            var (prepared, pieces, read, loadedZones, shownGearKit) = task.Result;
            foreach (var (prefix, maps) in read)
                _maps[prefix] = maps;
            _pieces = pieces;
            _loadedZones = loadedZones;
            _gearKit = shownGearKit;
            Viewport.ShowEditable(prepared, keepView, Closeness);
            if (!keepView)
                Viewport.SetView(FrontYaw, FrontPitch);
            _everShown = true;
            foreach (var part in Enum.GetValues<GearPart>())
                _panel.SetModelZones(part, Coverage(part));
            if (_highlight.Zone > 0)
                Retint(_highlight.Part);
            ShowNote();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The zones the shown model of a part has, from all its pieces (both hands for gloves).</summary>
    private IReadOnlyDictionary<int, double>? Coverage(GearPart part)
    {
        var pieces = _pieces.Where(p => p.Part == part).ToList();
        if (pieces.Count == 0)
            return null;
        var merged = new Dictionary<int, double>();
        foreach (var piece in pieces)
            foreach (var (zone, share) in piece.Maps.Map.Coverage)
                merged[zone] = Math.Max(merged.GetValueOrDefault(zone), share);
        return merged;
    }

    private void ShowNote()
    {
        if (_kit is not { } kit)
            return;
        var notes = new List<string> { kit.Label };
        var missing = new[] { kit.Pant == null ? "pants" : null, kit.Sock == null ? "socks" : null }.OfType<string>().ToList();
        if (missing.Count > 0)
            notes.Add($"The game has no {string.Join(" or ", missing)} sheet for this kit, so they are grey");
        if (_team != null && GearColours.GearTeamOf(_team) == null)
            notes.Add("This team is not linked to the gear table, so the gloves and helmet are grey");
        else if (_gearKit == null)
            notes.Add(kit.Brand == "adidas"
                ? "No gear row is linked to this kit, so the gloves and helmet are grey"
                : "Older (Reebok and Nineties) kits have no linked gear rows, so the gloves and helmet are grey");
        else
        {
            var grey = Enum.GetValues<GearPart>().Where(p => _loadedZones.GetValueOrDefault(p) == null).Select(p => p.ToString().ToLowerInvariant()).ToList();
            notes.Add($"Gloves and helmet use the {_gearKit.Label} gear rows" + (GearColours.IsGuessedLink(kit) ? " (which alternate goes with which jersey is a guess from the sheet names)" : "")
                      + (grey.Count > 0 ? $"; {string.Join(" and ", grey)} grey (no row)" : ""));
            if (_panel.Kit is { } editing && editing.Variant != _gearKit.Variant)
                notes.Add($"The editor shows {editing.Label}, not this kit");
        }
        notes.Add("Numbers, names, shine and fabric detail are not drawn; skates stay grey");
        Note.Text = string.Join(". ", notes) + ".";
    }

    /// <summary>A kit chosen in the editor brings its jersey onto the player, where the team has sheets for it.</summary>
    private void OnPanelKit(GearKit? gearKit)
    {
        if (gearKit == null || _team == null || _panel.Gear is not { } gear || !IsOpen)
            return;
        if (_kit != null && gear.KitFor(_team, _kit)?.Variant == gearKit.Variant)
            return;
        var match = _kits.FirstOrDefault(k => gear.KitFor(_team, k)?.Variant == gearKit.Variant);
        if (match != null)
        {
            SetKit(match);
            KitChosen?.Invoke(match);
        }
        else
            ShowNote();
    }

    // ---- Zones

    private void OnZoneHighlighted(GearPart part, int zone)
    {
        var before = _highlight;
        _highlight = (part, zone);
        if (before.Part != part && before.Zone > 0)
            Retint(before.Part);
        Retint(part);
        ShowBadge();

        // A zone chosen in the editor brings the camera to it; a hover or a click on the model does not.
        var selected = _panel.SelectedZone;
        if (selected == (part, zone) && selected != _focused)
        {
            _focused = selected;
            if (!_picking)
                FocusZone(part, zone);
        }
        else if (selected == null)
            _focused = null;
    }

    private void ShowBadge()
    {
        var (part, zone) = _highlight;
        if (zone <= 0)
        {
            ZoneBadge.Visibility = Visibility.Collapsed;
            return;
        }
        var colour = ZonesNow(part) is { } zones && zone <= zones.Length ? zones[zone - 1] : (int?)null;
        ZoneSwatch.Background = colour is { } c ? new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c)) : Brushes.Transparent;
        ZoneText.Text = $"{part} zone {zone}" + (colour is { } rgb ? $"  #{GearColours.Hex(rgb)}" : "") + (_panel.SelectedZone == (part, zone) ? "" : "  (hover)");
        ZoneBadge.Visibility = Visibility.Visible;
    }

    private void FocusZone(GearPart part, int zone)
    {
        var piece = _pieces.Where(p => p.Part == part).OrderByDescending(p => p.Maps.Map.Coverage.GetValueOrDefault(zone)).FirstOrDefault();
        if (piece != null)
            Viewport.FocusOn(Viewport.PartBounds(piece.PartIndex), part == GearPart.Gloves ? GloveCloseness : HelmetCloseness);
    }

    private void OnPicked(int partIndex, Point uv)
    {
        if (_pieces.FirstOrDefault(p => p.PartIndex == partIndex) is not { } piece)
        {
            // The jersey, pants or socks: nothing to select.
            if (_panel.SelectedZone != null)
                _panel.SelectZone(_highlight.Part, 0);
            return;
        }
        var zone = piece.Maps.Map.ZoneAt(uv.X, uv.Y);
        _picking = true;
        try
        {
            if (zone > 0)
                _panel.SelectZone(piece.Part, zone);
            else
            {
                _panel.SelectZone(piece.Part, 0);
                _services.Notify($"That part of the {piece.Part.ToString().ToLowerInvariant()} is not tinted by any zone (it is in the detail layer).");
            }
        }
        finally
        {
            _picking = false;
        }
    }

    /// <summary>The colours a part shows now: the editor's working copy when it is open on this team, else those read at load.</summary>
    private int[]? ZonesNow(GearPart part)
    {
        if (_panel.Team == _team && _panel.Gear is { } gear && _team != null && GearColours.GearTeamOf(_team) is { } gearTeam && _kit != null)
            return gear.KitFor(_team, _kit) is { } kit ? gear.Resolve(gearTeam, kit, part).Row?.Zones.ToArray() : null;
        return _loadedZones.GetValueOrDefault(part);
    }

    private void RetintAll()
    {
        foreach (var part in Enum.GetValues<GearPart>())
            Retint(part);
        ShowBadge();
        ShowNote();
    }

    /// <summary>Tints one part's pieces again in the background, with the highlight if it is on this part. Only the newest result is shown.</summary>
    private void Retint(GearPart part)
    {
        var pieces = _pieces.Where(p => p.Part == part).ToList();
        if (pieces.Count == 0)
            return;
        var generation = _tintGeneration[part] = _tintGeneration.GetValueOrDefault(part) + 1;
        var zones = ZonesNow(part);
        var highlight = _highlight.Part == part ? _highlight.Zone : 0;
        if (zones == null && highlight > 0)
            zones = Enumerable.Repeat(0x808080, GearColours.ZoneCount).ToArray();
        _tints++;
        Task.Run(() => pieces.Select(p => (p.PartIndex, Texture: zones == null ? null
            : ToBitmap(GearPreview.Tint(p.Maps.Detail, p.Maps.Ambient, p.Maps.Masks, zones, highlight, p.Maps.Map, Outline)))).ToList())
            .ContinueWith(task =>
            {
                _tints--;
                if (task.IsFaulted || _tintGeneration[part] != generation || !ReferenceEquals(pieces[0], _pieces.FirstOrDefault(p => p.Part == part)))
                    return;
                foreach (var (index, texture) in task.Result)
                    Viewport.SetTexture(index, texture);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---- Camera

    private void OnViewChip(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string view })
            ShowView(view);
    }

    /// <summary>Front, back, gloves or helmet.</summary>
    public void ShowView(string view)
    {
        foreach (var chip in new[] { FullChip, BackChip, GlovesChip, HelmetChip })
            chip.IsChecked = (string)chip.Tag == view;
        switch (view)
        {
            case "front":
                Viewport.ResetView(FrontYaw, FrontPitch, Closeness);
                break;
            case "back":
                Viewport.ResetView(Math.PI + FrontYaw, FrontPitch, Closeness);
                break;
            case "gloves" or "helmet":
                var part = view == "gloves" ? GearPart.Gloves : GearPart.Helmet;
                if (_pieces.FirstOrDefault(p => p.Part == part) is { } piece)
                    Viewport.FocusOn(Viewport.PartBounds(piece.PartIndex), part == GearPart.Gloves ? GloveCloseness : HelmetCloseness, FrontYaw, view == "helmet" ? 0.25 : FrontPitch);
                break;
        }
    }

    // ---- Pictures

    /// <summary>A texture's mip at most 512 wide, or null if it is missing or unreadable.</summary>
    private static RgbaImage? Decode(byte[]? content)
    {
        if (content == null || GameTexture.TryOpen(content) is not { Blocker: null } texture)
            return null;
        var mip = 0;
        while (mip + 1 < texture.MipCount && Math.Max(texture.Width >> mip, texture.Height >> mip) > 512)
            mip++;
        return texture.DecodeMip(mip);
    }

    private static BitmapSource ToBitmap(RgbaImage image)
    {
        var pixels = new byte[image.Pixels.Length];
        for (var i = 0; i + 3 < pixels.Length; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (image.Pixels[i + 2], image.Pixels[i + 1], image.Pixels[i], 255);
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr32, null, pixels, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A sheet for wrapping on a mesh, at most 1024 wide. Its alpha holds other data, so it is left out.</summary>
    private static BitmapSource? SheetBitmap(byte[]? content)
    {
        if (content == null || GameTexture.TryOpen(content) is not { Blocker: null } texture)
            return null;
        var mip = 0;
        while (mip + 1 < texture.MipCount && Math.Max(texture.Width >> mip, texture.Height >> mip) > 1024)
            mip++;
        return ToBitmap(texture.DecodeMip(mip));
    }
}
