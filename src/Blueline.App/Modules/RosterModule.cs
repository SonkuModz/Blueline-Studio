using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core.Roster;
using Blueline.Core.Teams;
using Blueline.Formats.Tdb;
using Blueline.Formats.Textures;
using Microsoft.Win32;

namespace Blueline.App.Modules;

/// <summary>
/// The roster editor: every player, with every field the game database stores for him (docs/ROSTER_EDITOR.md). Edits change a
/// working copy at once and are listed; saving replays them onto the project's current database as one undo step, so edits
/// saved meanwhile by other tabs stay. Portraits are saved straight to the project as their own undo step.
/// </summary>
public sealed class RosterModule : UserControl, IEditorModule
{
    /// <summary>One line of the player list.</summary>
    public sealed record PlayerRow(long GameId, string Number, string Pos, string Name, string Team, int Overall, string Style, PlayerSummary Player);

    /// <summary>A change kept so it can be replayed onto a fresh copy of the database when saving.</summary>
    private sealed record Edit(string Tag, string Label, Action<RosterBook> Apply);

    private static readonly HashSet<string> OpenGroups = [RosterBook.NameGroup, RosterBook.BirthGroup, RosterBook.RatingGroup, RosterBook.StyleGroup];
    private const int AnyTeam = int.MinValue;

    private readonly AppServices _services;
    private readonly TextBox _search = new() { MinWidth = 160, ToolTip = "Name, last name or number" };
    private readonly ComboBox _teamFilter = new() { MinWidth = 170, ToolTip = "Show one team" };
    private readonly ComboBox _posFilter = new() { MinWidth = 90, ToolTip = "Show one position" };
    private readonly CheckBox _retired = new() { Content = "Retired", ToolTip = "Also list retired players", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ListView _list = new() { SelectionMode = SelectionMode.Extended, BorderThickness = new Thickness(0) };
    private readonly TextBlock _count = new() { Margin = new Thickness(2, 6, 0, 0) };
    private readonly StackPanel _detail = new();
    private readonly ScrollViewer _detailScroll;
    private readonly TextBox _fieldSearch = new() { MinWidth = 180, ToolTip = "Find a field by its label or database name" };
    private readonly Button _save = new() { Content = "Save to project", Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _discard = new() { Content = "Discard", Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _lines = new() { Content = "Lines", ToolTip = "Edit the chosen team's lines", Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) };
    private readonly CheckBox _realYears = new() { Content = "Real birth years", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly HashSet<string> _expanded = [.. OpenGroups];
    private readonly List<Edit> _edits = [];

    private RosterBook? _book;
    private RosterChoices? _choices;
    private int _detectedShift;
    private string? _loadedBlob;
    private bool _loading;
    private bool _showingLines;
    private bool _filling;
    private bool _quiet;
    private List<PlayerRow> _rows = [];

    public RosterModule(AppServices services)
    {
        _services = services;
        _save.Style = (Style)FindResource("Primary");
        _save.Click += (_, _) => SaveToProject();
        _discard.Click += (_, _) =>
        {
            if (_edits.Count == 0 || ConfirmDialog.Ask("Discard roster edits", $"Throw away {_edits.Count} unsaved change{S(_edits.Count)}?", "Discard"))
                _ = LoadAsync();
        };
        _lines.Click += (_, _) => ShowLines();
        _search.TextChanged += (_, _) => FillList();
        _teamFilter.SelectionChanged += (_, _) => { if (!_filling) { FillList(); if (_showingLines) ShowLines(); } };
        _posFilter.SelectionChanged += (_, _) => { if (!_filling) FillList(); };
        _retired.Click += (_, _) => FillList();
        _fieldSearch.TextChanged += (_, _) => { if (!_showingLines) ShowSelection(); };
        _realYears.Click += (_, _) =>
        {
            if (_book == null)
                return;
            _book.YearShift = _realYears.IsChecked == true ? 0 : _detectedShift;
            ShowSelection();
        };
        _list.SelectionChanged += (_, _) =>
        {
            if (_quiet)
                return;
            _showingLines = false;
            ShowSelection();
        };

        var view = new GridView();
        view.Columns.Add(Column("#", nameof(PlayerRow.Number), 38));
        view.Columns.Add(Column("Pos", nameof(PlayerRow.Pos), 40));
        view.Columns.Add(Column("Name", nameof(PlayerRow.Name), 170));
        view.Columns.Add(Column("Team", nameof(PlayerRow.Team), 56));
        view.Columns.Add(Column("OVR", nameof(PlayerRow.Overall), 42));
        view.Columns.Add(Column("Style", nameof(PlayerRow.Style), 130));
        _list.View = view;
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);

        // Left: filters over the list.
        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        filters.Children.Add(Hinted(_search, "Find a player"));
        filters.Children.Add(Spaced(_teamFilter));
        filters.Children.Add(Spaced(_posFilter));
        filters.Children.Add(_retired);
        var left = new DockPanel();
        DockPanel.SetDock(filters, Dock.Top);
        DockPanel.SetDock(_count, Dock.Bottom);
        left.Children.Add(filters);
        left.Children.Add(_count);
        left.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(2), Child = _list });

        // Right: actions, detail, status.
        var heading = new TextBlock { Text = "Roster", Style = (Style)FindResource("Heading"), Margin = new Thickness(0, 0, 0, 2) };
        var note = Dim("Every player the game database holds. A roster save loaded in the game keeps its own copy and wins, so after saving use \"Copy edits to roster save\" on the Build tab (it matches players by id, and lines by slot).");
        note.Margin = new Thickness(0, 0, 0, 8);
        var toSync = new Button { Content = "Go to Build", Style = (Style)FindResource("Quiet"), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Copy saved edits into RosterCustom0" };
        toSync.Click += (_, _) => _services.Navigate("Build");
        var actions = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = false };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_save);
        buttons.Children.Add(_discard);
        buttons.Children.Add(_lines);
        buttons.Children.Add(toSync);
        buttons.Children.Add(_realYears);
        actions.Children.Add(buttons);
        var findField = Hinted(_fieldSearch, "Find a field");
        DockPanel.SetDock(findField, Dock.Right);
        actions.Children.Add(findField);

