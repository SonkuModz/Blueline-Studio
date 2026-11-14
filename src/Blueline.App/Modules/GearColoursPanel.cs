using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Blueline.App.Shell;
using Blueline.Core.Teams;
using Blueline.Formats.Tdb;

namespace Blueline.App.Modules;

/// <summary>
/// Glove and helmet colours for each of a team's current kits, edited in the game database's team gear table. Edits stay in
/// a working copy until saved to the project as one change, like the Database tab.
/// </summary>
public sealed class GearColoursPanel : UserControl
{
    private readonly AppServices _services;
    private readonly ComboBox _kitBox = new() { MinWidth = 180, FontSize = 12 };
    private readonly StackPanel _body = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) };
    private readonly Button _save = new() { Content = "Save to project", Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _discard = new() { Content = "Discard" };
    private TeamInfo? _team;
    private GearColours? _gear;
    private string? _loadedBlob;
    private bool _dirty;
    private bool _showing;
    private readonly Dictionary<GearPart, IReadOnlyDictionary<int, double>> _modelZones = [];
    private readonly Dictionary<(GearPart Part, int Zone), Border> _zoneRows = [];
    private (GearPart Part, int Zone)? _selected;

    public event Action? CloseRequested;

    /// <summary>A zone to show on the model: hovered (for a moment) or selected. Zone 0 shows none.</summary>
    public event Action<GearPart, int>? ZoneHighlighted;

    /// <summary>Raised when the kit being edited changes, so the 3D view can follow.</summary>
    public event Action<GearKit?>? KitChanged;

    /// <summary>Raised after any change to the working copy, so the 3D preview can follow unsaved edits.</summary>
    public event Action? Edited;

    public GearColoursPanel(AppServices services)
    {
        _services = services;
        _save.Style = (Style)FindResource("Primary");
        _save.Click += (_, _) => SaveToProject();
        _discard.Click += (_, _) => { _dirty = false; Load(); };
        _kitBox.SelectionChanged += (_, _) =>
        {
            ShowKit();
            KitChanged?.Invoke(_kitBox.SelectedItem as GearKit);
        };

        var close = _close = new Button { Content = "Back to the file", Style = (Style)FindResource("Quiet"), FontSize = 12, Padding = new Thickness(6, 0, 6, 0) };
        close.Click += (_, _) => CloseRequested?.Invoke();
        var title = new DockPanel { Margin = new Thickness(2, 0, 0, 4) };
        DockPanel.SetDock(close, Dock.Right);
        title.Children.Add(close);
        title.Children.Add(new TextBlock { Text = "Glove and helmet colours", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var warning = Dim("A roster save loaded in the game keeps its own copy of these colours and wins over this table. To see an edit, play with the stock roster (no roster save loaded).");
        warning.Foreground = (Brush)FindResource("WarningBrush");
        warning.Margin = new Thickness(2, 0, 0, 8);

        var kitRow = new DockPanel { Margin = new Thickness(2, 0, 0, 8) };
        var kitLabel = Dim("Kit");
        kitLabel.Margin = new Thickness(0, 0, 8, 0);
        kitLabel.VerticalAlignment = VerticalAlignment.Center;
        kitRow.Children.Add(kitLabel);
        kitRow.Children.Add(_kitBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(_save);
        buttons.Children.Add(_discard);
        var bottom = new StackPanel();
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var top = new StackPanel();
        top.Children.Add(title);
        top.Children.Add(warning);
        top.Children.Add(kitRow);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(8),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _body },
        });
        Content = root;
        SetDirty(false);

        services.Workspace.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (_showing && !_dirty && CurrentBlob() != _loadedBlob)
                Load();
        });
    }

    private Button _close = null!;

    /// <summary>Whether the panel shows its own way back (it does not when it sits in the uniform studio).</summary>
    public bool CloseButtonVisible
    {
        get => _close.Visibility == Visibility.Visible;
        set => _close.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The kit to open on when the table is (re)read, so the editor follows the kit on screen.</summary>
    public Func<GearColours, GearKit?>? WantedKit { get; set; }

    public bool IsDirty => _dirty;
    public GearKit? Kit => _kitBox.SelectedItem as GearKit;
    public (GearPart Part, int Zone)? SelectedZone => _selected;

    /// <summary>Shows a kit, as choosing it in the list would. Unknown kits are ignored.</summary>
    public void SelectKit(GearKit? kit)
    {
        if (kit != null && _kitBox.Items.Cast<GearKit>().FirstOrDefault(k => k.Variant == kit.Variant) is { } match && !Equals(match, _kitBox.SelectedItem))
            _kitBox.SelectedItem = match;
    }

    /// <summary>The zones the model on screen really has for a part, with how much of it each covers. They are listed first.</summary>
    public void SetModelZones(GearPart part, IReadOnlyDictionary<int, double>? zones)
    {
        if (zones == null)
            _modelZones.Remove(part);
        else
            _modelZones[part] = zones;
        if (_showing)
            ShowKit();
    }

    /// <summary>The colours of the row the shown kit reads for a part, copied, so a preview can use them on another thread.</summary>
    public int[]? ZonesFor(GearPart part) =>
        _gear != null && _team != null && GearColours.GearTeamOf(_team) is { } gearTeam && Kit is { } kit
            ? _gear.Resolve(gearTeam, kit, part).Row?.Zones.ToArray()
            : null;

    /// <summary>Selects a zone, as clicking its row would, and scrolls to it. Zone 0 clears the selection.</summary>
    public void SelectZone(GearPart part, int zone)
    {
        _selected = zone > 0 ? (part, zone) : null;
        RestyleZones();
        if (zone > 0 && _zoneRows.TryGetValue((part, zone), out var row))
            row.BringIntoView();
        ZoneHighlighted?.Invoke(part, zone);
    }

    private void RestyleZones()
    {
        foreach (var ((part, zone), row) in _zoneRows)
        {
            var on = _selected == (part, zone);
            row.Background = on ? (Brush)FindResource("SelectBrush") : Brushes.Transparent;
            row.BorderBrush = on ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
        }
    }
    public TeamInfo? Team => _team;
    public GearColours? Gear => _gear;
    public ComboBox KitBox => _kitBox;

    /// <summary>Shows a team. Unsaved edits to another team are kept only if the user says so.</summary>
    public bool SetTeam(TeamInfo? team)
    {
        if (team == _team)
            return true;
        if (_dirty && !Controls.ConfirmDialog.Ask("Unsaved colours", $"The glove and helmet colours of {_team?.Name} have edits that are not in the project yet. Discard them?", "Discard"))
            return false;
        _team = team;
        _dirty = false;
        if (_showing)
            Load();
        return true;
    }

    public void SetShowing(bool showing)
    {
        _showing = showing;
        if (showing && (_gear == null || (!_dirty && CurrentBlob() != _loadedBlob)))
            Load();
        else if (showing)
            ShowKit();
    }

    private static string? CurrentBlobOf(AppServices services) =>
        services.Workspace.Project?.TryGet(TeamCatalog.DatabasePath, out var change) == true ? change.Blob : null;

    private string? CurrentBlob() => CurrentBlobOf(_services);

    private void Load()
    {
        _gear = null;
        Edited?.Invoke();
        _body.Children.Clear();
        _kitBox.ItemsSource = null;
        SetDirty(false);
        _status.Text = "";
        var workspace = _services.Workspace;
        if (_team == null || workspace.Vfs is not { } vfs)
            return;
        if (GearColours.GearTeamOf(_team) == null)
        {
            _body.Children.Add(Dim($"{_team.Name} is not linked to the gear table yet. The link (team number plus one) has only been checked for NHL clubs."));
            return;
        }
        try
        {
            var file = TdbFile.Parse((byte[])workspace.ReadCurrent(TeamCatalog.DatabasePath)!.Clone());
            if (vfs.Find(TeamCatalog.MetaPath) is { } meta)
                file.ApplyMeta(Encoding.UTF8.GetString(vfs.ReadContent(meta)));
            _gear = GearColours.Open(file);
            _loadedBlob = CurrentBlob();
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            _services.ReportError("The gear table could not be read", e);
            return;
        }
        var kits = _gear.KitsOf(_team);
        var previous = (_kitBox.SelectedItem as GearKit)?.Variant;
        _kitBox.ItemsSource = kits;
        var wanted = WantedKit?.Invoke(_gear)?.Variant;
        _kitBox.SelectedItem = kits.FirstOrDefault(k => k.Variant == wanted) ?? kits.FirstOrDefault(k => k.Variant == previous) ?? kits.FirstOrDefault();
        if (kits.Count == 0)
            ShowKit();
    }

    private void ShowKit()
    {
        _body.Children.Clear();
        _zoneRows.Clear();
        if (_gear == null || _team == null || GearColours.GearTeamOf(_team) is not { } gearTeam)
            return;
        if (_kitBox.SelectedItem is GearKit kit)
            foreach (var part in Enum.GetValues<GearPart>())
                ShowPart(gearTeam, kit, part);
        else
            _body.Children.Add(Dim("This team has no current (adidas) kits in the jersey table."));

        var older = _gear.RowsOf(gearTeam).Where(r => !r.IsCurrentEra).ToList();
        if (older.Count == 0)
            return;
        var list = new StackPanel();
        list.Children.Add(Dim("Rows for older jersey styles. Which era goes with which style has not been seen in game, so they are listed by number."));
        foreach (var row in older)
        {
            var label = new TextBlock { Text = $"{row.Part}, era {row.Era}, variant {row.Variant} (row {row.Index})", FontSize = 12, Margin = new Thickness(0, 8, 0, 2) };
            list.Children.Add(label);
            list.Children.Add(RowEditor(row, null));
        }
        _body.Children.Add(new Expander { Header = $"Older rows  {older.Count}", Content = list, Margin = new Thickness(0, 12, 0, 0) });
    }

    private void ShowPart(int gearTeam, GearKit kit, GearPart part)
    {
        var resolution = _gear!.Resolve(gearTeam, kit, part);
        _body.Children.Add(new TextBlock { Text = part.ToString(), Style = (Style)FindResource("Section"), Margin = new Thickness(0, _body.Children.Count == 0 ? 0 : 14, 0, 4) });

        var others = resolution.Row == null ? [] : _gear.KitsUsing(_team!, resolution.Row).Where(k => k != kit).Select(k => k.Label).ToList();
        var note = resolution.Link switch
        {
            GearLink.Own => $"This kit has its own row ({resolution.Row!.Index})." + (others.Count > 0 ? $" Also worn with: {string.Join(", ", others)}." : ""),
            GearLink.Fallback => $"No row of its own: this kit shares the fallback row ({resolution.Row!.Index})"
                + (others.Count > 0 ? $" with {string.Join(", ", others)}, so a change here changes them too." : "."),
            _ => "No row found for this kit. The game shows something this table does not explain.",
        };
        var text = Dim(note);
        if (resolution.Link == GearLink.Fallback && others.Count > 0)
            text.Foreground = (Brush)FindResource("WarningBrush");
        _body.Children.Add(text);

        if (resolution.Link != GearLink.Own)
        {
            if (kit.GearVariant == null)
                _body.Children.Add(Dim("Jersey variant 2 has no gear number (gear 2 is Away), so this kit cannot have a row of its own."));
            else
            {
                var reuse = _gear.UnusedRow(_team!, part);
                var own = new Button
                {
                    Content = "Give this kit its own row",
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 6, 0, 0),
                    ToolTip = reuse != null
                        ? $"Points unused row {reuse.Index} (variant {reuse.Variant}, worn by no current kit) at this kit, starting from the colours it shows now. Seen working in game."
                        : $"Adds a new row in the table's spare space ({_gear.SpareRows:N0} left), starting from the colours this kit shows now. Not yet seen in game: check it before sharing.",
                };
                own.Click += (_, _) => GiveOwnRow(kit, part);
                _body.Children.Add(own);
            }
        }
        if (resolution.Row != null)
            _body.Children.Add(RowEditor(resolution.Row, part));
        RestyleZones();
    }

    /// <summary>
    /// The main colour, with the team's colours one click away, and every zone under it. For the shown kit, the zones the
    /// model on screen has come first, and each can be selected to find it on the model.
    /// </summary>
    private FrameworkElement RowEditor(GearRow row, GearPart? part)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var main = ColourField("Main", row.Main, rgb => _gear!.SetMain(row, rgb));
        foreach (var colour in _team!.Colors.Where(c => c > 0).Distinct())
        {
            var chip = Swatch(colour, 16);
            chip.Margin = new Thickness(4, 0, 0, 0);
            chip.Cursor = Cursors.Hand;
            chip.ToolTip = $"Team colour #{GearColours.Hex(colour)}";
            chip.MouseLeftButtonUp += (_, _) => Apply(() => _gear!.SetMain(row, colour));
            main.Children.Add(chip);
        }
        panel.Children.Add(main);

        var accents = row.Zones.Select((c, i) => (Colour: c, Zone: i + 1)).Where(z => z.Colour != row.Main).ToList();
        panel.Children.Add(Dim(accents.Count == 0 ? "One colour in every zone." : "Accents: " + string.Join(", ", accents.Select(a => $"zone {a.Zone} #{GearColours.Hex(a.Colour)}")) + "."));

        var onModel = part is { } p && _modelZones.TryGetValue(p, out var present) ? present : null;
        if (onModel != null)
        {
            panel.Children.Add(new TextBlock { Text = "ZONES ON THIS MODEL", Style = (Style)FindResource("Section"), Margin = new Thickness(0, 10, 0, 2) });
            panel.Children.Add(Dim("Click a zone to find it on the model, or click the model to find its zone. What each zone is (logo, cuff, trim) changes between models."));
            foreach (var (zone, share) in onModel.OrderBy(z => z.Key))
                if (zone <= GearColours.ZoneCount)
                    panel.Children.Add(ZoneRow(row, part!.Value, zone, share));
        }
        var rest = new WrapPanel();
        for (var z = 1; z <= GearColours.ZoneCount; z++)
        {
            if (onModel?.ContainsKey(z) == true)
                continue;
            var zone = z;
            var field = ColourField($"Zone {zone}", row.Zones[zone - 1], rgb => _gear!.SetZone(row, zone, rgb));
            field.Width = 150;
            field.Margin = new Thickness(0, 0, 6, 4);
            rest.Children.Add(field);
        }
        panel.Children.Add(new Expander
        {
            Header = onModel == null ? "All zones" : $"Zones not on this model  {rest.Children.Count}",
            Content = new StackPanel { Children = { Dim(onModel == null ? "Which zone is which part (logo, cuff, trim) changes from one glove model to the next." : "Other glove or helmet models may use these."), rest } },
            Margin = new Thickness(0, 6, 0, 0),
        });
        return panel;
    }

    /// <summary>One zone the model has: hover shows it on the model, a click selects it there.</summary>
    private Border ZoneRow(GearRow row, GearPart part, int zone, double share)
    {
        var field = ColourField($"Zone {zone}", row.Zones[zone - 1], rgb => _gear!.SetZone(row, zone, rgb));
        field.Margin = new Thickness(0);
        field.Children.Add(new TextBlock { Text = share >= 0.01 ? $"{share:P0}" : "<1%", Style = (Style)FindResource("Dim"), FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "How much of the texture this zone covers" });
        var border = new Border
        {
            Child = field,
            Padding = new Thickness(4, 3, 4, 3),
            Margin = new Thickness(0, 1, 0, 1),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = $"Zone {zone}: click to find it on the model",
        };
        border.MouseEnter += (_, _) =>
        {
            if (_selected != (part, zone))
                border.Background = (Brush)FindResource("HoverBrush");
            ZoneHighlighted?.Invoke(part, zone);
        };
        border.MouseLeave += (_, _) =>
        {
            RestyleZones();
            ZoneHighlighted?.Invoke(_selected?.Part ?? part, _selected?.Zone ?? 0);
        };
        border.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindParent<TextBox>(source) != null)
                return;
            SelectZone(part, _selected == (part, zone) ? 0 : zone);
        };
        _zoneRows[(part, zone)] = border;
        return border;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var node = child; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is T found)
                return found;
        return null;
    }

    private StackPanel ColourField(string label, int rgb, Func<int, GearRow> write)
    {
        var field = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        field.Children.Add(new TextBlock { Text = label, Width = 50, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        field.Children.Add(Swatch(rgb, 20));
        var box = new TextBox { Text = GearColours.Hex(rgb), Width = 70, FontSize = 12, Margin = new Thickness(6, 0, 0, 0), ToolTip = "A colour as RRGGBB, e.g. 0072CE" };
        void Commit()
        {
            if (box.Text.Trim().TrimStart('#').Equals(GearColours.Hex(rgb), StringComparison.OrdinalIgnoreCase))
                return;
            if (!GearColours.TryParseHex(box.Text, out var value))
            {
                _status.Text = $"{box.Text} is not a colour. Use six hex digits, e.g. 0072CE.";
                box.Text = GearColours.Hex(rgb);
                return;
            }
            Apply(() => write(value));
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Commit();
        };
        field.Children.Add(box);
        return field;
    }

    private void Apply(Func<GearRow> edit)
    {
        try
        {
            edit();
            SetDirty(true);
            Edited?.Invoke();
            _status.Text = "";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            _status.Text = e.Message;
        }
        Dispatcher.BeginInvoke(ShowKit);
    }

    /// <summary>Sets the main colour of the row the shown kit reads for a part, as typing it would.</summary>
    public void SetKitMain(GearPart part, int rgb)
    {
        if (_gear != null && _team != null && GearColours.GearTeamOf(_team) is { } gearTeam && _kitBox.SelectedItem is GearKit kit
            && _gear.Resolve(gearTeam, kit, part).Row is { } row)
            Apply(() => _gear.SetMain(row, rgb));
    }

    public void GiveOwnRow(GearKit kit, GearPart part)
    {
        try
        {
            var (row, added) = _gear!.GiveOwnRow(_team!, kit, part);
            SetDirty(true);
            Edited?.Invoke();
            _status.Text = added
                ? $"Added row {row.Index} for {kit.Label} {part.ToString().ToLowerInvariant()}. New rows have not been seen in game yet, so check this kit before sharing the mod."
                : $"Row {row.Index} now belongs to {kit.Label} {part.ToString().ToLowerInvariant()}.";
        }
        catch (InvalidOperationException e)
        {
            _status.Text = e.Message;
        }
        ShowKit();
    }

    public void SaveToProject()
    {
        if (_gear == null || _team == null || _services.RequireProject() is not { } project)
            return;
        if (CurrentBlob() != _loadedBlob && !Controls.ConfirmDialog.Ask("The database changed",
                "The project's copy of the game database changed after these colours were opened (for example in the Database tab). Saving replaces that copy with this one.", "Save anyway"))
            return;
        try
        {
            project.Set(TeamCatalog.DatabasePath, _gear.Save(), $"{_team.Name} glove and helmet colours");
        }
        catch (InvalidDataException e)
        {
            _services.ReportError("The colours were not saved", e);
            return;
        }
        _loadedBlob = CurrentBlob();
        SetDirty(false);
        _status.Text = "Saved to the project. Build the project to try it in game.";
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        _save.IsEnabled = _discard.IsEnabled = dirty;
    }

    private TextBlock Dim(string text) => new() { Text = text, Style = (Style)FindResource("Dim"), TextWrapping = TextWrapping.Wrap };

    private Border Swatch(int rgb, double size) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(3),
        BorderThickness = new Thickness(1),
        BorderBrush = (Brush)FindResource("LineBrush"),
        Background = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
