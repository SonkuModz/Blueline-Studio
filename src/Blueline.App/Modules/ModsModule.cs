using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core;
using Blueline.Core.Packs;
using Microsoft.Win32;
using static Blueline.App.Modules.PackVisuals;

namespace Blueline.App.Modules;

/// <summary>
/// The mod manager (docs/MOD_PACKS.md): open or drop a .blpack, see what it changes and how it fits this game, then install it.
/// Installed packs are listed in priority order, top wins, with enable, disable, reorder and uninstall. Other mod archives and the
/// mods folder are listed too, because a pack works around them.
/// </summary>
public sealed class ModsModule : UserControl, IEditorModule
{
    private readonly AppServices _services;
    private readonly StackPanel _installed = new();
    private readonly StackPanel _others = new();
    private readonly StackPanel _detail = new() { Margin = new Thickness(18, 14, 18, 14) };
    private readonly TextBlock _footText = Dim("", 12);
    private readonly StackPanel _footButtons = new() { Orientation = Orientation.Horizontal };
    private readonly Border _drop;
    private readonly Dictionary<string, bool> _packWins = new(StringComparer.OrdinalIgnoreCase);
    private readonly CheckBox _roster = new() { Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly ComboBox _slot = new() { MinWidth = 260, Margin = new Thickness(0, 6, 0, 0) };

    private string? _openedPath;
    private PackCheck? _check;
    private string? _selectedId;

    public ModsModule(AppServices services)
    {
        _services = services;

        var open = new Button { Content = "Open pack…", Margin = new Thickness(0, 0, 6, 0) };
        open.Click += (_, _) => PickPack();
        var folder = new Button { Content = "Show folder", Style = (Style)FindResource("Quiet") };
        folder.Click += (_, _) => { if (Manager() is { } m && Directory.Exists(m.Folder)) Process.Start("explorer.exe", m.Folder); };
        var header = new DockPanel { Margin = new Thickness(14, 12, 14, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(open);
        buttons.Children.Add(folder);
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(new TextBlock { Text = "Mod packs", Style = (Style)FindResource("Heading"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center });

        _drop = new Border
        {
            Padding = new Thickness(14),
            Margin = new Thickness(0, 12, 0, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "Drop a .blpack here", HorizontalAlignment = HorizontalAlignment.Center }.Brush(TextBlock.ForegroundProperty, "DimBrush"),
        }.Brush(Border.BorderBrushProperty, "LineBrush");

        var left = new StackPanel { Margin = new Thickness(14, 0, 14, 14) };
        left.Children.Add(Label("Installed · top wins", 0));
        left.Children.Add(_installed);
        left.Children.Add(Label("Not from a pack"));
        left.Children.Add(_others);
        left.Children.Add(_drop);
        var leftScroll = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 340 };
        var leftBorder = new Border { Child = leftScroll, BorderThickness = new Thickness(0, 0, 1, 0) }.Brush(Border.BorderBrushProperty, "LineBrush");

        var foot = new DockPanel { Margin = new Thickness(18, 10, 18, 10) };
        DockPanel.SetDock(_footButtons, Dock.Right);
        foot.Children.Add(_footButtons);
        _footText.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(_footText);
        var footBorder = new Border { Child = foot, BorderThickness = new Thickness(0, 1, 0, 0) }.Brush(Border.BorderBrushProperty, "LineBrush");

        var right = new DockPanel();
        DockPanel.SetDock(footBorder, Dock.Bottom);
        right.Children.Add(footBorder);
        right.Children.Add(new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var body = new DockPanel();
        DockPanel.SetDock(leftBorder, Dock.Left);
        body.Children.Add(leftBorder);
        body.Children.Add(right);

        var root = new DockPanel();
        var headerBorder = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) }.Brush(Border.BorderBrushProperty, "LineBrush");
        DockPanel.SetDock(headerBorder, Dock.Top);
        root.Children.Add(headerBorder);
        root.Children.Add(body);
        Content = root;

        DropTarget.Attach(this, p => p.EndsWith(ModPack.Extension, StringComparison.OrdinalIgnoreCase), files => _ = OpenPack(files[0]), _drop);
        _roster.Click += (_, _) => _slot.IsEnabled = _roster.IsChecked == true;
        Refresh();
    }

    public string Title => "Mods";
    public FrameworkElement View => this;
    public void Activated() => Refresh();

    /// <summary>Opening a .blpack path from elsewhere (the Export page, the command palette) shows it here.</summary>
    public void OpenPath(string path)
    {
        if (path.EndsWith(ModPack.Extension, StringComparison.OrdinalIgnoreCase))
            _ = OpenPack(path);
    }

    private VirtualFileSystem? Vfs => _services.Workspace.Vfs;

    private PackManager? Manager() => Vfs is { } vfs ? new PackManager(GameInstall.Open(vfs.Install.Root)) : null;

    private string? SaveData()
    {
        if (_services.Settings.EmulatorPath is not { Length: > 0 } path || !File.Exists(path))
            return null;
        return Core.Teams.RosterSync.SaveDataFolder(new Core.Emulator.ShadInstall(path).UserFolder);
    }

    private void PickPack()
    {
        var dialog = new OpenFileDialog { Filter = "Blueline mod pack (*.blpack)|*.blpack", Title = "Open a mod pack" };
        if (dialog.ShowDialog() == true)
            _ = OpenPack(dialog.FileName);
    }

    // ---- lists

    private void Refresh()
    {
        _installed.Children.Clear();
        _others.Children.Clear();
        if (Manager() is not { } manager)
        {
            _installed.Children.Add(Dim("Choose the game folder in Settings first."));
            ShowEmpty();
            return;
        }

        if (manager.Packs.Count == 0)
            _installed.Children.Add(Dim("No packs yet. Open or drop a .blpack to install one."));
        for (var i = 0; i < manager.Packs.Count; i++)
            _installed.Children.Add(PackRow(manager, manager.Packs[i], i));

        var owned = manager.OwnedArchives();
        foreach (var name in ModInstaller.InstalledMods(Vfs!.Install).Where(n => !owned.Contains(n)))
            _others.Children.Add(OtherRow(name.StartsWith("patch_") ? "Project: " + ProjectName(name) : name, name));
        if (Vfs.Install.ModsFolder is { } mods)
            _others.Children.Add(OtherRow("Mods folder (Puck Peak)", $"{Path.GetFileName(mods)} · {Vfs.Install.ArchivesAddedByMods.Count + Vfs.Install.ArchivesReplacedByMods.Count} archives"));
        if (_others.Children.Count == 0)
            _others.Children.Add(Dim("None"));

        if (_check != null)
            ShowOpened();
        else if (_selectedId != null && manager.Find(_selectedId) is { } selected)
            ShowInstalled(manager, selected);
        else
            ShowEmpty();
    }

    private static string ProjectName(string archive)
    {
        var stem = Path.GetFileNameWithoutExtension(archive);
        stem = stem.Replace("patch_inxp_99_", "").Replace("patch_game_99_", "");
        var db = stem.LastIndexOf("_db", StringComparison.Ordinal);
        return db > 0 ? stem[..db] : stem;
    }

    private FrameworkElement PackRow(PackManager manager, InstalledPack pack, int index)
    {
        var store = manager.StoreFolder(pack.Id);
        var manifest = manager.StoredManifest(pack.Id);
        byte[]? Art(string? entry) => entry != null && File.Exists(Path.Combine(store, entry)) ? File.ReadAllBytes(Path.Combine(store, entry)) : null;

        var up = new Button { Content = "▲", Style = (Style)FindResource("Quiet"), FontSize = 9, Padding = new Thickness(4, 0, 4, 0), IsEnabled = index > 0, ToolTip = "Higher priority" };
        var down = new Button { Content = "▼", Style = (Style)FindResource("Quiet"), FontSize = 9, Padding = new Thickness(4, 0, 4, 0), IsEnabled = index < manager.Packs.Count - 1, ToolTip = "Lower priority" };
        up.Click += (_, _) => _ = Change($"Moving {pack.Name} up", m => m.Move(pack.Id, index - 1, Vfs!));
        down.Click += (_, _) => _ = Change($"Moving {pack.Name} down", m => m.Move(pack.Id, index + 1, Vfs!));
        var arrows = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        arrows.Children.Add(up);
        arrows.Children.Add(down);

        var toggle = new CheckBox { IsChecked = pack.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = pack.Enabled ? "On: click to turn off" : "Off: click to turn on" };
        toggle.Click += (_, _) => _ = Change($"{(pack.Enabled ? "Turning off" : "Turning on")} {pack.Name}", m => m.SetEnabled(pack.Id, !pack.Enabled, Vfs!));

        var rank = new TextBlock { Text = (index + 1).ToString(), Width = 18, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Brush(TextBlock.ForegroundProperty, "DimBrush");
        var icon = Icon(Art(manifest.Art.Icon), pack.Name, 36);
        icon.Margin = new Thickness(4, 0, 10, 0);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = pack.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(Dim($"v{pack.Version} · {manifest.Files.Count:N0} file{S(manifest.Files.Count)}{(pack.Enabled ? "" : " · off")}{(pack.Roster != null ? " · roster" : "")}"));

        var row = new DockPanel();
        DockPanel.SetDock(arrows, Dock.Left);
        DockPanel.SetDock(rank, Dock.Left);
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(arrows);
        row.Children.Add(rank);
        row.Children.Add(icon);
        row.Children.Add(toggle);
        row.Children.Add(text);
        var selected = _check == null && _selectedId == pack.Id;
        var border = new Border { Child = row, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
        border.Brush(Border.BackgroundProperty, selected ? "AccentSoftBrush" : "PanelBrush").Brush(Border.BorderBrushProperty, selected ? "AccentBrush" : "LineBrush");
        border.MouseLeftButtonUp += (_, _) => { _check = null; _openedPath = null; _selectedId = pack.Id; Refresh(); };
        return border;
    }

    private static FrameworkElement OtherRow(string title, string detail)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(Dim(detail));
        return new Border { Child = text, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 4) };
    }

    // ---- details

    private void ShowEmpty()
    {
        _detail.Children.Clear();
        _footButtons.Children.Clear();
        _footText.Text = "";
        var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 80, 0, 0), MaxWidth = 380 };
        empty.Children.Add(new TextBlock { Text = "", FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) }.Brush(TextBlock.ForegroundProperty, "AccentTextBrush"));
        empty.Children.Add(new TextBlock { Text = "Install a mod pack", FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        empty.Children.Add(new TextBlock { Text = "Open or drop a .blpack to see what it changes and check it against your game. Nothing is written until you press Install.", TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) }.Brush(TextBlock.ForegroundProperty, "DimBrush"));
        _detail.Children.Add(empty);
    }

