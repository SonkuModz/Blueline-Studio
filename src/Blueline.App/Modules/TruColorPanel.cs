using System.Diagnostics;
using System.Globalization;
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
/// A whole league's team colours from TruColor (trucolor.net), previewed next to each team's current colours before anything is
/// written. Click a site colour to make it primary, right-click to make it secondary. Writing puts the chosen colours into the
/// project's copy of the game database as one change; the roster save keeps its own copy, which the roster sync carries over.
/// </summary>
public sealed class TruColorPanel : UserControl
{
    private sealed record LeagueChoice(string Label, string SlugStart, int[] GameLeagues)
    {
        public override string ToString() => Label;
    }

    private sealed class Row(TeamInfo team, TruColorTeam site, string how)
    {
        public TeamInfo Team { get; } = team;
        public TruColorTeam Site { get; set; } = site;
        public string How { get; set; } = how;
        /// <summary>Null until a colour is clicked: the automatic order (site order, accent first for black-first teams).</summary>
        public TruColorPick? Pick { get; set; }
        public bool Include { get; set; } = !TruColorImport.IsAlumni(team);
        public TruColorPlan? Plan { get; set; }
    }

    private readonly AppServices _services;
    private readonly ComboBox _league = new() { MinWidth = 200, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _fetch = new() { Content = "Fetch colours", Margin = new Thickness(0, 0, 6, 0) };
    private readonly CheckBox _refresh = new() { Content = "Fetch again (skip the cache)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
    private readonly CheckBox _menu = new() { Content = "Menu colours", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _pres = new() { Content = "Presentation colours", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _overlay = new() { Content = "Score clock colours", IsChecked = true, Margin = new Thickness(0, 0, 4, 0) };
    private readonly ComboBox _shade = new() { Width = 120, Margin = new Thickness(0, 0, 12, 0), ToolTip = "Stock score clock colours sit about 0.5 to 0.7 as bright as the team's menu colour." };
    private readonly CheckBox _gear = new()
    {
        Content = "Home gloves and helmet (NHL)",
        Margin = new Thickness(0, 0, 12, 0),
        ToolTip = "Sets the main colour of the home kit's glove and helmet rows to the primary colour. Stock gear follows the kit, not the logo colours, so check it in the uniform studio afterwards.",
    };
    private readonly TextBox _season = new() { Width = 60, VerticalAlignment = VerticalAlignment.Center, ToolTip = "A season's start year, such as 2018 for 2018-19. Empty: the newest colours." };
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) };
    private readonly Button _write = new() { Content = "Write to project", Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _page = new() { Content = "Open preview page", Margin = new Thickness(0, 0, 6, 0) };
    private readonly List<Row> _list = [];
    private IReadOnlyList<TeamInfo> _teams = [];
    private IReadOnlyList<TruColorTeam> _siteTeams = [];
    private TruColorMatches? _matches;
    private TruColorLeague? _siteLeague;
    private TeamDetails? _details;
    private GearColours? _gearTable;
    private TruColorSite? _site;

    public event Action? CloseRequested;
    public event Action? Saved;

    public TruColorPanel(AppServices services)
    {
        _services = services;
        _write.Style = (Style)FindResource("Primary");
        _write.Click += (_, _) => Write();
        _page.Click += (_, _) => OpenPage();
        _fetch.Click += (_, _) => _ = Fetch();
        foreach (var box in new[] { _menu, _pres, _overlay, _gear })
            box.Click += (_, _) => Replan();
        foreach (var shade in new[] { 1.0, 0.9, 0.8, 0.7, 0.6, 0.5 })
            _shade.Items.Add(new ComboBoxItem { Content = shade == 1 ? "as on the site" : $"×{shade.ToString("0.0", CultureInfo.InvariantCulture)} darker", Tag = shade });
        _shade.SelectedIndex = 0;
        _shade.SelectionChanged += (_, _) => Replan();
        _season.LostFocus += (_, _) => Replan();
        _season.KeyDown += (_, e) => { if (e.Key == Key.Enter) Replan(); };

        var close = new Button { Content = "Back to the art", Style = (Style)FindResource("Quiet"), FontSize = 12, Padding = new Thickness(6, 0, 6, 0) };
        close.Click += (_, _) => CloseRequested?.Invoke();
        var title = new DockPanel { Margin = new Thickness(2, 0, 0, 4) };
        DockPanel.SetDock(close, Dock.Right);
        title.Children.Add(close);
        title.Children.Add(new TextBlock { Text = "League colours from TruColor", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var about = Dim("Reads a league's page on trucolor.net (one page per league, kept on this PC) and matches its teams to the game's by full name. " +
            "Each row shows the site's colours (outlined: used as primary and secondary; click one for primary, right-click for secondary) and what would change. Nothing is written until you press Write.");
        about.Margin = new Thickness(2, 0, 0, 6);

        var fetchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        fetchRow.Children.Add(_league);
        fetchRow.Children.Add(_fetch);
        fetchRow.Children.Add(_refresh);

        var targetRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        targetRow.Children.Add(_menu);
        targetRow.Children.Add(_pres);
        targetRow.Children.Add(_overlay);
        targetRow.Children.Add(_shade);
        targetRow.Children.Add(_gear);
        var seasonLabel = Dim("Season");
        seasonLabel.VerticalAlignment = VerticalAlignment.Center;
        seasonLabel.Margin = new Thickness(0, 0, 6, 0);
        targetRow.Children.Add(seasonLabel);
        targetRow.Children.Add(_season);

        var warning = Dim("A roster save loaded in the game keeps its own copy of these colours and wins over the database. After writing, use Copy edits to roster save on the Build tab (it backs the save up first).");
        warning.Foreground = (Brush)FindResource("WarningBrush");
        warning.Margin = new Thickness(2, 0, 0, 4);

        var top = new StackPanel();
        top.Children.Add(title);
        top.Children.Add(about);
        top.Children.Add(fetchRow);
        top.Children.Add(targetRow);
        top.Children.Add(warning);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(_write);
        buttons.Children.Add(_page);
        var credit = Dim("Colours from TruColor (trucolor.net), fetched politely and cached. The site has no European leagues.");
        credit.Margin = new Thickness(2, 4, 0, 0);
        var bottom = new StackPanel();
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);
        bottom.Children.Add(credit);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(8),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _rows },
        });
        Content = root;
        SetButtons();
    }

    public int RowCount => _list.Count;
    public int ChangeCount => _list.Where(r => r.Include).Sum(r => r.Plan?.ChangeCount ?? 0);

    private TruColorSite Site => _site ??= new TruColorSite(TruColorSite.DefaultCache);

    /// <summary>Called when the panel opens: the game's teams, and the selected team's league as the starting choice.</summary>
    public void SetTeams(TeamInfo? team, IReadOnlyList<TeamInfo> teams)
    {
        _teams = teams;
        var choices = TruColorImport.LeaguePairs
            .Select(p => (p, ids: p.GameLeagues.Where(id => teams.Any(t => t.LeagueId == id)).ToArray()))
            .Where(x => x.ids.Length > 0)
            .Select(x => new LeagueChoice(string.Join(" and ", x.ids.Select(id => teams.First(t => t.LeagueId == id).League).Distinct()), x.p.SlugStart, x.ids))
            .ToList();
        var keep = (_league.SelectedItem as LeagueChoice)?.SlugStart;
        _league.ItemsSource = choices;
        _league.SelectedItem = choices.FirstOrDefault(c => c.SlugStart == keep)
            ?? choices.FirstOrDefault(c => team != null && c.GameLeagues.Contains(team.LeagueId)) ?? choices.FirstOrDefault();
        if (choices.Count == 0)
            _status.Text = "None of the game's leagues are on trucolor.net.";
        SetButtons();
    }

    public Task Fetch()
    {
        if (_league.SelectedItem is not LeagueChoice choice)
            return Task.CompletedTask;
        var site = Site;
        var refresh = _refresh.IsChecked == true;
        return _services.Jobs.RunAsync($"Reading {choice.Label} colours from trucolor.net", async context =>
        {
            context.Status("Reading the hockey index");
            var leagues = await site.ListLeaguesAsync(refresh, context.Cancel);
            var league = leagues.FirstOrDefault(l => l.Key?.StartsWith(choice.SlugStart, StringComparison.Ordinal) == true)
                ?? throw new InvalidDataException($"trucolor.net no longer lists a page for {choice.Label}.");
            context.Status($"Reading {league.Name}");
            var teams = await site.ListTeamsAsync(league, refresh, context.Cancel);
            return (league, teams);
        }, result =>
        {
            _refresh.IsChecked = false;
            (_siteLeague, _siteTeams) = result;
            Load(choice);
        });
    }

    private void Load(LeagueChoice choice)
    {
        _list.Clear();
        _details = null;
        _gearTable = null;
        try
        {
            var file = OpenDatabase();
            _details = TeamDetails.Open(file);
            _gearTable = GearColours.Open(file);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            _services.ReportError("The team table could not be read", e);
            return;
        }
        var gameTeams = _teams.Where(t => choice.GameLeagues.Contains(t.LeagueId)).OrderBy(t => t.LeagueId).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _matches = TruColorImport.Match(gameTeams, _siteTeams);
        foreach (var m in _matches.Matched)
            _list.Add(new Row(m.Team, m.Site, m.How));
        Replan();
        var cached = _siteLeague?.Url is { } url ? Site.CachedAt(url) : null;
        _status.Text = $"{_siteLeague?.Name}: {_matches.Matched.Count} of {gameTeams.Count} game teams matched" +
            (cached is { } at ? $" (page fetched {at:g})." : ".") + " Nothing is written yet.";
    }

    private TdbFile OpenDatabase()
    {
        var workspace = _services.Workspace;
        var file = TdbFile.Parse((byte[])(workspace.ReadCurrent(TeamCatalog.DatabasePath) ?? throw new InvalidDataException("The game database is missing.")).Clone());
        if (workspace.Vfs?.Find(TeamCatalog.MetaPath) is { } meta)
            file.ApplyMeta(Encoding.UTF8.GetString(workspace.Vfs.ReadContent(meta)));
        return file;
    }

    private TruColorOptions Options()
    {
        var targets = TruColorTargets.None;
        if (_menu.IsChecked == true) targets |= TruColorTargets.Menu;
        if (_pres.IsChecked == true) targets |= TruColorTargets.Presentation;
        if (_overlay.IsChecked == true) targets |= TruColorTargets.Overlay;
        if (_gear.IsChecked == true) targets |= TruColorTargets.Gear;
        _shade.IsEnabled = _overlay.IsChecked == true;
        var shade = (_shade.SelectedItem as ComboBoxItem)?.Tag is double d ? d : 1.0;
        int? season = int.TryParse(_season.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) && year is > 1900 and < 2100 ? year : null;
        return new TruColorOptions(targets, shade, season);
    }

    private void Replan()
    {
        if (_details == null)
            return;
        var options = Options();
        foreach (var row in _list)
            row.Plan = TruColorImport.Plan(_details, options.Targets.HasFlag(TruColorTargets.Gear) ? _gearTable : null, row.Team, row.Site, options, row.Pick);
        ShowRows();
    }

    private void ShowRows()
    {
        _rows.Children.Clear();
        foreach (var row in _list)
            _rows.Children.Add(RowView(row));
        if (_matches is { } matches)
        {
            if (matches.UnmatchedGame.Count > 0)
            {
                _rows.Children.Add(Heading("Game teams with no match on the site"));
                foreach (var team in matches.UnmatchedGame.Where(t => _list.All(r => r.Team != t)))
                    _rows.Children.Add(UnmatchedView(team));
            }
            if (matches.UnmatchedSite.Count > 0)
            {
                _rows.Children.Add(Heading("Active site teams with no game team"));
                _rows.Children.Add(Dim(string.Join(", ", matches.UnmatchedSite.Select(t => t.DisplayName))));
            }
        }
        SetButtons();
    }

    private FrameworkElement RowView(Row row)
    {
        var plan = row.Plan;
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var include = new CheckBox { IsChecked = row.Include, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Include this team when writing" };
        include.Click += (_, _) => { row.Include = include.IsChecked == true; SetButtons(); };
        grid.Children.Add(include);

        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = row.Team.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var record = Dim($"{row.Site.DisplayName}{(plan?.Era != null ? ", " + plan.Era.Years : "")}{(row.How == "name" ? "" : $" (by {row.How})")}");
        record.FontSize = 11;
        name.Children.Add(record);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var swatches = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        if (plan?.Era is { } era)
            for (var i = 0; i < era.Colours.Count; i++)
            {
                var colour = era.Colours[i];
                var index = i;
                var role = ReferenceEquals(colour, plan.Primary) ? "P" : ReferenceEquals(colour, plan.Secondary) ? "S" : null;
                var chip = Swatch(colour.Rgb, 22, role != null);
                chip.ToolTip = $"{colour}{(colour.Code != null ? "\n" + colour.Code : "")}{(colour.Estimated ? "\nEstimated by the site" : "")}\nClick: primary. Right-click: secondary.";
                chip.Cursor = Cursors.Hand;
                chip.MouseLeftButtonUp += (_, _) =>
                {
                    var now = plan.CurrentPick ?? new TruColorPick();
                    row.Pick = now with { Primary = index, Secondary = now.Secondary == index ? now.Primary : now.Secondary };
                    Replan();
                };
                chip.MouseRightButtonUp += (_, e) =>
                {
                    var now = plan.CurrentPick ?? new TruColorPick();
                    row.Pick = now with { Secondary = index, Primary = now.Primary == index ? now.Secondary : now.Primary };
                    Replan();
                    e.Handled = true;
                };
                var cell = new StackPanel { Margin = new Thickness(0, 0, 3, 0) };
                cell.Children.Add(chip);
                cell.Children.Add(new TextBlock { Text = role ?? " ", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Style = (Style)FindResource("Dim") });
                swatches.Children.Add(cell);
            }
        Grid.SetColumn(swatches, 2);
        grid.Children.Add(swatches);

        var changes = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var change in plan?.Changes ?? [])
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 2), Opacity = change.Changes ? 1 : 0.45, ToolTip = $"{change.Label}: {GearColours.Hex(change.Old)} → {GearColours.Hex(change.New)}" };
            var label = Dim(ShortLabel(change.Label));
            label.FontSize = 11;
            label.Margin = new Thickness(0, 0, 4, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            item.Children.Add(label);
            item.Children.Add(Swatch(change.Old, 16, false));
            item.Children.Add(new TextBlock { Text = "→", Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("Dim") });
            item.Children.Add(Swatch(change.New, 16, false));
            changes.Children.Add(item);
        }
        foreach (var note in plan?.Notes ?? [])
        {
            var text = Dim(note);
            text.FontSize = 11;
            text.Foreground = (Brush)FindResource("WarningBrush");
            changes.Children.Add(text);
        }
        Grid.SetColumn(changes, 3);
        grid.Children.Add(changes);
        return new Border { BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 3, 0, 3), Child = grid };
    }

    /// <summary>"Score clock primary" as "Clock P", so a row's changes fit on one line.</summary>
    private static string ShortLabel(string label) =>
        label.Replace("Score clock", "Clock").Replace("Presentation", "Pres").Replace(" primary", " P").Replace(" secondary", " S");

    /// <summary>A game team with no match, with a list of the site's teams to pair it with by hand.</summary>
    private FrameworkElement UnmatchedView(TeamInfo team)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(28, 2, 0, 2) };
        var name = new TextBlock { Text = team.Name, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var pick = new ComboBox { MinWidth = 260, ToolTip = "Pair this team with a site team by hand" };
        pick.Items.Add("(leave as it is)");
        foreach (var site in _siteTeams.OrderByDescending(t => t.Active).ThenBy(t => t.Name, StringComparer.Ordinal))
            pick.Items.Add(site);
        pick.SelectedIndex = 0;
        pick.SelectionChanged += (_, _) =>
        {
            if (pick.SelectedItem is not TruColorTeam chosen)
                return;
            _list.Add(new Row(team, chosen, "your choice"));
            Replan();
        };
        panel.Children.Add(name);
        panel.Children.Add(pick);
        return panel;
    }

    private void SetButtons()
    {
        _fetch.IsEnabled = _league.SelectedItem != null;
        var changes = ChangeCount;
        _write.IsEnabled = changes > 0;
        _write.Content = changes > 0 ? $"Write {changes} colour{(changes == 1 ? "" : "s")} to project" : "Write to project";
        _page.IsEnabled = _list.Count > 0;
    }

    private void OpenPage()
    {
        if (_matches == null)
            return;
        var name = _siteLeague?.Name ?? "League";
        var path = Path.Combine(Path.GetTempPath(), $"blueline-trucolor-{string.Concat(name.Where(char.IsLetterOrDigit))}.html");
        File.WriteAllText(path, TruColorImport.PreviewHtml($"{name}: TruColor colours", _list.Where(r => r.Include && r.Plan != null).Select(r => r.Plan!).ToList(), _matches, Options(), $"{name} page"));
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void Write()
    {
        if (_services.RequireProject() is not { } project)
            return;
        var rows = _list.Where(r => r.Include && r.Plan is { ChangeCount: > 0 }).ToList();
        if (rows.Count == 0)
            return;
        var count = rows.Sum(r => r.Plan!.ChangeCount);
        if (Controls.ConfirmDialog.Ask("Write team colours",
                $"Write {count} colour value(s) for {rows.Count} team(s) into the project's game database? This is one change you can undo. The game files and roster saves are not touched.", "Write"))
            WriteNow();
    }

    /// <summary>Writes the ticked teams' colours into the open project without asking. Returns the number of values written.</summary>
    public int WriteNow()
    {
        if (_services.Workspace.Project is not { } project)
            return 0;
        var rows = _list.Where(r => r.Include && r.Plan is { ChangeCount: > 0 }).ToList();
        if (rows.Count == 0)
            return 0;
        int written;
        try
        {
            // Planned again on the project's current copy, so edits saved elsewhere since the fetch are kept.
            var file = OpenDatabase();
            var details = TeamDetails.Open(file);
            var options = Options();
            var gear = options.Targets.HasFlag(TruColorTargets.Gear) ? GearColours.Open(file) : null;
            var plans = rows.Select(r => TruColorImport.Plan(details, gear, r.Team, r.Site, options, r.Pick)).ToList();
            written = TruColorImport.Apply(details, gear, plans);
            project.Set(TeamCatalog.DatabasePath, details.Save(), $"{_siteLeague?.Name ?? "League"} colours from TruColor ({rows.Count} teams)");
            // Keep the rows and picks; show them against the database as it now is.
            var now = OpenDatabase();
            (_details, _gearTable) = (TeamDetails.Open(now), GearColours.Open(now));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
        {
            _services.ReportError("The colours were not written", e);
            return 0;
        }
        Replan();
        _status.Text = $"Wrote {written} colour value(s) for {rows.Count} team(s) to the project. Build the project to try it; copy the edits to the roster save on the Build tab if the game loads one.";
        Saved?.Invoke();
        return written;
    }

    private TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) };

    private TextBlock Dim(string text) => new() { Text = text, Style = (Style)FindResource("Dim"), TextWrapping = TextWrapping.Wrap };

    private Border Swatch(int rgb, double size, bool mark) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(3),
        BorderThickness = new Thickness(mark ? 2 : 1),
        BorderBrush = mark ? (Brush)FindResource("TextBrush") : (Brush)FindResource("LineBrush"),
        Background = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
