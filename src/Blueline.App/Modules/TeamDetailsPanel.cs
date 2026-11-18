using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Blueline.App.Shell;
using Blueline.Core.Teams;
using Blueline.Formats.Tdb;

namespace Blueline.App.Modules;

/// <summary>
/// Everything the game database holds for one team: names, the score clock, menu and presentation colours, its palette, arena,
/// songs, goalie masks, gear brands, mascot, effects and links. Edits stay in a working copy until saved to the project; saving
/// replays them onto the project's current database, so glove and helmet edits saved meanwhile are kept.
/// </summary>
public sealed class TeamDetailsPanel : UserControl
{
    /// <summary>Groups open when a team is first shown.</summary>
    private static readonly HashSet<string> OpenGroups =
        [TeamDetails.NamesGroup, TeamDetails.OverlayGroup, TeamDetails.MenuGroup, TeamDetails.PresentationGroup, TeamDetails.ArenaColourGroup];

    private static readonly int[] CommonColours =
    [
        0x000000, 0x1B1B1B, 0x555555, 0xA2AAAD, 0xFFFFFF, 0xC8102E, 0x8D1B3D, 0xF47A38, 0xFFB81C, 0xFCB514,
        0x006847, 0x00843D, 0x00205B, 0x002654, 0x003E7E, 0x0038A8, 0x6CACE4, 0x00778B, 0x5E2D79, 0x6F263D,
    ];