    private void Header(PackManifest manifest, Func<string?, byte[]?> art)
    {
        _detail.Children.Clear();
        _detail.Children.Add(Cover(art(manifest.Art.Cover), manifest.Name, 170));
        if (manifest.Art.Shots.Count > 0)
        {
            var shots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var shot in manifest.Art.Shots)
                if (art(shot) is { } png)
                    shots.Children.Add(Shot(png, 54));
            _detail.Children.Add(shots);
        }
        var meta = $"v{manifest.Version} · by {(manifest.Author.Length > 0 ? manifest.Author : "unknown")} · made with Blueline {manifest.Blueline} · {FormatSize.Of(manifest.Bytes)}";
        _detail.Children.Add(Dim(meta).Also(t => t.Margin = new Thickness(0, 10, 0, 0)));
        if (manifest.Description.Length > 0)
            _detail.Children.Add(new TextBlock { Text = manifest.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
    }

    private static Grid TwoColumns(UIElement left, UIElement right)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private async Task OpenPack(string path)
    {
        if (Vfs is not { } vfs || Manager() is not { } manager)
        {
            _services.ReportError("No game is open", "Choose the game folder in Settings first.");
            return;
        }
        await _services.Jobs.RunAsync($"Checking {Path.GetFileName(path)}", _ => manager.Check(path, vfs), check =>
        {
            _openedPath = path;
            _check = check;
            _packWins.Clear();
            _roster.IsChecked = true;
            _selectedId = null;
            Refresh();
        });
    }

    private void ShowOpened()
    {
        var check = _check!;
        var manifest = check.Manifest;
        Header(manifest, entry => ModPack.ReadArt(_openedPath!, entry));

        var game = new StackPanel();
        game.Children.Add(Label("Your game"));
        foreach (var line in check.Lines)
            game.Children.Add(CheckLine(line.Level, line.Title, line.Detail));
        game.Children.Add(Label("Changes"));
        game.Children.Add(Chips(manifest.Files.Select(f => f.Path), manifest.Roster != null ? ["Roster"] : []));
        if (manifest.LeftOut.Count > 0)
            game.Children.Add(Dim($"The author's project also had {manifest.LeftOut.Count} change{S(manifest.LeftOut.Count)} a pack cannot carry, such as score clock files."));

        var side = new StackPanel();
        side.Children.Add(Label($"Conflicts · {check.Conflicts.Count + check.Others.Sum(o => o.Paths.Count)} file{S(check.Conflicts.Count + check.Others.Sum(o => o.Paths.Count))}"));
        if (check.Conflicts.Count == 0 && check.Others.Count == 0)
            side.Children.Add(Dim("None. No other pack or mod archive holds these files."));
        if (check.Conflicts.Count > 0)
        {
            var list = new StackPanel();
            foreach (var c in check.Conflicts.Take(12))
                list.Children.Add(ConflictLine(c.Path, c.Winner, string.Join(", ", c.Losers)));
            if (check.Conflicts.Count > 12)
                list.Children.Add(Dim($"and {check.Conflicts.Count - 12} more"));
            list.Children.Add(Dim("Reorder packs in the list to change who wins.").Also(t => t.Margin = new Thickness(0, 6, 0, 0)));
            side.Children.Add(Card(list));
        }
        foreach (var other in check.Others)
        {
            var box = new StackPanel();
            box.Children.Add(new TextBlock { Text = other.Archive, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            box.Children.Add(Dim($"{other.Paths.Count} shared file{S(other.Paths.Count)}, e.g. {Path.GetFileName(other.Paths[0])}"));
            var choice = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var wins = _packWins.GetValueOrDefault(other.Archive);
            foreach (var (label, value) in new[] { ("Keep theirs", false), ("Pack wins", true) })
            {
                var pill = new System.Windows.Controls.Primitives.ToggleButton { Content = label, IsChecked = wins == value, Style = (Style)FindResource("Pill"), Margin = new Thickness(0, 0, 6, 0) };
                pill.Click += (_, _) => { _packWins[other.Archive] = value; ShowOpened(); };
                pill.ToolTip = value ? $"Removes {other.Archive} when the pack installs. Its project can install it again." : "The pack leaves these files out.";
                choice.Children.Add(pill);
            }
            box.Children.Add(choice);
            side.Children.Add(Card(box).Also(b => b.Margin = new Thickness(0, 0, 0, 6)));
        }

        if (manifest.Roster is { } roster)
        {
            side.Children.Add(Label("Custom roster"));
            var saveData = SaveData();
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = $"Install the pack's roster “{roster.Title}”", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (saveData == null)
            {
                _roster.IsChecked = false;
                _roster.IsEnabled = false;
                text.Children.Add(Dim("Choose shadPS4 in Settings so Blueline knows where the game's saves are."));
            }
            else
            {
                _roster.IsEnabled = true;
                FillSlots(saveData, Manager()!.Find(manifest.Id)?.Roster?.Slot);
                text.Children.Add(_slot);
                text.Children.Add(Dim("Never goes into RosterCustom0, Puck Peak's roster. A save it replaces is backed up first and put back when you uninstall. Close the game before installing.").Also(t => t.Margin = new Thickness(0, 6, 0, 0)));
            }
            _slot.IsEnabled = _roster.IsChecked == true;
            var row = new DockPanel();
            if (_roster.Parent is Panel old)
                old.Children.Remove(_roster);
            DockPanel.SetDock(_roster, Dock.Left);
            row.Children.Add(_roster);
            row.Children.Add(text);
            side.Children.Add(Card(row));
        }

        _detail.Children.Add(TwoColumns(game, side));

        _footButtons.Children.Clear();
        _footText.Text = check.IsUpdate ? "An older version of this pack is installed. Installing updates it in place." : Path.GetFileName(_openedPath);
        var close = new Button { Content = "Close", Style = (Style)FindResource("Quiet"), Margin = new Thickness(0, 0, 6, 0) };
        close.Click += (_, _) => { _check = null; _openedPath = null; Refresh(); };
        var install = new Button { Content = check.IsUpdate ? $"Update to v{manifest.Version}" : "Install", Style = (Style)FindResource("Primary"), IsEnabled = !check.Blocked };
        install.Click += (_, _) => _ = InstallOpened();
        _footButtons.Children.Add(close);
        _footButtons.Children.Add(install);
    }

    private void FillSlots(string saveData, string? current)
    {
        var slots = RosterSlots.List(saveData).Where(s => RosterSlots.IsPackSlot(s.Name)).ToList();
        _slot.Items.Clear();
        var free = RosterSlots.FirstFree(saveData);
        if (current != null)
            _slot.Items.Add(new ComboBoxItem { Content = $"{current} · this pack's slot", Tag = current });
        if (free != null && free != current)
            _slot.Items.Add(new ComboBoxItem { Content = $"{free} · free slot", Tag = free });
        foreach (var s in slots.Where(s => s.Name != current))
            _slot.Items.Add(new ComboBoxItem { Content = $"{s.Name} · replace “{s.Title}” (backed up)", Tag = s.Name });
        _slot.SelectedIndex = _slot.Items.Count > 0 ? 0 : -1;
    }

    private static UIElement ConflictLine(string path, string winner, string losers)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var who = new TextBlock { Text = winner, Margin = new Thickness(8, 0, 0, 0) }.Brush(TextBlock.ForegroundProperty, "SuccessBrush");
        var lose = new TextBlock { Text = losers, Margin = new Thickness(8, 0, 0, 0), TextDecorations = TextDecorations.Strikethrough }.Brush(TextBlock.ForegroundProperty, "DimBrush");
        DockPanel.SetDock(lose, Dock.Right);
        DockPanel.SetDock(who, Dock.Right);
        row.Children.Add(lose);
        row.Children.Add(who);
        row.Children.Add(new TextBlock { Text = Path.GetFileName(path), ToolTip = path, TextTrimming = TextTrimming.CharacterEllipsis });
        return row;
    }

    private async Task InstallOpened()
    {
        if (_check is not { } check || _openedPath is not { } path || Vfs is not { } vfs || Manager() is not { } manager)
            return;
        var manifest = check.Manifest;
        var remove = check.Others.Where(o => _packWins.GetValueOrDefault(o.Archive)).Select(o => o.Archive).ToList();
        var withRoster = manifest.Roster != null && _roster.IsChecked == true && SaveData() != null;
        var slot = withRoster ? (_slot.SelectedItem as ComboBoxItem)?.Tag as string : null;
        if (withRoster && slot == null)
        {
            _services.ReportError("No roster slot", "All custom roster slots from RosterCustom1 are taken. Pick one to replace.");
            return;
        }

        var lines = new List<string> { $"Builds {manifest.Name}'s archives on this machine and adds them to {vfs.Install.Databigs}. No original archive changes." };
        if (remove.Count > 0)
            lines.Add($"Removes {string.Join(", ", remove)}, so the pack wins their shared files.");
        if (withRoster)
        {
            var target = Path.Combine(SaveData()!, slot!);
            lines.Add(Directory.Exists(target)
                ? $"Replaces the roster in {slot} (“{RosterSlots.Title(target)}”). A copy goes to {RosterSlots.BackupFolder(SaveData()!)} first."
                : $"Puts the pack's roster in {slot}, a free slot. RosterCustom0 is not touched.");
        }
        if (check.Lines.Any(l => l.Level == CheckLevel.Warning))
            lines.Add("Warnings: " + string.Join(" ", check.Lines.Where(l => l.Level == CheckLevel.Warning).Select(l => l.Title + ".")));
        if (!ConfirmDialog.Ask(check.IsUpdate ? "Update this pack?" : "Install this pack?", string.Join("\n\n", lines), check.IsUpdate ? "Update" : "Install"))
            return;

        await _services.Jobs.RunAsync($"Installing {manifest.Name}", context => manager.Install(path, vfs, new PackInstallOptions
        {
            SaveData = withRoster ? SaveData() : null,
            InstallRoster = withRoster,
            Slot = slot,
            RemoveOthers = remove,
        }, new JobProgress(context)), result =>
        {
            foreach (var warning in result.Warnings)
                _services.Log.Warning(warning);
            _services.Notify($"Installed {result.Pack.Name}: {result.Archives.Count} archive{S(result.Archives.Count)}" + (result.Pack.Roster is { } r ? $", roster in {r.Slot}." : "."));
            _check = null;
            _openedPath = null;
            _selectedId = result.Pack.Id;
            Refresh();
        });
    }

    private void ShowInstalled(PackManager manager, InstalledPack pack)
    {
        var store = manager.StoreFolder(pack.Id);
        var manifest = manager.StoredManifest(pack.Id);
        Header(manifest, entry => entry != null && File.Exists(Path.Combine(store, entry)) ? File.ReadAllBytes(Path.Combine(store, entry)) : null);

        var archives = ModProject.Open(store).Installed;
        var left = new StackPanel();
        left.Children.Add(Label("In your game"));
        left.Children.Add(CheckLine(pack.Enabled ? CheckLevel.Ok : CheckLevel.Warning, pack.Enabled ? "On" : "Off", pack.Enabled ? $"{archives.Count} archive{S(archives.Count)}: {string.Join(", ", archives.Select(a => a.FileName))}" : "Its archives are removed until you turn it back on."));
        var rank = manager.Packs.ToList().IndexOf(pack) + 1;
        left.Children.Add(CheckLine(CheckLevel.Ok, $"Priority {rank} of {manager.Packs.Count}", rank == 1 ? "It wins every file it shares with other packs." : "Packs above it win the files they share."));
        left.Children.Add(Label("Changes"));
        left.Children.Add(Chips(manifest.Files.Select(f => f.Path)));

        var right = new StackPanel();
        right.Children.Add(Label("Custom roster"));
        right.Children.Add(pack.Roster is { } r
            ? CheckLine(CheckLevel.Ok, $"In {r.Slot}", r.Backup != null ? "The save that was there is backed up and comes back when you uninstall." : "The slot was free; uninstalling moves this roster to the backup folder.")
            : Dim(manifest.Roster != null ? "The pack has a roster, but it was not installed. Open the pack again to install it." : "This pack has no roster."));
        _detail.Children.Add(TwoColumns(left, right));

        _footButtons.Children.Clear();
        _footText.Text = $"Installed {pack.InstalledAt:yyyy-MM-dd HH:mm}";
        var uninstall = new Button { Content = "Uninstall", Style = (Style)FindResource("Quiet"), Margin = new Thickness(0, 0, 6, 0) };
        uninstall.Click += (_, _) => Uninstall(manager, pack);
        var toggle = new Button { Content = pack.Enabled ? "Turn off" : "Turn on", Style = (Style)FindResource(pack.Enabled ? "Quiet" : "Primary") };
        toggle.Click += (_, _) => _ = Change($"{(pack.Enabled ? "Turning off" : "Turning on")} {pack.Name}", m => m.SetEnabled(pack.Id, !pack.Enabled, Vfs!));
        _footButtons.Children.Add(uninstall);
        _footButtons.Children.Add(toggle);
    }

    private void Uninstall(PackManager manager, InstalledPack pack)
    {
        var message = $"Removes {pack.Name}'s archives from the game. Other packs are rebuilt if they now win its files.";
        if (pack.Roster is { } r)
            message += r.Backup != null ? $"\n\nThe roster in {r.Slot} is moved to the backup folder, and the save that was there before comes back." : $"\n\nThe roster in {r.Slot} is moved to the backup folder.";
        if (!ConfirmDialog.Ask("Uninstall this pack?", message, "Uninstall"))
            return;
        _ = Change($"Uninstalling {pack.Name}", m =>
        {
            var result = m.Uninstall(pack.Id, Vfs!);
            return result;
        }, result =>
        {
            if (result is PackUninstallResult { Roster: RosterRemoval.ChangedSinceInstall })
            {
                var choice = ConfirmDialog.Choose("The roster was saved over in game",
                    $"The roster in {pack.Roster!.Slot} changed since the pack installed it, so you may have edits in it.\n\nRemove it anyway (a copy is kept in the backup folder), or keep it and uninstall the rest?",
                    "Remove anyway", "Keep the roster");
                if (choice >= 0)
                    _ = Change($"Uninstalling {pack.Name}", m => m.Uninstall(pack.Id, Vfs!, forceRoster: choice == 0, keepRoster: choice == 1));
            }
            else if (result is PackUninstallResult { Done: false } failed)
                _services.ReportError("Not uninstalled", string.Join(" ", failed.Problems));
        });
    }

    /// <summary>Runs a manager change as a job, then refreshes the lists.</summary>
    private Task Change<T>(string title, Func<PackManager, T> work, Action<T>? done = null)
    {
        if (Manager() is not { } manager || Vfs == null)
            return Task.CompletedTask;
        return _services.Jobs.RunAsync(title, _ => work(manager), result =>
        {
            if (result is IReadOnlyList<string> warnings)
                foreach (var warning in warnings)
                    _services.Log.Warning(warning);
            if (manager.Find(_selectedId ?? "") == null && _check == null)
                _selectedId = null;
            _services.Notify(title.Replace("ing ", "ed ", StringComparison.Ordinal) + ".");
            Refresh();
            done?.Invoke(result);
        });
    }
}

internal static class FluentExtensions
{
    public static T Also<T>(this T value, Action<T> act)
    {
        act(value);
        return value;
    }
}
