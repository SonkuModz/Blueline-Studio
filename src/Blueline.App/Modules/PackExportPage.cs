using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core;
using Blueline.Core.Packs;
using Blueline.Formats.Textures;
using Microsoft.Win32;
using static Blueline.App.Modules.PackVisuals;

namespace Blueline.App.Modules;

/// <summary>
/// Build › Export mod pack: details, art for the player's Mods page (cover, icon, screenshots, from a file or from the project's own
/// pictures), an optional roster save, and a live preview of how the pack will look. Writes one .blpack; the game is only read.
/// </summary>
public sealed class PackExportPage : UserControl
{
    private const string ArtFolder = "pack-art";

    private readonly AppServices _services;
    private readonly ModProject _project;
    private readonly Action _back;
    private readonly TextBox _name = new();
    private readonly TextBox _author = new();
    private readonly TextBox _version = new();
    private readonly TextBox _description = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 52 };
    private readonly CheckBox _includeRoster = new() { Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly ComboBox _rosterSlot = new() { MinWidth = 280, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Grid _coverSlot = new();
    private readonly Grid _iconSlot = new();
    private readonly WrapPanel _shotSlots = new();
    private readonly StackPanel _picker = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel _preview = new();
    private readonly StackPanel _contents = new();
    private readonly Button _export = new() { Content = "Export .blpack…" };
    private readonly TextBlock _footText = Dim("");

    private PackPicture? _cover;
    private PackPicture? _icon;
    private readonly List<PackPicture> _shots = [];
    private PackExportPlan? _plan;
    private List<(string Path, string Group, RgbaImage Image)>? _projectPictures;
    private string _pickerGroup = "";

    public PackExportPage(AppServices services, ModProject project, Action back)
    {
        _services = services;
        _project = project;
        _back = back;
        var info = PackExporter.LoadInfo(project);
        _name.Text = project.Name;
        _author.Text = project.Author;
        _version.Text = info?.Version is { } v ? NextVersion(v) : "1.0.0";
        _description.Text = info?.Description ?? project.Notes;
        LoadSavedArt();

        foreach (var box in new[] { _name, _author, _version, _description })
            box.TextChanged += (_, _) => RefreshPreview();
        _includeRoster.Click += (_, _) => { _rosterSlot.IsEnabled = _includeRoster.IsChecked == true; RefreshContents(); };
        _rosterSlot.SelectionChanged += (_, _) => RefreshPreview();
        _export.Style = (Style)FindResource("Primary");
        _export.Click += (_, _) => _ = ExportAsync();

        // Header
        var back2 = new Button { Content = "← Back to Build", Style = (Style)FindResource("Quiet") };
        back2.Click += (_, _) => _back();
        var header = new DockPanel { Margin = new Thickness(14, 12, 14, 10) };
        DockPanel.SetDock(back2, Dock.Right);
        header.Children.Add(back2);
        header.Children.Add(new TextBlock { Text = "Export mod pack", Style = (Style)FindResource("Heading"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center });

        // Left: details, art, roster
        var left = new StackPanel { Margin = new Thickness(18, 14, 18, 18) };
        left.Children.Add(Label("Details", 0));
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddField(row, 0, "Name", _name);
        AddField(row, 1, "Author", _author);
        AddField(row, 2, "Version", _version);
        left.Children.Add(row);
        left.Children.Add(Field("Description", _description));

        left.Children.Add(Label("Art · shown on the player's Mods page"));
        var art = new Grid { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        art.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
        art.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        art.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var coverBox = new StackPanel();
        coverBox.Children.Add(_coverSlot);
        coverBox.Children.Add(Dim("Cover · 1280×720 · drag the picture to move the crop", 11).Also(t => t.Margin = new Thickness(0, 4, 0, 0)));
        var iconBox = new StackPanel();
        iconBox.Children.Add(_iconSlot);
        iconBox.Children.Add(Dim("Icon · 256×256", 11).Also(t => t.Margin = new Thickness(0, 4, 0, 0)));
        Grid.SetColumn(iconBox, 2);
        art.Children.Add(coverBox);
        art.Children.Add(iconBox);
        left.Children.Add(art);
        left.Children.Add(Dim($"Screenshots · up to {ModPack.MaxShots}", 12).Also(t => t.Margin = new Thickness(0, 10, 0, 4)));
        left.Children.Add(_shotSlots);
        var artButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var fromProject = new Button { Content = "Pick from project", Margin = new Thickness(0, 0, 6, 0) };
        fromProject.Click += (_, _) => _ = TogglePicker();
        var paste = new Button { Content = "Paste as cover", Style = (Style)FindResource("Quiet") };
        paste.Click += (_, _) => PasteCover();
        artButtons.Children.Add(fromProject);
        artButtons.Children.Add(paste);
        left.Children.Add(artButtons);
        left.Children.Add(_picker);

        left.Children.Add(Label("Custom roster"));
        var rosterText = new StackPanel();
        rosterText.Children.Add(new TextBlock { Text = "Include a roster save", FontWeight = FontWeights.SemiBold });
        rosterText.Children.Add(_rosterSlot);
        rosterText.Children.Add(Dim("On the player's game it goes into a free custom slot, never over their RosterCustom0. The account id in the save is blanked.").Also(t => t.Margin = new Thickness(0, 6, 0, 0)));
        var rosterRow = new DockPanel();
        DockPanel.SetDock(_includeRoster, Dock.Left);
        rosterRow.Children.Add(_includeRoster);
        rosterRow.Children.Add(rosterText);
        left.Children.Add(Card(rosterRow));
        FillRosterSlots();

        // Right: preview and contents
        var right = new StackPanel { Margin = new Thickness(18, 14, 18, 18) };
        right.Children.Add(Label("Preview · player's Mods page", 0));
        right.Children.Add(new Border { Child = _preview, Padding = new Thickness(12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) }
            .Brush(Border.BackgroundProperty, "WindowBrush").Brush(Border.BorderBrushProperty, "LineBrush"));
        right.Children.Add(Label("Contents"));
        right.Children.Add(_contents);
        var rightBorder = new Border { Child = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Width = 440, BorderThickness = new Thickness(1, 0, 0, 0) }
            .Brush(Border.BackgroundProperty, "PanelBrush").Brush(Border.BorderBrushProperty, "LineBrush");

        var foot = new DockPanel { Margin = new Thickness(18, 10, 18, 10) };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("Quiet"), Margin = new Thickness(0, 0, 6, 0) };
        cancel.Click += (_, _) => _back();
        var footButtons = new StackPanel { Orientation = Orientation.Horizontal };
        footButtons.Children.Add(cancel);
        footButtons.Children.Add(_export);
        DockPanel.SetDock(footButtons, Dock.Right);
        foot.Children.Add(footButtons);
        _footText.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(_footText);

        var body = new DockPanel();
        DockPanel.SetDock(rightBorder, Dock.Right);
        body.Children.Add(rightBorder);
        body.Children.Add(new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var root = new DockPanel();
        var headerBorder = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) }.Brush(Border.BorderBrushProperty, "LineBrush");
        var footBorder = new Border { Child = foot, BorderThickness = new Thickness(0, 1, 0, 0) }.Brush(Border.BorderBrushProperty, "LineBrush");
        DockPanel.SetDock(headerBorder, Dock.Top);
        DockPanel.SetDock(footBorder, Dock.Bottom);
        root.Children.Add(headerBorder);
        root.Children.Add(footBorder);
        root.Children.Add(body);
        Content = root;

        RefreshArt();
        RefreshPreview();
        _ = PlanAsync();
    }

    private static string NextVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length == 3 && int.TryParse(parts[2], out var patch) ? $"{parts[0]}.{parts[1]}.{patch + 1}" : version;
    }

    private static StackPanel Field(string label, Control box)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 10, 10) };
        panel.Children.Add(Dim(label).Also(t => t.Margin = new Thickness(0, 0, 0, 4)));
        panel.Children.Add(box);
        return panel;
    }

    private static void AddField(Grid grid, int column, string label, Control box)
    {
        var field = Field(label, box);
        Grid.SetColumn(field, column);
        grid.Children.Add(field);
    }

    private string? SaveData() =>
        _services.Settings.EmulatorPath is { Length: > 0 } path && File.Exists(path)
            ? Core.Teams.RosterSync.SaveDataFolder(new Core.Emulator.ShadInstall(path).UserFolder) : null;

    private void FillRosterSlots()
    {
        _rosterSlot.Items.Clear();
        var saveData = SaveData();
        var slots = saveData == null ? [] : RosterSlots.List(saveData);
        foreach (var slot in slots)
            _rosterSlot.Items.Add(new ComboBoxItem { Content = $"{slot.Name} · “{slot.Title}”  (saved {Saved(slot.Folder):yyyy-MM-dd HH:mm})", Tag = slot.Folder });
        var hasDatabase = _project.TryGet(Core.Teams.TeamCatalog.DatabasePath, out _);
        // The most recently saved roster by default: that is the one Blueline's roster sync and the game last wrote.
        var list = slots.ToList();
        _rosterSlot.SelectedIndex = list.Count == 0 ? -1 : list.IndexOf(list.MaxBy(s => Saved(s.Folder))!);
        _includeRoster.IsEnabled = slots.Count > 0;
        _includeRoster.IsChecked = slots.Count > 0 && hasDatabase;
        _rosterSlot.IsEnabled = _includeRoster.IsChecked == true;
        if (saveData == null)
            _rosterSlot.Items.Add(new ComboBoxItem { Content = "Choose shadPS4 in Settings to list the roster saves", IsEnabled = false });
    }

    private static DateTime Saved(string folder) =>
        File.Exists(Path.Combine(folder, "DATA")) ? File.GetLastWriteTime(Path.Combine(folder, "DATA")) : DateTime.MinValue;

    private string? RosterFolder => _includeRoster.IsChecked == true ? (_rosterSlot.SelectedItem as ComboBoxItem)?.Tag as string : null;

    // ---- art

    private void LoadSavedArt()
    {
        var folder = Path.Combine(_project.Folder, ArtFolder);
        PackPicture? Read(string name) => File.Exists(Path.Combine(folder, name)) ? new PackPicture(File.ReadAllBytes(Path.Combine(folder, name))) : null;
        _cover = Read("cover.png");
        _icon = Read("icon.png");
        for (var i = 1; i <= ModPack.MaxShots; i++)
            if (Read($"shot{i}.png") is { } shot)
                _shots.Add(shot);
    }

    private void SaveArt()
    {
        var folder = Path.Combine(_project.Folder, ArtFolder);
        if (Directory.Exists(folder))
            foreach (var file in Directory.GetFiles(folder, "*.png"))
                File.Delete(file);
        if (_cover == null && _icon == null && _shots.Count == 0)
            return;
        Directory.CreateDirectory(folder);
        if (_cover != null) File.WriteAllBytes(Path.Combine(folder, "cover.png"), PackExporter.Fit(_cover, ModPack.CoverSize.Width, ModPack.CoverSize.Height));
        if (_icon != null) File.WriteAllBytes(Path.Combine(folder, "icon.png"), PackExporter.Fit(_icon, ModPack.IconSize.Width, ModPack.IconSize.Height));
        for (var i = 0; i < _shots.Count; i++)
            File.WriteAllBytes(Path.Combine(folder, $"shot{i + 1}.png"), PackExporter.Fit(_shots[i], ModPack.ShotSize.Width, ModPack.ShotSize.Height));
    }

    private static PackPicture? FromFile()
    {
        var dialog = new OpenFileDialog { Filter = ImageFiles.OpenFilter, Title = "Choose a picture" };
        return dialog.ShowDialog() == true ? new PackPicture(PngFile.Write(ImageFiles.Load(dialog.FileName))) : null;
    }

    private byte[] Fitted(PackPicture picture, (int Width, int Height) size, double scale)
    {
        var width = Math.Max(1, (int)(size.Width * scale));
        var height = Math.Max(1, (int)(size.Height * scale));
        return PackExporter.Fit(picture, width, height);
    }

    private void RefreshArt()
    {
        Slot(_coverSlot, _cover, 16.0 / 9, "+ Cover", p => _cover = p, () => _cover, ModPack.CoverSize, draggable: true);
        Slot(_iconSlot, _icon, 1, "+ Icon", p => _icon = p, () => _icon, ModPack.IconSize, draggable: true);
        _shotSlots.Children.Clear();
        for (var i = 0; i < _shots.Count; i++)
        {
            var index = i;
            var grid = new Grid { Width = 150, Margin = new Thickness(0, 0, 8, 8) };
            Slot(grid, _shots[i], 16.0 / 9, "", p => { if (p == null) _shots.RemoveAt(index); else _shots[index] = p; }, () => _shots[index], ModPack.ShotSize, draggable: false);
            _shotSlots.Children.Add(grid);
        }
        if (_shots.Count < ModPack.MaxShots)
        {
            var add = new Grid { Width = 150, Margin = new Thickness(0, 0, 8, 8) };
            Slot(add, null, 16.0 / 9, "+ Add picture", p => { if (p != null) _shots.Add(p); }, () => null, ModPack.ShotSize, draggable: false);
            _shotSlots.Children.Add(add);
        }
        RefreshPreview();
    }

    /// <summary>One art slot: empty shows a dashed box to click; filled shows the crop it will use, a remove button, and drag to recentre.</summary>
    private void Slot(Grid host, PackPicture? picture, double aspect, string emptyText, Action<PackPicture?> set, Func<PackPicture?> get, (int Width, int Height) size, bool draggable)
    {
        host.Children.Clear();
        var box = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true, Cursor = Cursors.Hand };
        box.Brush(Border.BorderBrushProperty, picture == null ? "LineBrush" : "AccentBrush");
        box.SizeChanged += (_, e) => box.Height = e.NewSize.Width / aspect;
        host.Children.Add(box);

        if (picture == null)
        {
            box.BorderBrush = null;
            box.Brush(Border.BorderBrushProperty, "LineBrush");
            box.Child = new TextBlock { Text = emptyText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Brush(TextBlock.ForegroundProperty, "DimBrush");
            box.Background = System.Windows.Media.Brushes.Transparent;
            box.MouseLeftButtonUp += (_, _) => { if (FromFile() is { } chosen) { set(chosen); RefreshArt(); } };
            return;
        }

        box.Background = new System.Windows.Media.ImageBrush(Bitmap(Fitted(picture, size, 0.4))) { Stretch = System.Windows.Media.Stretch.UniformToFill };
        var remove = new Button { Content = "✕", Style = (Style)FindResource("Quiet"), FontSize = 10, Padding = new Thickness(5, 1, 5, 1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4), ToolTip = "Remove" };
        remove.Click += (_, _) => { set(null); RefreshArt(); };
        host.Children.Add(remove);
        box.ToolTip = draggable ? "Drag to move the crop. Double-click to choose another picture." : "Double-click to choose another picture.";
        box.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && FromFile() is { } chosen)
            {
                set(chosen);
                RefreshArt();
            }
        };
        if (!draggable)
            return;
        Point? start = null;
        (double X, double Y) focus = (picture.FocusX, picture.FocusY);
        box.MouseLeftButtonDown += (_, e) => { start = e.GetPosition(box); box.CaptureMouse(); };
        box.MouseLeftButtonUp += (_, e) =>
        {
            if (start is not { } from)
                return;
            box.ReleaseMouseCapture();
            var to = e.GetPosition(box);
            start = null;
            if (Math.Abs(to.X - from.X) + Math.Abs(to.Y - from.Y) < 3 || get() is not { } current)
                return;
            // Dragging the picture right shows more of its left side.
            var moved = current with
            {
                FocusX = Math.Clamp(focus.X - (to.X - from.X) / Math.Max(1, box.ActualWidth), 0, 1),
                FocusY = Math.Clamp(focus.Y - (to.Y - from.Y) / Math.Max(1, box.ActualHeight), 0, 1),
            };
            set(moved);
            RefreshArt();
        };
    }

    private void PasteCover()
    {
        if (!Clipboard.ContainsImage())
        {
            _services.Notify("The clipboard holds no picture.");
            return;
        }
        var source = Clipboard.GetImage();
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        _cover = new PackPicture(PngFile.Write(ImageFiles.Decode(stream.ToArray())));
        RefreshArt();
    }

    /// <summary>For the screenshot tour: sets a cover and icon without a file dialog.</summary>
    internal void TourArt(PackPicture picture)
    {
        _cover = picture;
        _icon = picture with { FocusX = 0.3 };
        RefreshArt();
    }

    internal Task ShowProjectPictures() => _picker.Visibility == Visibility.Visible ? Task.CompletedTask : TogglePicker();

    internal void Back() => _back();

    private async Task TogglePicker()
    {
        if (_picker.Visibility == Visibility.Visible)
        {
            _picker.Visibility = Visibility.Collapsed;
            return;
        }
        if (_projectPictures == null)
        {
            var changes = _project.Changes.Where(c => Path.GetExtension(c.Path).ToLowerInvariant() is ".gnf" or ".dds" or ".png" or ".jpg").ToList();
            await _services.Jobs.RunAsync("Reading the project's pictures", context =>
            {
                var found = new List<(string, string, RgbaImage)>();
                for (var i = 0; i < changes.Count && found.Count < 240; i++)
                {
                    context.Progress("Reading the project's pictures", i + 1, changes.Count);
                    try
                    {
                        if (ImageFiles.DecodeAny(_project.ReadBlob(changes[i])) is { Width: >= 64, Height: >= 64 } image)
                            found.Add((changes[i].Path, PackSummary.GroupOf(changes[i].Path), image));
                    }
                    catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException or IOException)
                    {
                    }
                }
                return found;
            }, found => _projectPictures = found);
            if (_projectPictures == null)
                return;
        }
        _pickerGroup = _projectPictures.Select(p => p.Group).FirstOrDefault() ?? "";
        _picker.Visibility = Visibility.Visible;
        FillPicker();
    }

    private void FillPicker()
    {
        _picker.Children.Clear();
        if (_projectPictures is not { Count: > 0 } pictures)
        {
            _picker.Children.Add(Card(Dim("This project changes no pictures.")));
            return;
        }
        var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var group in pictures.Select(p => p.Group).Distinct())
        {
            var pill = new ToggleButton { Content = group, IsChecked = group == _pickerGroup, Style = (Style)FindResource("Pill"), Margin = new Thickness(0, 0, 6, 0) };
            pill.Click += (_, _) => { _pickerGroup = group; FillPicker(); };
            tabs.Children.Add(pill);
        }
        var grid = new UniformGrid { Columns = 6 };
        foreach (var (path, _, image) in pictures.Where(p => p.Group == _pickerGroup).Take(60))
        {
            var png = PngFile.Write(image.Width > 512 ? TexturePipeline.Resize(image, 512, Math.Max(1, image.Height * 512 / image.Width)) : image);
            var tile = new Border { Margin = new Thickness(0, 0, 6, 6), Height = 64, CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, ToolTip = path,
                Background = new System.Windows.Media.ImageBrush(Bitmap(png)) { Stretch = System.Windows.Media.Stretch.UniformToFill } };
            var menu = new ContextMenu();
            void Use(string label, Action<PackPicture> apply)
            {
                var item = new MenuItem { Header = label };
                item.Click += (_, _) => { apply(new PackPicture(PngFile.Write(image))); RefreshArt(); };
                menu.Items.Add(item);
            }
            Use("Use as cover", p => _cover = p);
            Use("Use as icon", p => _icon = p);
            if (_shots.Count < ModPack.MaxShots)
                Use("Add as screenshot", p => _shots.Add(p));
            tile.ContextMenu = menu;
            tile.MouseLeftButtonUp += (_, _) => { menu.PlacementTarget = tile; menu.IsOpen = true; };
            grid.Children.Add(tile);
        }
        var box = new StackPanel();
        box.Children.Add(tabs);
        box.Children.Add(grid);
        box.Children.Add(Dim("Pictures this project changes. Click one, then choose cover, icon or screenshot.", 11));
        _picker.Children.Add(Card(box));
    }

    // ---- preview and contents

    private void RefreshPreview()
    {
        if (_preview == null)
            return;
        _preview.Children.Clear();
        var name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : _project.Name;
        var files = _plan?.Files.Count ?? _project.ChangeCount;

        var tile = new DockPanel();
        var icon = Icon(_icon == null ? null : Fitted(_icon, ModPack.IconSize, 0.25), name, 36);
        icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
        text.Children.Add(Dim($"v{_version.Text.Trim()} · {files:N0} file{S(files)}{(RosterFolder != null ? " · roster" : "")}"));
        var check = new CheckBox { IsChecked = true, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(check, Dock.Right);
        tile.Children.Add(icon);
        tile.Children.Add(check);
        tile.Children.Add(text);
        _preview.Children.Add(new Border { Child = tile, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) }
            .Brush(Border.BackgroundProperty, "AccentSoftBrush").Brush(Border.BorderBrushProperty, "AccentBrush"));

        _preview.Children.Add(Cover(_cover == null ? null : Fitted(_cover, ModPack.CoverSize, 0.4), name, 210));
        if (_shots.Count > 0)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var shot in _shots)
                strip.Children.Add(Shot(Fitted(shot, ModPack.ShotSize, 0.1), 40));
            _preview.Children.Add(strip);
        }
        _preview.Children.Add(Dim($"v{_version.Text.Trim()} · by {(_author.Text.Trim().Length > 0 ? _author.Text.Trim() : "unknown")}" + (_description.Text.Trim().Length > 0 ? " · " + _description.Text.Trim() : "")).Also(t => t.Margin = new Thickness(0, 8, 0, 0)));
        _footText.Text = $"Saves as {FileName()}";
    }

    private string FileName() => $"{ModPack.MakeSlug(_name.Text.Trim().Length > 0 ? _name.Text : _project.Name)}-{_version.Text.Trim()}{ModPack.Extension}";

    private async Task PlanAsync()
    {
        if (_services.Workspace.Vfs is not { } vfs)
            return;
        _contents.Children.Clear();
        _contents.Children.Add(Dim("Comparing with the stock game…"));
        await _services.Jobs.RunAsync("Comparing the project with the stock game", context => PackExporter.Plan(_project, vfs, context.Cancel), plan =>
        {
            _plan = plan;
            RefreshContents();
            RefreshPreview();
        });
    }

    private void RefreshContents()
    {
        _contents.Children.Clear();
        if (_plan is not { } plan)
            return;
        _contents.Children.Add(Chips(plan.Files.Select(f => f.Path), RosterFolder != null ? ["Roster"] : []));
        var bytes = plan.Files.Sum(f => f.Size);
        _contents.Children.Add(CheckLine(plan.Files.Count > 0 ? CheckLevel.Ok : CheckLevel.Warning, $"{plan.Files.Count:N0} file{S(plan.Files.Count)} · {FormatSize.Of(bytes)} before packing", $"{plan.Files.Count(f => f.Stock == null)} new, the rest replace stock files."));
        if (plan.SameAsStock > 0)
            _contents.Children.Add(CheckLine(CheckLevel.Ok, $"{plan.SameAsStock} file{S(plan.SameAsStock)} identical to stock dropped", ""));
        if (plan.LeftOut.Count > 0)
            _contents.Children.Add(CheckLine(CheckLevel.Warning, $"Left out: {plan.LeftOut.Count} file{S(plan.LeftOut.Count)}", string.Join("\n", plan.LeftOut.Take(4))));
        _contents.Children.Add(CheckLine(CheckLevel.Ok, $"Made for {plan.Game.TitleId} {plan.Game.AppVersion}", "Players on another version get a warning."));
        _export.IsEnabled = plan.Files.Count > 0 || RosterFolder != null;
    }

    private async Task ExportAsync()
    {
        if (_services.Workspace.Vfs is not { } vfs)
            return;
        var name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : _project.Name;
        if (_version.Text.Trim().Length == 0)
        {
            _services.ReportError("No version", "Give the pack a version, such as 1.0.0.");
            return;
        }
        var dialog = new SaveFileDialog { Filter = "Blueline mod pack (*.blpack)|*.blpack", FileName = FileName(), Title = "Export mod pack", InitialDirectory = Path.GetDirectoryName(_project.Folder) };
        if (dialog.ShowDialog() != true)
            return;
        var options = new PackExportOptions
        {
            Name = name,
            Author = _author.Text.Trim(),
            Version = _version.Text.Trim(),
            Description = _description.Text.Trim(),
            Cover = _cover,
            Icon = _icon,
            Shots = [.. _shots],
            RosterFolder = RosterFolder,
        };
        await _services.Jobs.RunAsync($"Exporting {name}", context => PackExporter.Export(_project, vfs, options, dialog.FileName, new JobProgress(context), context.Cancel), result =>
        {
            SaveArt();
            _services.Notify($"Exported {Path.GetFileName(result.Path)}: {result.Manifest.Files.Count} files, {FormatSize.Of(result.PackedBytes)}" + (result.Manifest.Roster != null ? ", with the roster." : "."));
            Process.Start("explorer.exe", $"/select,\"{result.Path}\"");
            _back();
        });
    }
}