    private readonly AppServices _services;
    private readonly StackPanel _body = new();
    private readonly TextBox _search = new() { MinWidth = 200, ToolTip = "Find a field by its label or database name" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) };
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _save = new() { Content = "Save to project", Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _discard = new() { Content = "Discard" };
    private readonly HashSet<string> _expanded = [.. OpenGroups];
    private TeamInfo? _team;
    private IReadOnlyList<TeamInfo> _teams = [];
    private TeamDetails? _details;
    private string? _loadedBlob;
    private bool _showing;

    public event Action? CloseRequested;

    /// <summary>Raised after a save, so the tab can re-read team names and colours.</summary>
    public event Action? Saved;

    public TeamDetailsPanel(AppServices services)
    {
        _services = services;
        _save.Style = (Style)FindResource("Primary");
        _save.Click += (_, _) => SaveToProject();
        _discard.Click += (_, _) => Load();
        _search.TextChanged += (_, _) => Build();

        var close = new Button { Content = "Back to the art", Style = (Style)FindResource("Quiet"), FontSize = 12, Padding = new Thickness(6, 0, 6, 0) };
        close.Click += (_, _) => CloseRequested?.Invoke();
        var title = new DockPanel { Margin = new Thickness(2, 0, 0, 4) };
        DockPanel.SetDock(close, Dock.Right);
        title.Children.Add(close);
        title.Children.Add(_title);

        var warning = Dim("These are roster tables. A roster save loaded in the game keeps its own copy and wins over them. After saving, use \"Copy edits to roster save\" on the Build tab to put them into RosterCustom0 as well.");
        warning.Foreground = (Brush)FindResource("WarningBrush");
        warning.Margin = new Thickness(2, 0, 0, 8);

        var searchRow = new Grid { Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        var hint = new TextBlock { Text = "Find a field", Style = (Style)FindResource("Dim"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        _search.TextChanged += (_, _) => hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        searchRow.Children.Add(_search);
        searchRow.Children.Add(hint);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(_save);
        buttons.Children.Add(_discard);
        var bottom = new StackPanel();
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var top = new StackPanel();
        top.Children.Add(title);
        top.Children.Add(warning);
        top.Children.Add(searchRow);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _body },
        });
        Content = root;
        SetDirty();

        services.Workspace.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (_showing && !IsDirty && CurrentBlob() != _loadedBlob)
                Load();
        });
    }

    public bool IsDirty => _details?.Edits.Count > 0;
    public TeamDetails? Details => _details;

    /// <summary>Changes team. Returns false, keeping the current team, when unsaved edits were not given up.</summary>
    public bool SetTeam(TeamInfo? team, IReadOnlyList<TeamInfo> teams)
    {
        _teams = teams;
        if (team?.Index == _team?.Index && _details != null)
        {
            _team = team;
            return true;
        }
        if (IsDirty && !Controls.ConfirmDialog.Ask("Unsaved team details",
                $"{_team?.Name} has {_details!.Edits.Count} unsaved change{(_details.Edits.Count == 1 ? "" : "s")}. Switching team discards them.", "Discard and switch"))
            return false;
        _team = team;
        _details = null;
        if (_showing)
            Load();
        return true;
    }

    public void SetShowing(bool showing)
    {
        _showing = showing;
        if (showing && (_details == null || (!IsDirty && CurrentBlob() != _loadedBlob)))
            Load();
    }

    private string? CurrentBlob() =>
        _services.Workspace.Project?.TryGet(TeamCatalog.DatabasePath, out var change) == true ? change.Blob : null;

    /// <summary>The database as the project has it now, with names applied.</summary>
    private TeamDetails? Read()
    {
        var workspace = _services.Workspace;
        if (workspace.Vfs is not { } vfs || workspace.ReadCurrent(TeamCatalog.DatabasePath) is not { } data)
            return null;
        var file = TdbFile.Parse((byte[])data.Clone());
        if (vfs.Find(TeamCatalog.MetaPath) is { } meta)
            file.ApplyMeta(Encoding.UTF8.GetString(vfs.ReadContent(meta)));
        return TeamDetails.Open(file);
    }

    private void Load()
    {
        _details = null;
        _status.Text = "";
        _title.Text = _team == null ? "Team details" : $"{_team.Name}: team details";
        if (_team != null)
        {
            try
            {
                _details = Read();
                _loadedBlob = CurrentBlob();
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                _services.ReportError("The team table could not be read", e);
            }
        }
        SetDirty();
        Build();
    }

    // ---- Building the sections

    private void Build()
    {
        _body.Children.Clear();
        if (_details == null || _team == null)
        {
            _body.Children.Add(Dim(_team == null ? "Choose a team." : "The team table could not be read."));
            return;
        }
        var fields = _details.FieldsOf(_team.Index);
        if (fields.Count == 0)
        {
            _body.Children.Add(Dim($"Team number {_team.Index} is not in the team table."));
            return;
        }

        var words = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(TeamField f) => words.All(w => f.Label.Contains(w, StringComparison.CurrentCultureIgnoreCase)
            || f.Field.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || f.Group.Contains(w, StringComparison.CurrentCultureIgnoreCase));
        var edited = _details.Edits.Select(e => (e.Table, e.Row, e.Field)).ToHashSet();
        var teamColours = fields.Where(f => f.Kind == TeamFieldKind.Colour && Convert.ToInt32(f.Value) > 0)
            .Select(f => Convert.ToInt32(f.Value)).Distinct().Take(16).ToList();

        foreach (var group in fields.Where(Matches).GroupBy(f => f.Group))
        {
            var list = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            foreach (var note in GroupNotes(group.Key, group.ToList()))
                list.Children.Add(note);
            foreach (var field in group)
                list.Children.Add(Row(field, edited.Contains((field.Table, field.Row, field.Field.Label)), teamColours));

            var count = group.Count(f => edited.Contains((f.Table, f.Row, f.Field.Label)));
            var expander = new Expander
            {
                Header = new TextBlock { Text = group.Key + (count > 0 ? $"  ●{count}" : ""), FontWeight = FontWeights.SemiBold },
                Content = list,
                IsExpanded = words.Length > 0 || _expanded.Contains(group.Key),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var key = group.Key;
            expander.Expanded += (_, _) => { if (words.Length == 0) _expanded.Add(key); };
            expander.Collapsed += (_, _) => { if (words.Length == 0) _expanded.Remove(key); };
            _body.Children.Add(expander);
        }
        if (_body.Children.Count == 0)
            _body.Children.Add(Dim("No field matches."));
    }

    private IEnumerable<UIElement> GroupNotes(string group, IReadOnlyList<TeamField> fields)
    {
        switch (group)
        {
            case TeamDetails.OverlayGroup:
                yield return Note("Assumed to be the score clock and other in-game graphics, from the field names and one measured clock (Calgary). Not yet seen in game.");
                break;
            case TeamDetails.ArenaGroup when _team != null:
                var sharing = _details!.TeamsSharingArena(_team.Index);
                if (sharing.Count > 0)
                {
                    var names = sharing.Select(i => _teams.FirstOrDefault(t => t.Index == i)?.Name ?? $"team {i}").Take(6).ToList();
                    var note = Note($"This arena row is shared with {string.Join(", ", names)}{(sharing.Count > names.Count ? $" and {sharing.Count - names.Count} more" : "")}, so a change here changes them too.");
                    note.Foreground = (Brush)FindResource("WarningBrush");
                    yield return note;
                }
                break;
            case TeamDetails.LinkGroup:
                var warn = Note("These point at other data. Changing one re-points this team at another team's art, audio, arena or league, or at nothing.");
                warn.Foreground = (Brush)FindResource("WarningBrush");
                yield return warn;
                break;
            case TeamDetails.OtherGroup or TeamDetails.EffectGroup or TeamDetails.MascotGroup:
                yield return Note("Fields with no known meaning keep their database name.");
                break;
        }
    }

    private FrameworkElement Row(TeamField field, bool edited, IReadOnlyList<int> teamColours)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var label = new TextBlock
        {
            Text = field.Label,
            Width = 190,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = Tip(field),
        };
        var mark = new TextBlock { Text = edited ? "●" : "", Width = 14, Foreground = (Brush)FindResource("AccentBrush"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(mark, Dock.Left);
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(mark);
        row.Children.Add(label);
        row.Children.Add(Editor(field, teamColours));
        return row;
    }

    private static string Tip(TeamField field)
    {
        var parts = new List<string> { field.Key };
        if (field.Hint != null)
            parts.Add(field.Hint);
        parts.Add(field.Kind switch
        {
            TeamFieldKind.Text => $"Up to {field.TextCapacity} bytes (letters like ® take two or three).",
            TeamFieldKind.Colour => "A colour as RRGGBB.",
            TeamFieldKind.Flag => "Yes or no.",
            _ => $"0 to {field.Max:N0}.",
        });
        return string.Join("\n", parts);
    }

    private FrameworkElement Editor(TeamField field, IReadOnlyList<int> teamColours)
    {
        if (field.Field.Label == "index" || field.Field.IsPoolPointer)
            return new TextBlock { Text = TeamDetails.Format(field), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("Dim") };

        switch (field.Kind)
        {
            case TeamFieldKind.Flag:
            {
                var box = new CheckBox { IsChecked = Convert.ToInt64(field.Value) != 0, VerticalAlignment = VerticalAlignment.Center };
                box.Click += (_, _) => Commit(field, box.IsChecked == true ? 1 : 0);
                return box;
            }
            case TeamFieldKind.Team:
            {
                var items = _teams.OrderBy(t => t.Index).Select(t => new ComboBoxItem { Content = $"{t.Index}  {t.Name}", Tag = t.Index }).ToList();
                var current = Convert.ToInt32(field.Value);
                if (items.All(i => (int)i.Tag != current))
                    items.Insert(0, new ComboBoxItem { Content = $"{current}  (no team)", Tag = current });
                var combo = new ComboBox { ItemsSource = items, FontSize = 12, MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left, IsTextSearchEnabled = true };
                combo.SelectedItem = items.First(i => (int)i.Tag == current);
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem { Tag: int index } && index != current)
                        Commit(field, (long)index);
                };
                return combo;
            }
            case TeamFieldKind.GoalHorn:
            {
                var current = Convert.ToInt64(field.Value);
                var items = GoalHorns.All.Select(h => new ComboBoxItem { Content = $"{h}  ({h.Kind})", Tag = (long)h.Value }).ToList();
                if (items.All(i => (long)i.Tag != current))
                    items.Insert(0, new ComboBoxItem { Content = GoalHorns.Describe(current), Tag = current });
                var combo = new ComboBox { ItemsSource = items, FontSize = 12, MaxWidth = 340, HorizontalAlignment = HorizontalAlignment.Left, IsTextSearchEnabled = true };
                combo.SelectedItem = items.First(i => (long)i.Tag == current);
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem { Tag: long value } && value != current)
                        Commit(field, value);
                };
                return HornSoundRow(combo, current);
            }
            case TeamFieldKind.Colour:
                return ColourEditor(field, teamColours);
            default:
            {
                var box = new TextBox
                {
                    Text = TeamDetails.Format(field),
                    FontSize = 12,
                    Width = field.Kind == TeamFieldKind.Text ? 260 : 110,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                CommitOnEnter(box, field);
                return box;
            }
        }
    }

    private readonly MediaPlayer _player = new();

    /// <summary>
    /// The horn picker, then the bank sample that horn plays (filled in where it is known), with play and replace. A
    /// replacement goes straight into the project's horn bank; the horn choice itself is saved with the other details.
    /// </summary>
    private FrameworkElement HornSoundRow(ComboBox combo, long horn)
    {
        var panel = new StackPanel();
        panel.Children.Add(combo);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var sampleBox = new TextBox { Width = 50, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "The hornbank.sbr sample this horn plays. Known ones are filled in; for others, find the number with the spoken test bank (blueline horn-label) and type it here." };
        var play = new Button { Content = "Play", FontSize = 12, Margin = new Thickness(6, 0, 0, 0) };
        var replace = new Button { Content = "Replace sound…", FontSize = 12, Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Any audio FFmpeg reads (WAV, MP3, OGG, FLAC...). It is encoded to EA Opus and must fit this sample's slot; every arena playing this horn hears it." };
        row.Children.Add(new TextBlock { Text = "Sound sample", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        row.Children.Add(sampleBox);
        row.Children.Add(play);
        row.Children.Add(replace);
        panel.Children.Add(row);
        var note = Dim("");
        panel.Children.Add(note);
        void Show(long h)
        {
            var sample = GoalHorns.SampleOf(h);
            sampleBox.Text = sample?.ToString() ?? "";
            note.Text = sample == null ? "This horn has no sample in the bank."
                : GoalHorns.IsSampleConfirmed(h) ? ""
                : "Likely sample, not yet heard in game. A club horn may also layer other sounds.";
            note.Visibility = note.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        Show(horn);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: long h }) Show(h); };

        int? Sample() => int.TryParse(sampleBox.Text.Trim(), out var n) && n >= 0 ? n : null;
        play.Click += (_, _) => PlayHorn(Sample());
        replace.Click += (_, _) => ReplaceHorn(Sample());
        return panel;
    }

    private Core.OpusTool? Opus()
    {
        if (_services.Ffmpeg is { } ffmpeg)
            return new Core.OpusTool(ffmpeg);
        _status.Text = "Horn sounds need FFmpeg (a full build). Choose it in Settings.";
        return null;
    }

    private void PlayHorn(int? sample)
    {
        if (sample is not { } index)
        {
            _status.Text = "Type the horn's sample number first.";
            return;
        }
        if (Opus() is not { } tool)
            return;
        var workspace = _services.Workspace;
        _ = _services.Jobs.RunAsync($"Decoding horn sample {index}", async context =>
        {
            var bank = workspace.ReadCurrent(GoalHorns.BankPath) ?? throw new InvalidOperationException("The game has no horn bank.");
            var samples = HornBank.Samples(bank);
            if (index >= samples.Count)
                throw new InvalidOperationException($"The horn bank has samples 0 to {samples.Count - 1}.");
            var target = Path.Combine(Path.GetTempPath(), "Blueline", $"horn-{Guid.NewGuid():N}.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await tool.DecodeAsync(HornBank.Read(bank, samples[index]), target, context.Cancel);
            return target;
        }, wav =>
        {
            _player.Close();
            _player.Open(new Uri(wav));
            _player.Play();
        });
    }

    private void ReplaceHorn(int? sample)
    {
        if (sample is not { } index)
        {
            _status.Text = "Type the horn's sample number first.";
            return;
        }
        if (Opus() is not { } tool || _services.RequireProject() is not { } project)
            return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose the horn's new sound", Filter = "Audio|*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.aac;*.opus|All files|*.*" };
        if (dialog.ShowDialog() != true)
            return;
        var audio = dialog.FileName;
        var workspace = _services.Workspace;
        _ = _services.Jobs.RunAsync($"Encoding horn sample {index}", async context =>
        {
            var bank = workspace.ReadCurrent(GoalHorns.BankPath) ?? throw new InvalidOperationException("The game has no horn bank.");
            return await HornBank.ReplaceAsync(tool, bank, index, audio, new Progress<string>(context.Status), context.Cancel);
        }, result =>
        {
            project.Set(GoalHorns.BankPath, result.Bank, $"Horn sample {index} from {Path.GetFileName(audio)}");
            _services.Notify($"Horn sample {index} now plays {Path.GetFileName(audio)}: {result.Encoded.Seconds:F1} s at {result.Encoded.Kilobits} kbit/s ({result.Encoded.Sample.Length:N0} of {result.Sample.Length:N0} bytes).");
        });
    }

    private FrameworkElement ColourEditor(TeamField field, IReadOnlyList<int> teamColours)
    {
        var rgb = Convert.ToInt32(field.Value);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var swatch = Swatch(rgb, 20);
        swatch.Cursor = Cursors.Hand;
        swatch.ToolTip = "Choose from this team's colours and common ones";
        var box = new TextBox { Text = GearColours.Hex(rgb), Width = 70, FontSize = 12, Margin = new Thickness(6, 0, 0, 0) };
        CommitOnEnter(box, field);

        var choices = new WrapPanel { Width = 8 * 26, Margin = new Thickness(6) };
        var popup = new Popup
        {
            PlacementTarget = swatch,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = (Brush)FindResource("PanelBrush"),
                BorderBrush = (Brush)FindResource("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = choices,
            },
        };
        void AddChoice(int colour, string tip)
        {
            var chip = Swatch(colour, 20);
            chip.Margin = new Thickness(3);
            chip.Cursor = Cursors.Hand;
            chip.ToolTip = $"{tip} #{GearColours.Hex(colour)}";
            chip.MouseLeftButtonUp += (_, _) =>
            {
                popup.IsOpen = false;
                Commit(field, colour);
            };
            choices.Children.Add(chip);
        }
        foreach (var colour in teamColours)
            AddChoice(colour, "This team's");
        foreach (var colour in CommonColours.Except(teamColours))
            AddChoice(colour, "Common");
        swatch.MouseLeftButtonUp += (_, _) => popup.IsOpen = true;

        panel.Children.Add(swatch);
        panel.Children.Add(box);
        panel.Children.Add(popup);
        return panel;
    }

    private void CommitOnEnter(TextBox box, TeamField field)
    {
        var original = TeamDetails.Format(field);
        void Commit()
        {
            if (box.Text == original || (field.Kind == TeamFieldKind.Colour && box.Text.Trim().TrimStart('#').Equals(original, StringComparison.OrdinalIgnoreCase)))
                return;
            object value;
            try
            {
                value = TeamDetails.Parse(field, box.Text);
            }
            catch (FormatException e)
            {
                _status.Text = e.Message;
                box.Text = original;
                return;
            }
            if (!this.Commit(field, value))
                box.Text = original;
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Commit();
            else if (e.Key == Key.Escape)
                box.Text = original;
        };
    }

    private bool Commit(TeamField field, object value)
    {
        if (_details == null)
            return false;
        try
        {
            var changed = _details.Set(field, value);
            _status.Text = $"{field.Group}: {field.Label} is now {TeamDetails.Format(changed)}. Not saved yet.";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            _status.Text = e.Message;
            return false;
        }
        SetDirty();
        // Rebuild after the current input event, so focus moving to the next box is not lost mid-change.
        Dispatcher.BeginInvoke(Build, System.Windows.Threading.DispatcherPriority.Background);
        return true;
    }

    // ---- Saving

    public void SaveToProject()
    {
        if (_details == null || _team == null || !IsDirty || _services.RequireProject() is not { } project)
            return;
        byte[] bytes;
        try
        {
            // Replay onto the project's current copy, so edits saved elsewhere since this opened (glove and helmet colours,
            // the Database tab) stay.
            var current = Read() ?? throw new InvalidDataException("The game database could not be read.");
            TeamDetails.Replay(current, _details.Edits);
            bytes = current.Save();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or InvalidOperationException)
        {
            _services.ReportError("The team details were not saved", e);
            return;
        }
        var count = _details.Edits.Count;
        project.Set(TeamCatalog.DatabasePath, bytes, $"{_team.Name}: {count} team detail{(count == 1 ? "" : "s")}");
        Load();
        _status.Text = $"Saved {count} change{(count == 1 ? "" : "s")} to the project. Build the project to try it in game.";
        Saved?.Invoke();
    }

    private void SetDirty()
    {
        _save.IsEnabled = _discard.IsEnabled = IsDirty;
        _save.Content = IsDirty ? $"Save {_details!.Edits.Count} change{(_details.Edits.Count == 1 ? "" : "s")} to project" : "Save to project";
    }

    private TextBlock Dim(string text) => new() { Text = text, Style = (Style)FindResource("Dim"), TextWrapping = TextWrapping.Wrap };

    private TextBlock Note(string text)
    {
        var note = Dim(text);
        note.FontSize = 11.5;
        note.Margin = new Thickness(0, 0, 0, 6);
        return note;
    }

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