        _detailScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _detail };
        var top = new StackPanel();
        top.Children.Add(heading);
        top.Children.Add(note);
        top.Children.Add(actions);
        var right = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        right.Children.Add(top);
        right.Children.Add(_status);
        right.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(12, 10, 12, 10), Child = _detailScroll });

        var grid = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(510), MinWidth = 300 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380 });
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(splitter);
        grid.Children.Add(right);
        Content = grid;

        SetDirty();
        ShowMessage("Open a game to edit its roster.");
        services.Workspace.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && _edits.Count == 0 && !_loading && (_book == null || CurrentBlob() != _loadedBlob))
                _ = LoadAsync();
        });
    }

    public string Title => "Roster";
    public FrameworkElement View => this;
    public bool IsLoading => _loading;
    public RosterBook? Book => _book;
    public int PendingEdits => _edits.Count;

    // For the screenshot tour.
    public void ShowTeam(int team)
    {
        if (_teamFilter.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, team)) is { } item)
            _teamFilter.SelectedItem = item;
    }

    public void SelectPlayers(IEnumerable<long> ids)
    {
        var set = ids.ToHashSet();
        _list.SelectedItems.Clear();
        foreach (var row in _rows.Where(r => set.Contains(r.GameId)))
            _list.SelectedItems.Add(row);
    }

    public void OpenLines() => ShowLines();

    public void SaveEdits() => SaveToProject();

    public bool EditField(long gid, string key, object value)
    {
        var done = SetField(gid, key, value, key);
        ShowSelection();
        return done;
    }

    public void Activated()
    {
        if (_edits.Count == 0 && !_loading && (_book == null || CurrentBlob() != _loadedBlob))
            _ = LoadAsync();
    }

    public void OpenPath(string path)
    {
    }

    // ---- Loading

    private string? CurrentBlob() =>
        _services.Workspace.Project?.TryGet(TeamCatalog.DatabasePath, out var change) == true ? change.Blob : "game";

    /// <summary>The project's database as it is now (else the game's), parsed, with the birth-year shift found.</summary>
    private (RosterBook Book, int Shift) ReadBook()
    {
        var vfs = _services.Workspace.Vfs ?? throw new InvalidOperationException("No game is open.");
        var meta = RosterSource.Meta(vfs);
        var data = _services.Workspace.ReadCurrent(TeamCatalog.DatabasePath) ?? throw new InvalidDataException($"The game has no {TeamCatalog.DatabasePath}.");
        var book = RosterBook.Open(RosterSource.Parse(data, meta));
        var shift = vfs.ReadStock(TeamCatalog.DatabasePath) is { } stock ? RosterBook.DetectYearShift(RosterBook.Open(RosterSource.Parse(stock, meta)), book) : 0;
        book.YearShift = shift;
        return (book, shift);
    }

    public async Task LoadAsync()
    {
        if (_services.Workspace.Vfs is not { } vfs)
        {
            ShowMessage("Open a game to edit its roster.");
            return;
        }
        _loading = true;
        ShowMessage("Reading the roster…");
        try
        {
            var blob = CurrentBlob();
            var (book, shift, choices) = await Task.Run(() =>
            {
                var (b, s) = ReadBook();
                return (b, s, RosterSource.Choices(vfs, b));
            });
            _book = book;
            _choices = choices;
            _detectedShift = shift;
            _loadedBlob = blob;
            _edits.Clear();
            if (_realYears.IsChecked == true)
                _book.YearShift = 0;
            _realYears.Visibility = shift == 0 ? Visibility.Collapsed : Visibility.Visible;
            _realYears.ToolTip = $"This roster moved birth years back {shift} years to fit the game's calendar. Tick to see and type real years.";
            FillFilters();
            FillList();
            SetDirty();
            _status.Text = "";
        }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException or KeyNotFoundException)
        {
            _book = null;
            ShowMessage("The roster could not be read.");
            _services.ReportError("The roster could not be read", e);
        }
        finally
        {
            _loading = false;
        }
    }

    private void FillFilters()
    {
        if (_book == null)
            return;
        _filling = true;
        var keepTeam = (_teamFilter.SelectedItem as ComboBoxItem)?.Tag;
        _teamFilter.Items.Clear();
        _teamFilter.Items.Add(new ComboBoxItem { Content = "All teams", Tag = AnyTeam });
        _teamFilter.Items.Add(new ComboBoxItem { Content = "Free agents", Tag = -1 });
        foreach (var team in _book.Teams.Values.OrderBy(t => RosterBook.ClubLeagues.Contains(t.League) ? 0 : 1).ThenBy(t => t.League).ThenBy(t => t.Name))
            _teamFilter.Items.Add(new ComboBoxItem { Content = team.Abbreviation.Length > 0 ? $"{team.Name} ({team.Abbreviation})" : team.Name, Tag = team.Index });
        _teamFilter.SelectedItem = _teamFilter.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, keepTeam)) ?? _teamFilter.Items[0];

        var keepPos = (_posFilter.SelectedItem as ComboBoxItem)?.Tag;
        _posFilter.Items.Clear();
        _posFilter.Items.Add(new ComboBoxItem { Content = "All", Tag = -1 });
        _posFilter.Items.Add(new ComboBoxItem { Content = "Skaters", Tag = -2 });
        _posFilter.Items.Add(new ComboBoxItem { Content = "Forwards", Tag = -3 });
        foreach (var (value, name) in new[] { (0, "C"), (1, "LW"), (2, "RW"), (3, "D"), (4, "G") })
            _posFilter.Items.Add(new ComboBoxItem { Content = name, Tag = value });
        _posFilter.SelectedItem = _posFilter.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, keepPos)) ?? _posFilter.Items[0];
        _filling = false;
    }

    private int SelectedTeam => (_teamFilter.SelectedItem as ComboBoxItem)?.Tag is int t ? t : AnyTeam;

    private static readonly string[] PositionNames = ["C", "LW", "RW", "D", "G"];

    private PlayerRow RowOf(PlayerSummary p) => new(p.GameId, p.Jersey.ToString(), p.Position is >= 0 and < 5 ? PositionNames[p.Position] : p.Position.ToString(),
        p.Name, TeamAbbr(p.Club), p.Overall, StyleName(p), p);

    private string TeamAbbr(int team) => _book!.Teams.TryGetValue(team, out var t) ? (t.Abbreviation.Length > 0 ? t.Abbreviation : t.Name) : team < 0 ? "FA" : team.ToString();

    private string StyleName(PlayerSummary p) =>
        _choices?.NameOf(p.IsGoalie ? RosterChoices.GoalieStyle : RosterChoices.SkaterStyle, p.Style) ?? p.Style.ToString();

    private void FillList()
    {
        if (_book == null)
            return;
        var keep = _list.SelectedItems.Cast<PlayerRow>().Select(r => r.GameId).ToHashSet();
        var words = _search.Text.Trim();
        var team = SelectedTeam;
        var pos = (_posFilter.SelectedItem as ComboBoxItem)?.Tag is int p ? p : -1;
        IEnumerable<PlayerSummary> players = team switch
        {
            AnyTeam => _book.Players(),
            -1 => _book.Players().Where(x => x.Active && x.Club < 0 || !_book.Teams.ContainsKey(x.Club)),
            _ => _book.PlayersOn(team).Select(id => _book.Summary(id)!),
        };
        if (_retired.IsChecked != true && team == AnyTeam)
            players = players.Where(x => x.Active);
        players = pos switch
        {
            -1 => players,
            -2 => players.Where(x => !x.IsGoalie),
            -3 => players.Where(x => x.Position is >= 0 and <= 2),
            _ => players.Where(x => x.Position == pos),
        };
        if (words.Length > 0)
            players = players.Where(x => x.Name.Contains(words, StringComparison.OrdinalIgnoreCase) || x.Jersey.ToString() == words || x.GameId.ToString() == words);
        _rows = players.OrderByDescending(x => x.Overall).ThenBy(x => x.LastName, StringComparer.OrdinalIgnoreCase).Select(RowOf).ToList();
        _list.ItemsSource = _rows;
        foreach (var row in _rows.Where(r => keep.Contains(r.GameId)))
            _list.SelectedItems.Add(row);
        _count.Text = $"{_rows.Count:N0} player{S(_rows.Count)}";
        _count.Style = (Style)FindResource("Dim");
        _lines.IsEnabled = team >= 0;
        if (_list.SelectedItems.Count == 0 && !_showingLines)
            ShowMessage(_rows.Count == 0 ? "No player matches." : "Pick a player, or several to edit them together.");
    }

    /// <summary>Re-reads the shown players' list rows after an edit, keeping the list and selection as they are.</summary>
    private void RefreshRows(IEnumerable<long> ids)
    {
        if (_book == null)
            return;
        var set = ids.ToHashSet();
        var selected = _list.SelectedItems.Cast<PlayerRow>().Select(r => r.GameId).ToHashSet();
        var changed = false;
        for (var i = 0; i < _rows.Count; i++)
            if (set.Contains(_rows[i].GameId) && _book.Summary(_rows[i].GameId) is { } p)
            {
                _rows[i] = RowOf(p);
                changed = true;
            }
        if (!changed)
            return;
        // Quietly: the detail panel stays as it is (a slider may be mid-drag).
        _quiet = true;
        try
        {
            _list.ItemsSource = null;
            _list.ItemsSource = _rows;
            foreach (var row in _rows.Where(r => selected.Contains(r.GameId)))
                _list.SelectedItems.Add(row);
        }
        finally
        {
            _quiet = false;
        }
    }

    // ---- Editing

    /// <summary>
    /// Applies a change to the working copy and keeps it for saving. A change with the same tag as the last one replaces it, so
    /// dragging a slider is one change.
    /// </summary>
    private bool Apply(string tag, string label, Action<RosterBook> apply, IEnumerable<long> players)
    {
        if (_book == null)
            return false;
        var shift = _book.YearShift;
        void WithShift(RosterBook b)
        {
            var keep = b.YearShift;
            b.YearShift = shift;
            try { apply(b); }
            finally { b.YearShift = keep; }
        }
        try
        {
            WithShift(_book);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            _status.Text = e.Message;
            _status.Foreground = (Brush)FindResource("DangerBrush");
            return false;
        }
        if (_edits.Count > 0 && _edits[^1].Tag == tag)
            _edits[^1] = new Edit(tag, label, WithShift);
        else
            _edits.Add(new Edit(tag, label, WithShift));
        _status.Text = label;
        _status.Foreground = (Brush)FindResource("DimBrush");
        SetDirty();
        RefreshRows(players);
        return true;
    }

    private bool SetField(long gid, string key, object value, string name)
    {
        var who = _book!.Summary(gid)?.Name ?? gid.ToString();
        return Apply($"{gid}|{key}", $"{who}: {name} → {value}", b =>
        {
            var field = b.Find(gid, key) ?? throw new KeyNotFoundException($"{who} has no {key}.");
            b.Set(field, value);
        }, [gid]);
    }

    private void SaveToProject()
    {
        if (_book == null || _edits.Count == 0 || _services.RequireProject() is not { } project)
            return;
        byte[] bytes;
        try
        {
            // Replay onto the project's current copy, so edits saved meanwhile by other tabs (gear colours, team details) stay.
            var (fresh, _) = ReadBook();
            foreach (var edit in _edits)
                edit.Apply(fresh);
            bytes = fresh.Save();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            _services.ReportError("The roster edits were not saved", e);
            return;
        }
        var count = _edits.Count;
        var label = count == 1 ? $"Roster: {_edits[0].Label}" : $"Roster: {count} changes";
        project.Set(TeamCatalog.DatabasePath, bytes, label);
        project.Save();
        _edits.Clear();
        _loadedBlob = CurrentBlob();
        SetDirty();
        _status.Text = $"Saved {count} change{S(count)} to the project. Copy them to the roster save on the Build tab, or build to try them.";
        _services.Notify($"Saved {count} roster change{S(count)}");
    }

    private void SetDirty()
    {
        _save.IsEnabled = _discard.IsEnabled = _edits.Count > 0;
        _save.Content = _edits.Count > 0 ? $"Save {_edits.Count} change{S(_edits.Count)} to project" : "Save to project";
    }

    // ---- Detail

    private void ShowSelection()
    {
        if (_book == null)
            return;
        var selected = _list.SelectedItems.Cast<PlayerRow>().ToList();
        if (selected.Count == 1)
            ShowPlayer(selected[0].GameId);
        else if (selected.Count > 1)
            ShowBulk(selected.Select(r => r.GameId).ToList());
        else if (!_showingLines)
            ShowMessage("Pick a player, or several to edit them together.");
    }

    private void ShowMessage(string text)
    {
        _detail.Children.Clear();
        _detail.Children.Add(Dim(text));
    }

    private void ShowPlayer(long gid)
    {
        var book = _book!;
        if (book.Summary(gid) is not { } p)
            return;
        var offset = _detailScroll.VerticalOffset;
        _detail.Children.Clear();
        _detail.Children.Add(Header(p));
        _detail.Children.Add(TeamRow(p));

        var words = _fieldSearch.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(PlayerField f) => words.All(w => f.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || f.Field.Label.Contains(w, StringComparison.OrdinalIgnoreCase)
            || f.Group.Contains(w, StringComparison.OrdinalIgnoreCase));
        var fields = book.FieldsOf(gid);
        var club = p.Club;
        // Roster rows: the club's in full; other teams' only for number and captaincy, under Teams and lines.
        var shown = fields.Where(f => f.Table != PlayerTable.Roster || f.Team == club || f.Field.Label is "jersey" or "captain")
            .Where(f => !(f.Table == PlayerTable.Roster && f.Field.Depth == 1 && f.Field.Label != "rosterstatus"))
            .Where(Matches).ToList();
        foreach (var group in shown.GroupBy(f => f.Group))
        {
            var list = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            if (group.Key == RosterBook.LinesGroup)
                list.Children.Add(SlotsSummary(gid));
            foreach (var field in group)
                list.Children.Add(FieldRow(gid, field));
            var expander = new Expander
            {
                Header = new TextBlock { Text = group.Key, FontWeight = FontWeights.SemiBold },
                Content = list,
                IsExpanded = words.Length > 0 || _expanded.Contains(group.Key),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var key = group.Key;
            expander.Expanded += (_, _) => { if (words.Length == 0) _expanded.Add(key); };
            expander.Collapsed += (_, _) => { if (words.Length == 0) _expanded.Remove(key); };
            _detail.Children.Add(expander);
        }
        if (shown.Count == 0)
            _detail.Children.Add(Dim("No field matches."));
        Dispatcher.BeginInvoke(() => _detailScroll.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private FrameworkElement Header(PlayerSummary p)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var portrait = new StackPanel { Margin = new Thickness(0, 0, 14, 0), Width = 132 };
        var frame = new Border
        {
            Width = 132, Height = 132, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true,
            BorderBrush = (Brush)FindResource("LineBrush"), Background = (Brush)FindResource("RaisedBrush"),
        };
        var path = RosterSource.PortraitPath(p.ArtId);
        if (Portrait(path) is { } image)
            frame.Child = new Image { Source = image, Stretch = Stretch.Uniform };
        else
            frame.Child = new TextBlock { Text = "No portrait", Style = (Style)FindResource("Dim"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        frame.ToolTip = $"{path}{(_services.Workspace.IsEdited(path) ? " (changed in the project)" : "")}";
        DropTarget.Attach(frame, f => File.Exists(f) && ImageFiles.IsSupported(f), files => ReplacePortrait(p, files[0]), frame);
        portrait.Children.Add(frame);
        var replace = new Button { Content = "Replace portrait…", Style = (Style)FindResource("Quiet"), Margin = new Thickness(0, 4, 0, 0), FontSize = 12, ToolTip = "Pick a picture, or drop one on the portrait" };
        replace.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = $"Portrait for {p.Name}", Filter = ImageFiles.OpenFilter };
            if (dialog.ShowDialog() == true)
                ReplacePortrait(p, dialog.FileName);
        };
        portrait.Children.Add(replace);
        portrait.Children.Add(new TextBlock { Text = $"Art id {p.ArtId}", Style = (Style)FindResource("Dim"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = p.Name, FontSize = 22, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var pos = p.Position is >= 0 and < 5 ? PositionNames[p.Position] : p.Position.ToString();
        info.Children.Add(new TextBlock { Text = $"#{p.Jersey}  ·  {pos}  ·  {_book!.TeamName(p.Club)}{(p.Active ? "" : "  ·  retired")}", Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 2, 0, 8) });
        var chips = new WrapPanel();
        chips.Children.Add(Pill($"{p.Overall} OVR", true));
        chips.Children.Add(Pill(StyleName(p), false));
        chips.Children.Add(Pill($"id {p.GameId}", false));
        info.Children.Add(chips);

        // Archetype and number here too, as they also change every team row.
        var quick = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var style = new ComboBox { MinWidth = 150, Margin = new Thickness(0, 0, 8, 4), ToolTip = "Archetype; also set on every team he is on" };
        foreach (var c in _choices?.Get(p.IsGoalie ? RosterChoices.GoalieStyle : RosterChoices.SkaterStyle) ?? [])
            style.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Value });
        style.SelectedItem = style.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (long)i.Tag == p.Style);
        style.SelectionChanged += (_, _) =>
        {
            if (style.SelectedItem is ComboBoxItem { Tag: long v } item && v != p.Style
                && Apply($"{p.GameId}|style", $"{p.Name}: archetype → {item.Content}", b => b.SetArchetype(p.GameId, v), [p.GameId]))
                ShowPlayer(p.GameId);
        };
        var number = new TextBox { Text = p.Jersey.ToString(), Width = 52, Margin = new Thickness(0, 0, 8, 4), ToolTip = "Jersey number on every team he is on" };
        void SetNumber()
        {
            if (int.TryParse(number.Text, out var n) && n != _book!.Summary(p.GameId)!.Jersey
                && Apply($"{p.GameId}|number", $"{p.Name}: number → {n}", b => b.SetJersey(p.GameId, n), [p.GameId]))
                ShowPlayer(p.GameId);
        }
        number.LostFocus += (_, _) => SetNumber();
        number.KeyDown += (_, e) => { if (e.Key == Key.Enter) SetNumber(); };
        quick.Children.Add(Labelled("Archetype", style));
        quick.Children.Add(Labelled("Number", number));
        info.Children.Add(quick);

        Grid.SetColumn(info, 1);
        grid.Children.Add(portrait);
        grid.Children.Add(info);
        return grid;
    }

    private System.Windows.Media.Imaging.BitmapSource? Portrait(string path)
    {
        try
        {
            return _services.Workspace.ReadCurrent(path) is { } data && GameTexture.TryOpen(data) is { Blocker: null } texture ? OverlayCanvas.ToBitmap(texture.DecodeMip(0)) : null;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private void ReplacePortrait(PlayerSummary p, string file)
    {
        if (_services.Workspace.Vfs is not { } vfs || _services.RequireProject() is not { } project)
            return;
        try
        {
            var (path, gnf, resized) = RosterSource.ImportPortrait(vfs, p.ArtId, ImageFiles.Load(file));
            project.Set(path, gnf, $"Portrait for {p.Name}");
            project.Save();
            _status.Text = $"Portrait for {p.Name} saved to the project{(resized ? ", resized to match" : "")}. Players who share art id {p.ArtId} show it too.";
            _status.Foreground = (Brush)FindResource("DimBrush");
            ShowPlayer(p.GameId);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            _services.ReportError("The portrait was not imported", e);
        }
    }

    private FrameworkElement TeamRow(PlayerSummary p)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var target = new ComboBox { MinWidth = 220, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var team in _book!.Teams.Values.Where(t => RosterBook.ClubLeagues.Contains(t.League)).OrderBy(t => t.League).ThenBy(t => t.Name))
            target.Items.Add(new ComboBoxItem { Content = $"{team.Name} ({team.Abbreviation})", Tag = team.Index });
        target.SelectedItem = target.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == p.Club);
        var move = new Button { Content = "Move", ToolTip = "Moves his club roster spot. He loses his lines and captaincy there and starts as a scratch." };
        move.Click += (_, _) =>
        {
            if (target.SelectedItem is not ComboBoxItem { Tag: int to } item || to == p.Club)
                return;
            if (_book.MembershipOn(p.GameId, p.Club) == null)
            {
                _status.Text = $"{p.Name} has no club roster spot to move. Free agents cannot be signed here yet.";
                return;
            }
            var from = p.Club;
            if (Apply($"{p.GameId}|move|{Guid.NewGuid()}", $"{p.Name}: moved to {item.Content}", b => b.Move(p.GameId, from, to), [p.GameId]))
            {
                FillList();
                ShowPlayer(p.GameId);
            }
        };
        row.Children.Add(new TextBlock { Text = "Club", Style = (Style)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center, Width = 60 });
        row.Children.Add(target);
        row.Children.Add(move);
        return row;
    }

    private FrameworkElement SlotsSummary(long gid)
    {
        var text = new StringBuilder();
        foreach (var m in _book!.Memberships(gid))
        {
            var slots = _book.Lines(m.Team).Where(l => l.Value == gid).Select(l => RosterBook.SlotLabel(l.Key)).ToList();
            text.AppendLine($"{_book.TeamName(m.Team)}: {(slots.Count == 0 ? "no slot (scratch)" : string.Join(", ", slots))}");
        }
        var block = Dim(text.ToString().TrimEnd());
        block.FontSize = 11.5;
        block.Margin = new Thickness(0, 0, 0, 6);
        return block;
    }

    private FrameworkElement FieldRow(long gid, PlayerField field)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock
        {
            Text = field.Table == PlayerTable.Roster && field.Team != _book!.Summary(gid)!.Club ? $"{field.Label} ({TeamAbbr(field.Team ?? -1)})" : field.Label,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = $"{field.Key}{(field.Hint != null ? "\n" + field.Hint : "")}",
        };
        grid.Children.Add(label);
        var editor = Editor(gid, field);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    private FrameworkElement Editor(long gid, PlayerField field)
    {
        var book = _book!;
        var readOnly = field.Field.Label is "game_id" or "key" or "playerindex" or "index" || field.Table == PlayerTable.Roster && field.Field.Label == "team";
        if (readOnly)
            return new TextBlock { Text = book.Format(field, _choices), Style = (Style)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Links tables; use Move to change the team." };

        switch (field.Kind)
        {
            case PlayerFieldKind.Rating:
            {
                var high = field.Field.RangeHigh is long h && h < 99 ? h : 99;
                var slider = new Slider { Minimum = 0, Maximum = high, Value = Convert.ToDouble(field.Value), IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
                var box = new TextBox { Text = field.Value.ToString(), Width = 44, Margin = new Thickness(8, 0, 0, 0) };
                var bar = new DockPanel();
                DockPanel.SetDock(box, Dock.Right);
                bar.Children.Add(box);
                bar.Children.Add(slider);
                var syncing = false;
                slider.ValueChanged += (_, _) =>
                {
                    if (syncing)
                        return;
                    syncing = true;
                    box.Text = ((long)slider.Value).ToString();
                    SetField(gid, field.Key, (long)slider.Value, field.Label);
                    syncing = false;
                };
                void Typed()
                {
                    if (syncing || !long.TryParse(box.Text, out var v))
                        return;
                    v = Math.Clamp(v, 0, high);
                    syncing = true;
                    slider.Value = v;
                    box.Text = v.ToString();
                    SetField(gid, field.Key, v, field.Label);
                    syncing = false;
                }
                box.LostFocus += (_, _) => Typed();
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Typed(); };
                return bar;
            }
            case PlayerFieldKind.Flag:
            {
                var check = new CheckBox { IsChecked = Convert.ToInt64(field.Value) != 0, VerticalAlignment = VerticalAlignment.Center };
                check.Click += (_, _) => SetField(gid, field.Key, check.IsChecked == true ? 1L : 0L, field.Label);
                return check;
            }
            case PlayerFieldKind.Choice or PlayerFieldKind.Team when (field.Kind == PlayerFieldKind.Team ? _choices?.Get(RosterChoices.Team) : _choices?.Get(field.Choices)) is { Count: > 0 } options:
            {
                var combo = new ComboBox { IsEditable = false, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 200 };
                var current = Convert.ToInt64(field.Value);
                foreach (var c in options)
                    combo.Items.Add(new ComboBoxItem { Content = $"{c.Name}", Tag = c.Value, ToolTip = c.Value.ToString() });
                if (options.All(c => c.Value != current))
                    combo.Items.Insert(0, new ComboBoxItem { Content = $"{current} (not in the list)", Tag = current });
                combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (long)i.Tag == current);
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem { Tag: long v } && v != current && SetField(gid, field.Key, v, field.Label))
                        current = v;
                };
                return combo;
            }
            case PlayerFieldKind.Colour:
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                var rgb = Convert.ToInt32(field.Value);
                var swatch = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = (Brush)FindResource("LineBrush"), Margin = new Thickness(0, 0, 8, 0) };
                void Paint(int c) => swatch.Background = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
                Paint(rgb);
                var box = new TextBox { Text = GearColours.Hex(rgb), Width = 80 };
                void Typed()
                {
                    if (GearColours.TryParseHex(box.Text, out var c) && SetField(gid, field.Key, (long)c, field.Label))
                        Paint(c);
                }
                box.LostFocus += (_, _) => Typed();
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Typed(); };
                panel.Children.Add(swatch);
                panel.Children.Add(box);
                return panel;
            }
            default:
            {
                var box = new TextBox { Text = field.IsText ? (string)field.Value : Shown(field), MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 120 };
                var hint = field.Kind switch
                {
                    PlayerFieldKind.Height => "Inches, or 6'2\"",
                    PlayerFieldKind.Weight => "Pounds",
                    PlayerFieldKind.Money => "Dollars, e.g. 12.5M or 850000 (steps of $5,000)",
                    PlayerFieldKind.Year => "Year, 0 for none",
                    _ => null,
                };
                if (hint != null)
                    box.ToolTip = hint;
                var last = box.Text;
                void Typed()
                {
                    if (box.Text == last || _book == null)
                        return;
                    try
                    {
                        var value = _book.Parse(_book.Find(gid, field.Key)!, box.Text, _choices);
                        if (SetField(gid, field.Key, value, field.Label))
                        {
                            last = box.Text;
                            if (field.Field.Label is "firstname" or "lastname" or "skill_overall" or "position")
                                ShowPlayer(gid);
                        }
                    }
                    catch (Exception e) when (e is FormatException or ArgumentException)
                    {
                        _status.Text = e.Message;
                        _status.Foreground = (Brush)FindResource("DangerBrush");
                    }
                }
                box.LostFocus += (_, _) => Typed();
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Typed(); };
                return box;
            }
        }
    }

    /// <summary>Numbers as typed back: money as dollars, others plain.</summary>
    private static string Shown(PlayerField field) => field.Kind switch
    {
        PlayerFieldKind.Money => $"{Convert.ToInt64(field.Value)}",
        _ => field.Value?.ToString() ?? "",
    };

    // ---- Lines

    private static readonly (string Title, string Prefix)[] LineGroups =
    [
        ("Even strength", "l"), ("Power play", "pp"), ("Penalty kill", "pk"), ("Overtime", "ot"), ("Shootout and extra attacker", "s"), ("Threes", "three"),
    ];

    private void ShowLines()
    {
        var team = SelectedTeam;
        if (_book == null || team < 0)
            return;
        _showingLines = true;
        _quiet = true;
        _list.UnselectAll();
        _quiet = false;
        _detail.Children.Clear();
        _detail.Children.Add(new TextBlock { Text = $"{_book.TeamName(team)} lines", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        _detail.Children.Add(Dim("Pick who plays each slot. Putting a player in a filled slot swaps the two when the incoming player held a slot of the same kind; otherwise the one replaced goes to no slot."));
        var players = _book.PlayersOn(team).Select(id => _book.Summary(id)!).OrderBy(p => p.Position).ThenByDescending(p => p.Overall).ToList();
        var lines = _book.Lines(team);
        var slots = _book.LineSlots;
        string GroupOf(string slot) => slot switch
        {
            _ when slot.StartsWith("three", StringComparison.Ordinal) => "three",
            _ when slot.StartsWith("pp", StringComparison.Ordinal) => "pp",
            _ when slot.StartsWith("pk", StringComparison.Ordinal) => "pk",
            _ when slot.StartsWith("ot", StringComparison.Ordinal) => "ot",
            _ when slot.StartsWith('s') || slot.StartsWith('x') || slot.StartsWith('g') => "s",
            _ => "l",
        };
        foreach (var (title, prefix) in LineGroups)
        {
            var mine = slots.Where(s => GroupOf(s) == prefix).ToList();
            if (mine.Count == 0)
                continue;
            var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
            foreach (var slot in mine)
            {
                var combo = new ComboBox { Width = 190, Margin = new Thickness(0, 0, 8, 6) };
                combo.Items.Add(new ComboBoxItem { Content = "—", Tag = -1L });
                foreach (var p in players)
                    combo.Items.Add(new ComboBoxItem { Content = $"{PositionNames.ElementAtOrDefault(p.Position)} {p.Name} ({p.Overall})", Tag = p.GameId });
                var holder = lines.TryGetValue(slot, out var h) ? h : -1L;
                combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (long)i.Tag == holder);
                var s = slot;
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is not ComboBoxItem { Tag: long who } || who == holder)
                        return;
                    long? gid = who < 0 ? null : who;
                    var name = gid is { } g ? _book.Summary(g)!.Name : "nobody";
                    if (Apply($"{team}|slot|{Guid.NewGuid()}", $"{_book.TeamName(team)}: {RosterBook.SlotLabel(s)} → {name}", b => b.SetSlot(team, s, gid), gid is { } x ? [x] : []))
                        Dispatcher.BeginInvoke(ShowLines);
                };
                wrap.Children.Add(Labelled(RosterBook.SlotLabel(slot), combo));
            }
            _detail.Children.Add(new Expander
            {
                Header = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold },
                Content = wrap,
                IsExpanded = prefix is "l" or "pp" || _expanded.Contains("lines:" + prefix),
                Margin = new Thickness(0, 6, 0, 0),
            });
        }
        var scratches = players.Where(p => !lines.Values.Contains(p.GameId)).Select(p => p.Name).ToList();
        if (scratches.Count > 0)
            _detail.Children.Add(Dim("In no slot: " + string.Join(", ", scratches)));
    }

    // ---- Bulk

    private void ShowBulk(IReadOnlyList<long> ids)
    {
        var book = _book!;
        _detail.Children.Clear();
        _detail.Children.Add(new TextBlock { Text = $"{ids.Count} players", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        _detail.Children.Add(Dim(string.Join(", ", ids.Take(12).Select(id => book.Summary(id)!.Name)) + (ids.Count > 12 ? $" and {ids.Count - 12} more" : "")));

        // Ratings up or down.
        var delta = new Slider { Minimum = -30, Maximum = 30, Value = 0, IsSnapToTickEnabled = true, TickFrequency = 1, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var deltaText = new TextBlock { Text = "+0", Width = 36, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        delta.ValueChanged += (_, _) => deltaText.Text = (delta.Value >= 0 ? "+" : "") + (int)delta.Value;
        var overall = new CheckBox { Content = "Overall too", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var go = new Button { Content = "Apply" };
        go.Click += (_, _) =>
        {
            var d = (int)delta.Value;
            if (d == 0)
                return;
            var withOverall = overall.IsChecked == true;
            if (Apply($"bulk|{Guid.NewGuid()}", $"{ids.Count} players: ratings {(d > 0 ? "+" : "")}{d}", b => b.AddToRatings(ids, d, withOverall), ids))
                delta.Value = 0;
        };
        var ratings = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        ratings.Children.Add(delta);
        ratings.Children.Add(deltaText);
        ratings.Children.Add(overall);
        ratings.Children.Add(go);
        _detail.Children.Add(Section("Ratings up or down", ratings, "Every 0-99 rating, kept within 0 to 99."));

        // One field for all.
        var first = book.FieldsOf(ids[0]).Where(f => f.Table != PlayerTable.Roster && f.Field.Label is not ("game_id" or "key" or "playerindex")).ToList();
        var fieldPick = new ComboBox { Width = 260, Margin = new Thickness(0, 0, 8, 0), IsTextSearchEnabled = true };
        foreach (var f in first.OrderBy(f => f.Group).ThenBy(f => f.Label))
            fieldPick.Items.Add(new ComboBoxItem { Content = $"{f.Group} · {f.Label}", Tag = f.Key });
        var value = new TextBox { Width = 140, Margin = new Thickness(0, 0, 8, 0), ToolTip = "As you would type it for one player: a number, a name from the list, yes/no, RRGGBB, 12.5M…" };
        var setAll = new Button { Content = "Set for all" };
        setAll.Click += (_, _) =>
        {
            if (fieldPick.SelectedItem is not ComboBoxItem { Tag: string key } item)
                return;
            var text = value.Text;
            var skipped = 0;
            Apply($"bulkset|{Guid.NewGuid()}", $"{ids.Count} players: {item.Content} → {text}", b =>
            {
                skipped = 0;
                foreach (var id in ids)
                {
                    if (b.Find(id, key) is not { } f)
                    {
                        skipped++;
                        continue;
                    }
                    b.Set(f, b.Parse(f, text, _choices));
                }
            }, ids);
            if (skipped > 0)
                _status.Text += $" ({skipped} player{S(skipped)} without this field left alone)";
        };
        var one = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        one.Children.Add(fieldPick);
        one.Children.Add(value);
        one.Children.Add(setAll);
        _detail.Children.Add(Section("Set one field", one, "Skater and goalie fields differ; players without the field are left alone."));

        // Move all.
        var target = new ComboBox { MinWidth = 220, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var team in book.Teams.Values.Where(t => RosterBook.ClubLeagues.Contains(t.League)).OrderBy(t => t.League).ThenBy(t => t.Name))
            target.Items.Add(new ComboBoxItem { Content = $"{team.Name} ({team.Abbreviation})", Tag = team.Index });
        var move = new Button { Content = "Move all" };
        move.Click += (_, _) =>
        {
            if (target.SelectedItem is not ComboBoxItem { Tag: int to } item)
                return;
            var movable = ids.Where(id => book.Summary(id)!.Club is var c && c != to && book.MembershipOn(id, c) != null)
                .Select(id => (id, from: book.Summary(id)!.Club)).ToList();
            if (movable.Count == 0)
                return;
            if (Apply($"bulkmove|{Guid.NewGuid()}", $"{movable.Count} players moved to {item.Content}", b =>
                {
                    foreach (var (id, from) in movable)
                        b.Move(id, from, to);
                }, ids))
                FillList();
        };
        var moveRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        moveRow.Children.Add(target);
        moveRow.Children.Add(move);
        _detail.Children.Add(Section("Move to a club", moveRow, "Each loses his lines and captaincy and starts as a scratch."));
    }

    // ---- Small parts

    private FrameworkElement Section(string title, FrameworkElement body, string note)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        var dim = Dim(note);
        dim.FontSize = 11.5;
        panel.Children.Add(dim);
        panel.Children.Add(body);
        return panel;
    }

    private static GridViewColumn Column(string header, string property, double width) =>
        new() { Header = header, Width = width, DisplayMemberBinding = new System.Windows.Data.Binding(property) };

    private FrameworkElement Labelled(string label, FrameworkElement control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Dim"), FontSize = 11 });
        panel.Children.Add(control);
        return panel;
    }

    private FrameworkElement Pill(string text, bool accent)
    {
        var border = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3), Margin = new Thickness(0, 0, 6, 4) };
        border.SetResourceReference(Border.BackgroundProperty, accent ? "AccentSoftBrush" : "RaisedBrush");
        var block = new TextBlock { Text = text, FontSize = 12, FontWeight = accent ? FontWeights.SemiBold : FontWeights.Normal };
        border.Child = block;
        return border;
    }

    private FrameworkElement Hinted(TextBox box, string hint)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 6, 4) };
        var text = new TextBlock { Text = hint, Style = (Style)FindResource("Dim"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        box.TextChanged += (_, _) => text.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        grid.Children.Add(box);
        grid.Children.Add(text);
        return grid;
    }

    private static FrameworkElement Spaced(FrameworkElement control)
    {
        control.Margin = new Thickness(0, 0, 6, 4);
        return control;
    }

    private TextBlock Dim(string text) => new() { Text = text, Style = (Style)FindResource("Dim"), TextWrapping = TextWrapping.Wrap };

    private static string S(int n) => n == 1 ? "" : "s";
}
