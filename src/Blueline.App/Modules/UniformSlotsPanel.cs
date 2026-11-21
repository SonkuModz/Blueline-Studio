using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core;
using Blueline.Core.Teams;
using Blueline.Formats.Tdb;
using Blueline.Formats.Textures;
using Microsoft.Win32;

namespace Blueline.App.Modules;

/// <summary>
/// A team's uniform slots: the kits its jersey select menu lists, in menu order (docs/UNIFORM_SLOTS.md). Slots can be
/// relabelled, reordered, made the default, removed, and added on a free kit name with every file that name has written
/// from a jersey sheet and a slot to copy the rest from. Each change goes into the project at once as one undo step.
/// </summary>
public sealed class UniformSlotsPanel : UserControl
{
    private readonly AppServices _services;
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _count = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 6) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) };
    private readonly WrapPanel _grid = new();
    private readonly StackPanel _details = new();
    private readonly StackPanel _add = new();
    private TeamInfo? _team;
    private TeamCatalog? _catalog;
    private UniformSlots? _slots;
    private GearColours? _gear;
    private LocDictionary? _names;
    private int? _selectedRow;
    private bool _showing;
    private string? _loadedBlob;

    public event Action? CloseRequested;

    public UniformSlotsPanel(AppServices services)
    {
        _services = services;
        var close = new Button { Content = "Back to the art", Style = (Style)FindResource("Quiet"), FontSize = 12, Padding = new Thickness(6, 0, 6, 0) };
        close.Click += (_, _) => CloseRequested?.Invoke();
        var title = new DockPanel { Margin = new Thickness(2, 0, 0, 4) };
        DockPanel.SetDock(close, Dock.Right);
        title.Children.Add(close);
        title.Children.Add(_title);

        var warn = Dim("A roster save holds its own copy of these slots and wins in game. After changing slots, copy the project's edits to the roster save (Build tab).");
        warn.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        warn.Margin = new Thickness(2, 0, 0, 8);

        var top = new StackPanel();
        top.Children.Add(title);
        top.Children.Add(_count);
        top.Children.Add(warn);

        var body = new StackPanel();
        body.Children.Add(_grid);
        body.Children.Add(Card(_details));
        body.Children.Add(Card(_add));
        body.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = body });
        Content = root;

        services.Workspace.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (_showing && CurrentBlob() != _loadedBlob)
                Load();
        });
    }

    private Border Card(UIElement child) => new() { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12), Child = child };

    private TextBlock Dim(string text) => new() { Text = text, Style = (Style)FindResource("Dim"), TextWrapping = TextWrapping.Wrap };

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 8) };

    public void SetTeam(TeamInfo? team, TeamCatalog? catalog)
    {
        if (team?.Index != _team?.Index)
            _selectedRow = null;
        _team = team;
        _catalog = catalog;
        _title.Text = team == null ? "Uniform slots" : $"Uniform slots: {team.Name}";
        if (_showing)
            Load();
    }

    public void SetShowing(bool showing)
    {
        _showing = showing;
        if (showing)
            Load();
    }

    private string? CurrentBlob() =>
        _services.Workspace.Project?.TryGet(TeamCatalog.DatabasePath, out var change) == true ? change.Blob : null;

    private byte[]? ReadCurrent(string path) => _services.Workspace.ReadCurrent(path);

    private void Load()
    {
        _slots = null;
        _gear = null;
        _grid.Children.Clear();
        _details.Children.Clear();
        _add.Children.Clear();
        _count.Text = "";
        if (_team == null || _catalog == null || _services.Workspace.Vfs is not { } vfs)
            return;
        try
        {
            var file = TdbFile.Parse((byte[])ReadCurrent(TeamCatalog.DatabasePath)!.Clone());
            if (vfs.Find(TeamCatalog.MetaPath) is { } meta)
                file.ApplyMeta(Encoding.UTF8.GetString(vfs.ReadContent(meta)));
            var vault = vfs.Find(UniformSlots.VaultPath) is { } v ? vfs.ReadContent(v) : null;
            _slots = UniformSlots.Open(file, _catalog, vault);
            _gear = GearColours.Open(file);
            _names = ReadCurrent(SlotNames.DictionaryPath) is { } loc ? new LocDictionary(loc) : null;
            _loadedBlob = CurrentBlob();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or KeyNotFoundException)
        {
            _services.ReportError("The jersey table could not be read", e);
            return;
        }
        ShowSlots();
    }

    private void ShowSlots()
    {
        if (_slots == null || _team == null)
            return;
        var list = _slots.SlotsOf(_team);
        _count.Text = $"{list.Count} of {UniformSlots.MaxSlotsPerTeam} slots, in jersey menu order. {_slots.SpareRows} spare rows are left in the jersey table for all teams.";
        _count.Style = (Style)FindResource("Dim");
        _grid.Children.Clear();
        for (var i = 0; i < list.Count; i++)
            _grid.Children.Add(Tile(list[i], i + 1));
        var selected = list.FirstOrDefault(s => s.Row == _selectedRow) ?? list.FirstOrDefault();
        _selectedRow = selected?.Row;
        foreach (var tile in _grid.Children.OfType<Border>())
            tile.SetResourceReference(Border.BorderBrushProperty, tile.Tag is int row && row == _selectedRow ? "AccentBrush" : "LineBrush");
        ShowDetails(selected);
        ShowAdd(list);
    }

    private string? NameOf(UniformSlot slot) => _names == null ? null : SlotNames.NameOf(_names, slot);

    private ImageSource? MenuPicture(UniformSlot slot)
    {
        var path = UniformSlotArt.MenuPictures(slot.Style, slot.ArtId, slot.Variant)[0];
        try
        {
            return ReadCurrent(path) is { } data && GameTexture.TryOpen(data) is { Blocker: null } texture ? OverlayCanvas.ToBitmap(texture.DecodeMip(0)) : null;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private Border Tile(UniformSlot slot, int number)
    {
        var stack = new StackPanel();
        var picture = new Grid { Height = 150 };
        picture.Children.Add(new Image { Source = MenuPicture(slot), Stretch = Stretch.Uniform });
        picture.Children.Add(new TextBlock { Text = number.ToString(), Style = (Style)FindResource("Dim"), FontSize = 11, Margin = new Thickness(2) });
        stack.Children.Add(picture);
        var shown = NameOf(slot);
        stack.Children.Add(new TextBlock { Text = shown ?? slot.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = $"{(shown != null ? slot.Label + " · " : "")}{slot.BrandLabel} · {slot.Word ?? slot.Variant.ToString()}", Style = (Style)FindResource("Dim"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        void Tag(string text, string brush)
        {
            var tag = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
            tag.SetResourceReference(TextBlock.ForegroundProperty, brush);
            stack.Children.Add(tag);
        }
        if (slot.IsDefault)
            Tag(slot.Variant == 0 ? "Default home" : "Default away", "AccentBrush");
        if (slot.ArtId != _team!.ArtId)
            Tag($"Uses {slot.KitName ?? $"art {slot.ArtId}"}", "WarningBrush");
        if (!_slots!.IsDrawable(slot))
            Tag("Not drawn: no texture pack", "DangerBrush");
        var tile = new Border
        {
            Width = 140, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(6), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.5),
            Child = stack, Tag = slot.Row, Cursor = Cursors.Hand, ToolTip = slot.KitName,
        };
        tile.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");
        tile.MouseLeftButtonUp += (_, _) =>
        {
            _selectedRow = slot.Row;
            ShowSlots();
        };
        return tile;
    }

    private static Grid Row(string label, UIElement value)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
        grid.Children.Add(text);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private Button Action(string text, Action click, string? tip = null)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
        button.Click += (_, _) => click();
        return button;
    }

    private void ShowDetails(UniformSlot? slot)
    {
        _details.Children.Clear();
        if (slot == null || _slots == null || _team == null)
        {
            _details.Children.Add(Dim("This team has no slots."));
            return;
        }
        var list = _slots.SlotsOf(_team);
        var number = list.ToList().FindIndex(s => s.Row == slot.Row) + 1;
        _details.Children.Add(Heading($"Slot {number}: {slot.Label} ({slot.BrandLabel} · {slot.Word ?? slot.Variant.ToString()})"));

        var label = new ComboBox { ItemsSource = UniformSlots.Labels, SelectedIndex = slot.LocString < UniformSlots.Labels.Count ? slot.LocString : -1, Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        label.SelectionChanged += (_, _) =>
        {
            if (label.SelectedIndex >= 0 && label.SelectedIndex != slot.LocString)
                Change($"{_team.Name} slot label", s => s.Set(slot, locString: label.SelectedIndex));
        };
        _details.Children.Add(Row("Menu label", label));

        var nameBox = new TextBox { Text = NameOf(slot) ?? "", Width = 260, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"The kit's own name in the jersey select, shown in place of the label (key {SlotNames.KeyOf(slot)} in the English text)" };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(nameBox);
        var saveName = Action("Save name", () => SaveName(slot, nameBox.Text));
        saveName.Margin = new Thickness(6, 0, 0, 0);
        nameRow.Children.Add(saveName);
        _details.Children.Add(Row("Name", nameRow));

        var isDefault = new CheckBox { Content = slot.Variant == 0 ? "Default home kit" : slot.Variant == 1 ? "Default away kit" : "Default kit", IsChecked = slot.IsDefault, VerticalAlignment = VerticalAlignment.Center };
        isDefault.Click += (_, _) => Change($"{_team.Name} default kit", s => s.Set(slot, isDefault: isDefault.IsChecked == true));
        _details.Children.Add(Row("Default", isDefault));

        var light = new CheckBox { Content = "Light kit (the game pairs it against dark ones)", IsChecked = slot.IsLight, VerticalAlignment = VerticalAlignment.Center };
        light.Click += (_, _) => Change($"{_team.Name} light kit", s => s.Set(slot, light: light.IsChecked == true));
        _details.Children.Add(Row("Light", light));

        _details.Children.Add(Row("Wears files of", new TextBlock
        {
            Text = slot.KitName == null ? $"art {slot.ArtId}, variant {slot.Variant}: no known file name" : slot.KitName + (slot.ArtId == _team.ArtId ? " (own)" : " (borrowed)"),
            TextWrapping = TextWrapping.Wrap,
        }));

        if (slot.Style == GearColours.AdidasStyle && _gear != null && GearColours.GearTeamOf(_team) is { } gearTeam
            && _gear.KitsOf(_team).FirstOrDefault(k => k.Variant == slot.Variant) is { } kit)
        {
            var gloves = _gear.Resolve(gearTeam, kit, GearPart.Gloves);
            var helmet = _gear.Resolve(gearTeam, kit, GearPart.Helmet);
            string Say(GearResolution r) => r.Row == null ? "none" : $"row {r.Row.Index}{(r.Link == GearLink.Own ? "" : " (shared)")}";
            _details.Children.Add(Row("Gear rows", new TextBlock { Text = $"Gloves {Say(gloves)} · helmet {Say(helmet)} (gear variant {kit.GearVariant}). Colours are edited in the Uniform studio.", TextWrapping = TextWrapping.Wrap }));
        }

        if (slot.KitName is { } name)
        {
            var parts = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
            foreach (var (part, maps) in UniformSlotArt.Parts)
            {
                var have = maps.Count(m => ReadCurrentExists(UniformSlotArt.PlayerFile(part, name, m)));
                var edited = maps.Count(m => _services.Workspace.IsEdited(UniformSlotArt.PlayerFile(part, name, m)));
                var chip = new TextBlock { Text = $"{PartLabel(part)} {have}/{maps.Length}{(edited > 0 ? " edited" : "")}", Margin = new Thickness(0, 0, 12, 2), FontSize = 12 };
                chip.SetResourceReference(TextBlock.ForegroundProperty, have == maps.Length ? "TextBrush" : "WarningBrush");
                parts.Children.Add(chip);
            }
            _details.Children.Add(Row("Files", parts));
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var earlier = Action("Earlier in menu", () => Change($"{_team.Name} slot order", s => s.Move(slot, number - 2)));
        earlier.IsEnabled = number > 1;
        var later = Action("Later in menu", () => Change($"{_team.Name} slot order", s => s.Move(slot, number)));
        later.IsEnabled = number < list.Count;
        var replace = Action("Replace jersey sheet…", () => ReplaceArt(slot), "Writes a new jersey sheet into this slot's kit files, with normal and coeff maps made from it");
        replace.IsEnabled = slot.KitName != null;
        buttons.Children.Add(earlier);
        buttons.Children.Add(later);
        buttons.Children.Add(replace);
        buttons.Children.Add(Action("Remove slot", () => Remove(slot)));
        _details.Children.Add(buttons);
    }

    private bool ReadCurrentExists(string path) => _services.Workspace.Vfs?.Find(path) != null || _services.Workspace.IsEdited(path);

    private static string PartLabel(string part) => part switch
    {
        "jersey" => "Jersey", "pant" => "Pants", "sock" => "Socks", "font" => "Numbers", "name" => "Name bar", "helmetlogo" => "Helmet logo", _ => part,
    };

    /// <summary>Applies one change to the jersey table and puts the database into the project as one undo step.</summary>
    private void Change(string description, Func<UniformSlots, object?> edit)
    {
        if (_slots == null || _services.RequireProject() is not { } project)
            return;
        if (CurrentBlob() != _loadedBlob)
        {
            Load();
            _status.Text = "The project's database changed elsewhere, so the slots were read again. Try once more.";
            return;
        }
        try
        {
            if (edit(_slots) is UniformSlot changed)
                _selectedRow = changed.Row;
            project.Set(TeamCatalog.DatabasePath, _slots.Save(), description);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or InvalidDataException)
        {
            _services.ReportError("The slot was not changed", e);
            Load();
            return;
        }
        _loadedBlob = CurrentBlob();
        _status.Text = $"{description}: saved to the project. Build the project to try it in game.";
        ShowSlots();
    }

    /// <summary>Writes the kit's own name into the English text table as one undo step.</summary>
    private void SaveName(UniformSlot slot, string text)
    {
        if (_services.RequireProject() is not { } project || _team == null)
            return;
        try
        {
            var names = new LocDictionary(ReadCurrent(SlotNames.DictionaryPath) ?? throw new FileNotFoundException("The game has no English text table."));
            var added = SlotNames.SetName(names, slot.Style, slot.ArtId, slot.Variant, text);
            project.Set(SlotNames.DictionaryPath, names.Save(), $"{_team.Name} kit name {text.Trim()}");
            _names = names;
            _status.Text = $"Named {slot.KitName ?? slot.Label} \"{text.Trim()}\" ({(added ? "new key" : "changed")} {SlotNames.KeyOf(slot)}). Build the project to try it in game.";
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or InvalidDataException or FileNotFoundException)
        {
            _services.ReportError("The name was not saved", e);
            return;
        }
        ShowSlots();
    }

    private void Remove(UniformSlot slot)
    {
        if (!ConfirmDialog.Ask("Remove slot", $"Remove {slot} from {_team!.Name}'s jersey menu? Its files stay in the game, and the kit name becomes free for a new slot. Undo puts it back.", "Remove"))
            return;
        _selectedRow = null;
        Change($"Remove {_team.Name} slot {slot.KitName ?? slot.Label}", s =>
        {
            s.Remove(slot);
            return null;
        });
    }

    private static string? ChooseImage(string title)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = ImageFiles.OpenFilter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void ReplaceArt(UniformSlot slot)
    {
        if (ChooseImage($"Choose a {slot.BrandLabel} jersey sheet for {slot.KitName}") is not { } file)
            return;
        var kit = new FreeKit(slot.Style, slot.Slug!, slot.ArtId, slot.Variant, FreeKitSource.OwnUnused);
        _ = WriteArt(kit, new SlotArtOptions(ImageFiles.Load(file)), null, $"{_team!.Name} {slot.KitName} jersey sheet");
    }

    private void ShowAdd(IReadOnlyList<UniformSlot> list)
    {
        _add.Children.Clear();
        if (_slots == null || _team == null)
            return;
        _add.Children.Add(Heading("Add a slot"));
        _add.Children.Add(Dim("The game only draws kits whose name its vault already lists, so a new slot takes a free kit name and writes your art into every file that name has. " +
            "Anything not given is copied from the slot chosen below, so no part keeps the old owner's look."));
        if (list.Count >= UniformSlots.MaxSlotsPerTeam)
        {
            _add.Children.Add(Dim($"{_team.Name} has {UniformSlots.MaxSlotsPerTeam} slots, the most the table can number."));
            return;
        }
        var free = _slots.FreeKits(_team);
        var kits = new ComboBox { ItemsSource = free, SelectedIndex = free.Count > 0 ? 0 : -1, Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        var copy = new ComboBox { Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        void FillCopy()
        {
            var kit = kits.SelectedItem as FreeKit;
            var same = list.Where(s => kit != null && s.Style == kit.Style && s.KitName != null).ToList();
            copy.ItemsSource = same;
            copy.SelectedIndex = same.Count > 0 ? 0 : -1;
        }
        kits.SelectionChanged += (_, _) => FillCopy();
        FillCopy();
        string? jersey = null, menu = null;
        var jerseyText = new TextBlock { Text = "none: the jersey is copied too", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Opacity = 0.75 };
        var menuText = new TextBlock { Text = "none: copied from the slot above", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Opacity = 0.75 };
        var jerseyRow = new StackPanel { Orientation = Orientation.Horizontal };
        jerseyRow.Children.Add(Action("Choose…", () =>
        {
            jersey = ChooseImage("Choose a jersey sheet drawn for the kit's brand") ?? jersey;
            jerseyText.Text = jersey == null ? jerseyText.Text : Path.GetFileName(jersey);
        }));
        jerseyRow.Children.Add(jerseyText);
        var menuRow = new StackPanel { Orientation = Orientation.Horizontal };
        menuRow.Children.Add(Action("Choose…", () =>
        {
            menu = ChooseImage("Choose a menu picture of the jersey") ?? menu;
            menuText.Text = menu == null ? menuText.Text : Path.GetFileName(menu);
        }));
        menuRow.Children.Add(menuText);
        var label = new ComboBox { ItemsSource = UniformSlots.Labels, SelectedIndex = 6, Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        var position = new ComboBox { ItemsSource = Enumerable.Range(1, list.Count + 1).ToList(), SelectedIndex = Math.Min(2, list.Count), Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        var name = new TextBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Optional: the kit's own name in the jersey select, shown in place of the label" };

        _add.Children.Add(new Border { Height = 6 });
        _add.Children.Add(Row("Kit name", kits));
        _add.Children.Add(Row("Copy the rest from", copy));
        _add.Children.Add(Row("Jersey sheet", jerseyRow));
        _add.Children.Add(Row("Menu picture", menuRow));
        _add.Children.Add(Row("Menu label", label));
        _add.Children.Add(Row("Name (optional)", name));
        _add.Children.Add(Row("Menu position", position));
        var add = Action("Add slot", () =>
        {
            if (kits.SelectedItem is not FreeKit kit)
                return;
            _ = AddSlot(kit, copy.SelectedItem as UniformSlot, jersey, menu, label.SelectedIndex, position.SelectedIndex, name.Text);
        });
        add.Style = (Style)FindResource("Primary");
        add.Margin = new Thickness(0, 8, 0, 0);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.IsEnabled = free.Count > 0 && _slots.SpareRows > 0;
        _add.Children.Add(add);
    }

    /// <summary>Free kit names for the team, best first (empty until the panel has loaded).</summary>
    public IReadOnlyList<FreeKit> FreeKits => _slots != null && _team != null ? _slots.FreeKits(_team) : [];

    public IReadOnlyList<UniformSlot> Slots => _slots != null && _team != null ? _slots.SlotsOf(_team) : [];

    public Task AddSlot(FreeKit kit, UniformSlot? copy, string? jersey, string? menu, int label = 6, int position = 2, string? name = null)
    {
        if (_slots == null || _team == null || _services.RequireProject() is null)
            return Task.CompletedTask;
        if (copy == null && jersey == null)
        {
            _services.ReportError("Nothing to draw", $"Choose a jersey sheet or a slot to copy from; otherwise {kit.Name} keeps its old owner's look.");
            return Task.CompletedTask;
        }
        var options = new SlotArtOptions(jersey == null ? null : ImageFiles.Load(jersey), copy, menu == null ? null : ImageFiles.Load(menu));
        return WriteArt(kit, options, (label < 0 ? 6 : label, position, string.IsNullOrWhiteSpace(name) ? null : name.Trim()), $"Add {_team.Name} uniform slot {kit.Name}");
    }

    /// <summary>Makes the kit's files in the background, then puts them (and the new row, when adding) into the project as one undo step.</summary>
    private Task WriteArt(FreeKit kit, SlotArtOptions options, (int Label, int Position, string? Name)? add, string description)
    {
        if (_services.Workspace.Vfs is not { } vfs || _services.RequireProject() is not { } project || _team is not { } team)
            return Task.CompletedTask;
        var read = (Func<string, byte[]?>)ReadCurrent;
        return _services.Jobs.RunAsync($"Writing {kit.Name}", _ => UniformSlotArt.Make(vfs, kit, options, read), files =>
        {
            if (_slots == null || CurrentBlob() != _loadedBlob)
            {
                Load();
                _status.Text = "The project's database changed while the files were made, so nothing was written. Try once more.";
                return;
            }
            var items = files.Where(f => f.Content != null).Select(f => (f.Path, f.Content!, $"{team.Name} {kit.Name}")).ToList();
            try
            {
                if (add is { } a)
                {
                    var slot = _slots.Add(team, kit, a.Label, a.Position, _gear);
                    _selectedRow = slot.Row;
                    items.Add((TeamCatalog.DatabasePath, _slots.Save(), description));
                    if (a.Name != null && ReadCurrent(SlotNames.DictionaryPath) is { } loc)
                    {
                        var names = new LocDictionary(loc);
                        SlotNames.SetName(names, kit.Style, kit.ArtId, kit.Variant, a.Name);
                        items.Add((SlotNames.DictionaryPath, names.Save(), $"{team.Name} kit name {a.Name}"));
                        _names = names;
                    }
                }
                if (items.Count == 0)
                {
                    _status.Text = "Nothing was written: " + string.Join("; ", files.Select(f => f.Note).Distinct());
                    return;
                }
                project.SetMany(items, description);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or InvalidDataException)
            {
                _services.ReportError("The slot was not added", e);
                Load();
                return;
            }
            _loadedBlob = CurrentBlob();
            var skipped = files.Where(f => f.Content == null).ToList();
            _status.Text = $"{description}: {items.Count} files into the project." + (skipped.Count > 0 ? " Not written: " + string.Join("; ", skipped.Select(f => $"{Path.GetFileName(f.Path)} ({f.Note})")) + "." : "")
                + " Build the project to try it in game.";
            ShowSlots();
        });
    }
}
