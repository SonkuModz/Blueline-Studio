using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Blueline.App.Controls;
using Blueline.App.Modules;
using Blueline.Core;

namespace Blueline.App.Shell;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly List<IEditorModule> _modules;
    private readonly Dictionary<string, RadioButton> _tabs = [];
    private IEditorModule? _current;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        IEditorModule[] all =
        [
            new HomeModule(services),
            new AssetsModule(services),
            new TexturesModule(services),
            new TeamsModule(services),
            new JerseyMapsModule(services),
            new ModelsModule(services),
            new TextModule(services),
            new DatabaseModule(services),
            new AudioModule(services),
            new VideoModule(services),
            new OverlaysModule(services),
            new BuildModule(services),
            new PlayModule(services),
        ];

        // The tabs sit in the order of the groups, so moving along the bar and the direction pages slide always agree.
        _modules = [.. TabGroups.SelectMany(g => g.Titles).Select(title => all.First(m => m.Title == title))];
        BuildTabs();
        BuildShortcuts();
        Palette.Source = PaletteItems;
        Tabs.SizeChanged += (_, _) => RefreshIndicator();
        SizeChanged += (_, _) => ShowGroupLabels();

        services.Workspace.Changed += () => Dispatcher.BeginInvoke(RefreshProjectState);
        services.Jobs.Updated += OnJobUpdated;
        services.Jobs.Failed += (title, e) => ShowBanner($"{title} failed", e.Message, true);
        services.ErrorReported += (title, message, _) => Dispatcher.BeginInvoke(() => ShowBanner(title, message, true));
        services.Notified += message => Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = message;
            if (ToastsEnabled)
                Toasts.Show(message);
        });
        services.NavigationRequested += (tab, path) => Dispatcher.BeginInvoke(() => Navigate(tab, path));
        services.Log.Added += entry => Dispatcher.BeginInvoke(() =>
            LogList.Items.Insert(0, $"{entry.When:HH:mm:ss}  {entry.Level,-7}  {entry.Message}"));

        WireStartScreen();

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => OnUndo(this, new RoutedEventArgs())));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, _) => OnRedo(this, new RoutedEventArgs())));
        SourceInitialized += (_, _) => UseTitleBar();
        ThemeManager.Changed += UseTitleBar;
        ThemeManager.Changed += ShowThemeButton;
        ShowThemeButton();
        RefreshProjectState();
    }

    public AppServices Services => _services;

    public StartScreen StartScreenView => Start;

    /// <summary>Turned off by the screenshot tour so messages do not drift into unrelated pictures.</summary>
    public bool ToastsEnabled { get; set; } = true;

    public ToastHost ToastView => Toasts;

    // Tab bar

    /// <summary>The tabs in order, grouped by what you do with them. Settings stands alone at the end.</summary>
    private static readonly (string? Label, string[] Titles)[] TabGroups =
    [
        ("LIBRARY", ["Assets", "Textures", "Text", "Database", "Audio", "Video"]),
        ("CREATE", ["Teams", "Jersey Maps", "Models", "Overlays"]),
        ("SHIP", ["Build", "Play"]),
        (null, ["Settings"]),
    ];

    /// <summary>Segoe Fluent Icons glyph for each tab.</summary>
    private static readonly Dictionary<string, string> TabIcons = new()
    {
        ["Assets"] = "",
        ["Textures"] = "",
        ["Text"] = "",
        ["Database"] = "",
        ["Audio"] = "",
        ["Video"] = "",
        ["Teams"] = "",
        ["Jersey Maps"] = "",
        ["Models"] = "",
        ["Overlays"] = "",
        ["Build"] = "",
        ["Play"] = "",
        ["Settings"] = "",
    };

    private readonly List<TextBlock> _groupLabels = [];
    private readonly List<System.Windows.Media.Animation.AnimationClock> _indicatorClocks = [];
    private RadioButton? _activeTab;

    private void BuildTabs()
    {
        var first = true;
        foreach (var (label, titles) in TabGroups)
        {
            if (!first)
            {
                var divider = new System.Windows.Shapes.Rectangle { Width = 1, Height = 16, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                divider.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "LineBrush");
                Tabs.Children.Add(divider);
            }
            first = false;

            if (label != null)
            {
                var text = new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Visibility = Visibility.Collapsed };
                text.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");
                _groupLabels.Add(text);
                Tabs.Children.Add(text);
            }

            foreach (var title in titles)
            {
                var module = _modules.First(m => m.Title == title);
                var number = _modules.IndexOf(module) + 1;
                var icon = new TextBlock
                {
                    Text = TabIcons.GetValueOrDefault(title, ""),
                    FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 14,
                    Margin = new Thickness(0, 1, 7, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var caption = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
                var tab = new RadioButton
                {
                    Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, caption } },
                    Style = (Style)FindResource("NavTab"),
                    Tag = module,
                    ToolTip = number <= 9 ? $"{title}  (Ctrl+{number})" : title,
                };
                tab.Checked += (_, _) =>
                {
                    _activeTab = tab;
                    UpdateTabWords();
                    Tabs.UpdateLayout(); // the line must measure the tab at its new width
                    Show(module);
                    MoveIndicator(tab, animate: true);
                };
                _tabs[title] = tab;
                Tabs.Children.Add(tab);
            }
        }
    }

    /// <summary>Ctrl+1 to Ctrl+9 jump to the first nine tabs in the order they sit in the bar, and Ctrl+, opens Settings.</summary>
    private void BuildShortcuts()
    {
        for (var i = 0; i < Math.Min(9, _modules.Count); i++)
        {
            var module = _modules[i];
            var command = new RoutedCommand();
            CommandBindings.Add(new CommandBinding(command, (_, _) =>
            {
                if (!Start.IsOpen)
                    Navigate(module.Title, null);
            }));
            InputBindings.Add(new KeyBinding(command, Key.D1 + i, ModifierKeys.Control));
        }

        // Ctrl+K opens the palette.
        var palette = new RoutedCommand();
        CommandBindings.Add(new CommandBinding(palette, (_, _) => OpenPalette()));
        InputBindings.Add(new KeyBinding(palette, Key.K, ModifierKeys.Control));

        // Ctrl+, opens Settings, as in most apps.
        var settings = new RoutedCommand();
        CommandBindings.Add(new CommandBinding(settings, (_, _) =>
        {
            if (!Start.IsOpen)
                Navigate("Settings", null);
        }));
        InputBindings.Add(new KeyBinding(settings, Key.OemComma, ModifierKeys.Control));
    }

    /// <summary>The group names only show when the window is wide enough for them not to crowd the tabs.</summary>
    private void ShowGroupLabels()
    {
        var show = ActualWidth >= 2050;
        foreach (var label in _groupLabels)
            label.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdateTabWords();
    }

    /// <summary>
    /// Wide windows name every tab. Narrower ones name only the tab you are on, so all thirteen icons still fit;
    /// tooltips carry the rest of the names.
    /// </summary>
    private void UpdateTabWords()
    {
        var all = ActualWidth >= 1900;
        foreach (var tab in _tabs.Values)
        {
            if (tab.Content is not StackPanel { Children: [TextBlock glyph, TextBlock caption] })
                continue;
            var named = all || tab == _activeTab;
            caption.Visibility = named ? Visibility.Visible : Visibility.Collapsed;
            glyph.Margin = new Thickness(0, 1, named ? 7 : 0, 0);
        }
    }

    private void RefreshIndicator()
    {
        if (_activeTab != null)
            MoveIndicator(_activeTab, animate: false);
    }

    /// <summary>Slides the blue line under <paramref name="tab"/>, or puts it there at once.</summary>
    private void MoveIndicator(RadioButton tab, bool animate)
    {
        if (!IsLoaded || !tab.IsLoaded || tab.ActualWidth <= 0)
        {
            // The bar has not been laid out yet. Try again once it has.
            Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_activeTab, tab))
                    MoveIndicator(tab, animate: false);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        var x = tab.TranslatePoint(new Point(0, 0), TabArea).X;
        var width = tab.ActualWidth;
        var slide = (System.Windows.Media.TranslateTransform)Indicator.RenderTransform;
        var fromX = slide.X;
        var fromWidth = Indicator.Width;
        _indicatorClocks.Clear();
        slide.ApplyAnimationClock(System.Windows.Media.TranslateTransform.XProperty, null);
        Indicator.ApplyAnimationClock(WidthProperty, null);

        if (!animate || !Motion.Enabled || fromWidth <= 0)
        {
            slide.X = x;
            Indicator.Width = width;
            return;
        }

        var move = new System.Windows.Media.Animation.DoubleAnimation(fromX, x, Motion.Of(Motion.Page)) { EasingFunction = Motion.Out }.CreateClock();
        var grow = new System.Windows.Media.Animation.DoubleAnimation(fromWidth, width, Motion.Of(Motion.Page)) { EasingFunction = Motion.Out }.CreateClock();
        _indicatorClocks.Add(move);
        _indicatorClocks.Add(grow);
        move.Completed += (_, _) =>
        {
            if (!_indicatorClocks.Contains(move))
                return;
            slide.ApplyAnimationClock(System.Windows.Media.TranslateTransform.XProperty, null);
            slide.X = x;
        };
        grow.Completed += (_, _) =>
        {
            if (!_indicatorClocks.Contains(grow))
                return;
            Indicator.ApplyAnimationClock(WidthProperty, null);
            Indicator.Width = width;
        };
        slide.ApplyAnimationClock(System.Windows.Media.TranslateTransform.XProperty, move);
        Indicator.ApplyAnimationClock(WidthProperty, grow);
    }

    private void WireStartScreen()
    {
        Start.Prepare(_services);
        _services.Workspace.Changed += () => Dispatcher.BeginInvoke(Start.RefreshGame);
        _services.StartBackgroundChanged += () => Dispatcher.BeginInvoke(Start.ApplyBackground);

        Start.ProjectChosen += folder =>
        {
            _services.OpenProject(folder);
            _ = EnterWorkspace();
        };
        Start.OpenProjectRequested += () =>
        {
            if (OpenProjectFlow())
                _ = EnterWorkspace();
        };
        Start.NewProjectRequested += () =>
        {
            if (NewProjectFlow())
                _ = EnterWorkspace();
        };
        Start.PuckPeakRequested += async () =>
        {
            if (await PuckPeakFlow())
                _ = EnterWorkspace();
        };
        Start.BrowseRequested += () => _ = EnterWorkspace();
        Start.ChooseGameRequested += () =>
        {
            if (AppServices.PickFolder("Choose the NHL 19 folder that holds eboot.bin") is { } folder)
                _ = _modules.OfType<HomeModule>().First().MountAsync(folder);
        };
    }

    /// <summary>Brings the start screen back, from the logo in the top bar.</summary>
    public void ShowStart() => Start.Open();

    private void OnShowStart(object sender, RoutedEventArgs e) => ShowStart();

    private Task EnterWorkspace() => Start.CloseAsync();

    /// <summary>True while a page is still sliding, so a screenshot or a test can wait for it.</summary>
    public bool IsPageMoving => Host.IsMoving || _indicatorClocks.Any(c => c.CurrentState == System.Windows.Media.Animation.ClockState.Active);

    /// <summary>
    /// Changes the theme. With <paramref name="animate"/> a picture of the old look is laid over the window and faded away, so
    /// the whole app seems to change colour smoothly.
    /// </summary>
    public void SwitchTheme(Blueline.Core.UiPalette palette, bool animate = true, bool save = true)
    {
        if (ReferenceEquals(palette, ThemeManager.Current))
            return;

        Image? snapshot = null;
        if (animate && Motion.Enabled && Root.ActualWidth > 0 && Root.ActualHeight > 0)
        {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Root.ActualWidth, (int)Root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(Root);
            snapshot = new Image { Source = bitmap, IsHitTestVisible = false, Stretch = System.Windows.Media.Stretch.Fill };
            Grid.SetRowSpan(snapshot, 5);
            Root.Children.Add(snapshot);
        }

        ThemeManager.Apply(palette);
        if (save)
        {
            _services.Settings.Theme = palette.Name;
            _services.Settings.Save();
        }

        if (snapshot == null)
            return;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, Motion.Of(Motion.Slow)) { EasingFunction = Motion.InOut };
        fade.Completed += (_, _) => Root.Children.Remove(snapshot);
        snapshot.BeginAnimation(OpacityProperty, fade);
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e) =>
        SwitchTheme(ThemeManager.IsDark ? Blueline.Core.UiPalette.Light : Blueline.Core.UiPalette.Dark);

    /// <summary>Shows the icon of the theme you would switch to: a sun while it is dark, a moon while it is light.</summary>
    private void ShowThemeButton()
    {
        // A TextBlock of its own, because the theme's text style would otherwise swap the icon font for the normal one.
        var glyph = new TextBlock { Text = ThemeManager.IsDark ? "" : "", FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15 };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");
        ThemeButton.Content = glyph;
        ThemeButton.ToolTip = ThemeManager.IsDark ? "Switch to the light theme" : "Switch to the dark theme";
    }

    public void PausePageMovement()
    {
        Host.PauseMovement();
        foreach (var clock in _indicatorClocks)
            clock.Controller?.Pause();
    }

    public void SeekPageMovement(double milliseconds)
    {
        Host.SeekMovement(milliseconds);
        foreach (var clock in _indicatorClocks)
            clock.Controller?.Seek(TimeSpan.FromMilliseconds(milliseconds), System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
    }

    public void ResumePageMovement()
    {
        Host.ResumeMovement();
        foreach (var clock in _indicatorClocks)
            clock.Controller?.Resume();
    }
    public IReadOnlyList<IEditorModule> Modules => _modules;

    /// <summary>Asks Windows for a title bar that matches the theme.</summary>
    private void UseTitleBar()
    {
        const int ImmersiveDarkMode = 20;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        var enabled = ThemeManager.IsDark ? 1 : 0;
        DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public void Begin(StartOptions options)
    {
        Navigate("Assets", null);
        var home = _modules.OfType<HomeModule>().First();

        // The screenshot tour goes straight to the workspace. Everyone else starts on the start screen, with the game loading behind it.
        if (options.CaptureFolder == null)
            Start.Open();
        var game = options.Game ?? _services.Settings.GameFolder;
        if (options.Project != null)
            _services.OpenProject(options.Project);
        if (options.Emulator != null)
        {
            _services.Settings.EmulatorPath = options.Emulator;
            _services.Settings.Save();
        }

        Func<Task> after = options.CaptureFolder != null
            ? () => new CaptureTour(this, options.CaptureLaunch).RunAsync(options.CaptureFolder)
            : () => Task.CompletedTask;
        _ = game != null ? home.MountAsync(game, after) : after();
    }

    public void Navigate(string title, string? path)
    {
        if (!_tabs.TryGetValue(title, out var tab))
            return;
        tab.IsChecked = true;
        if (path != null)
            ((IEditorModule)tab.Tag).OpenPath(path);
    }

    private void Show(IEditorModule module)
    {
        // Moving to a tab further along slides pages one way, moving back slides them the other.
        var direction = _current == null ? 0 : Math.Sign(_modules.IndexOf(module) - _modules.IndexOf(_current));
        _current = module;
        Host.Show(module.View, direction);
        module.Activated();
    }

    private void RefreshProjectState()
    {
        var project = _services.Workspace.Project;
        ProjectLabel.Text = project == null ? "No project open"
            : $"{project.Name}  ·  {project.ChangeCount} changed file{(project.ChangeCount == 1 ? "" : "s")}";
        UndoButton.IsEnabled = project?.CanUndo == true;
        RedoButton.IsEnabled = project?.CanRedo == true;
        UndoButton.ToolTip = project?.UndoLabel is { } undo ? $"Undo: {undo}" : null;
        RedoButton.ToolTip = project?.RedoLabel is { } redo ? $"Redo: {redo}" : null;

        // The dot in the chip: blue while there is a project, grey when there is none.
        ProjectDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, project == null ? "DimBrush" : "AccentTextBrush");
    }

    // Ctrl+K palette

    public CommandPalette PaletteView => Palette;

    /// <summary>The title of the tab that is showing.</summary>
    public string CurrentTabTitle => _current?.Title ?? "";

    public void OpenPalette()
    {
        if (!Start.IsOpen)
            Palette.Open();
    }

    private void OnSearch(object sender, RoutedEventArgs e) => OpenPalette();

    /// <summary>The tab a game file opens in, by what kind of file it is.</summary>
    private static (string Tab, string Kind) HomeFor(AssetCategory category) => category switch
    {
        AssetCategory.Texture => ("Textures", "TEXTURE"),
        AssetCategory.Model => ("Models", "MODEL"),
        AssetCategory.Audio => ("Audio", "AUDIO"),
        AssetCategory.Video => ("Video", "VIDEO"),
        AssetCategory.Database => ("Database", "DATA"),
        AssetCategory.Text => ("Text", "TEXT"),
        _ => ("Assets", "FILE"),
    };

    /// <summary>What the palette lists for what was typed: tabs and actions first, then matching game files.</summary>
    private IReadOnlyList<PaletteItem> PaletteItems(string query)
    {
        var found = new List<(PaletteItem Item, int Score)>();

        void Add(string kind, string title, string? detail, Action run, bool enabled = true)
        {
            if (!enabled)
                return;
            var score = Math.Max(PaletteSearch.Score(query, title), PaletteSearch.Score(query, detail ?? "") / 2);
            if (score > 0)
                found.Add((new PaletteItem(kind, title, detail, run), score));
        }

        foreach (var (label, titles) in TabGroups)
            foreach (var title in titles)
            {
                var tab = title;
                var group = label == null ? null : char.ToUpperInvariant(label[0]) + label[1..].ToLowerInvariant();
                Add("TAB", $"Go to {title}", group, () => Navigate(tab, null));
            }

        var project = _services.Workspace.Project;
        Add("ACTION", ThemeManager.IsDark ? "Switch to the light theme" : "Switch to the dark theme", "Appearance",
            () => SwitchTheme(ThemeManager.IsDark ? Blueline.Core.UiPalette.Light : Blueline.Core.UiPalette.Dark));
        Add("ACTION", Motion.Reduce ? "Turn motion back on" : "Reduce motion", "Appearance", () =>
        {
            Motion.Reduce = !Motion.Reduce;
            _services.Settings.ReduceMotion = Motion.Reduce;
            _services.Settings.Save();
            _services.Notify(Motion.Reduce ? "Motion reduced" : "Motion on");
        });
        Add("ACTION", "New project", "Project", () => NewProjectFlow());
        Add("ACTION", "Open project", "Project", () => OpenProjectFlow());
        Add("ACTION", "Close project", project?.Name, () => OnMenuClose(this, new RoutedEventArgs()), project != null);
        Add("ACTION", "Show project folder", project?.Folder, () => OnMenuFolder(this, new RoutedEventArgs()), project != null);
        Add("ACTION", project?.UndoLabel is { } undo ? $"Undo: {undo}" : "Undo", "Project", () => OnUndo(this, new RoutedEventArgs()), project?.CanUndo == true);
        Add("ACTION", project?.RedoLabel is { } redo ? $"Redo: {redo}" : "Redo", "Project", () => OnRedo(this, new RoutedEventArgs()), project?.CanRedo == true);
        Add("ACTION", "Back to the start screen", null, ShowStart);
        Add("ACTION", "Show or hide the log", null, () => OnToggleLog(this, new RoutedEventArgs()));

        var items = found.OrderByDescending(f => f.Score).Take(10).Select(f => f.Item).ToList();

        // Game files, once there are at least two letters to go on. The first word narrows the 150,000 paths quickly.
        if (query.Length >= 2 && _services.Index is { } index)
        {
            var first = PaletteSearch.Words(query).FirstOrDefault();
            if (first != null)
            {
                var hits = new List<(AssetRow Row, int Score)>();
                foreach (var row in index.Rows)
                {
                    if (row.Path.IndexOf(first, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var score = PaletteSearch.Score(query, row.Path) + PaletteSearch.Score(query, row.Name);
                    if (score > 0)
                        hits.Add((row, score));
                    if (hits.Count >= 800)
                        break;
                }

                foreach (var (row, _) in hits.OrderByDescending(h => h.Score).ThenBy(h => h.Row.Path.Length).Take(8))
                {
                    var (tab, kind) = HomeFor(row.Category);
                    var path = row.Path;
                    items.Add(new PaletteItem(kind, row.Name, row.Folder, () => _services.Navigate(tab, path)));
                }
            }
        }
        return items;
    }

    // Project chip and its menu

    private bool OpenProjectFlow()
    {
        if (AppServices.PickFolder("Open a Blueline project folder") is not { } folder)
            return false;
        _services.OpenProject(folder);
        return _services.Workspace.Project != null;
    }

    private bool NewProjectFlow()
    {
        var dialog = new NewProjectDialog { Owner = this };
        return dialog.ShowDialog() == true && _services.CreateProject(dialog.ProjectFolder, dialog.ProjectName) != null;
    }

    public FrameworkElement ProjectMenuCardView => ProjectMenuCard;

    public void OpenProjectMenu()
    {
        RefreshProjectMenu();
        ProjectMenu.IsOpen = true;
    }

    public void CloseProjectMenu() => ProjectMenu.IsOpen = false;

    private void OnProjectChip(object sender, RoutedEventArgs e)
    {
        if (ProjectMenu.IsOpen)
        {
            ProjectMenu.IsOpen = false;
            return;
        }
        OpenProjectMenu();
    }

    private void RefreshProjectMenu()
    {
        var project = _services.Workspace.Project;
        var has = project != null;
        MenuTitle.Text = project?.Name ?? "No project open";
        MenuPath.Text = project?.Folder ?? "A project starts the first time you make an edit, or create one now.";
        MenuStats.Text = project == null ? "" : $"{project.ChangeCount} changed file{(project.ChangeCount == 1 ? "" : "s")}"
            + (project.Installed.Count > 0 ? $"  ·  {project.Installed.Count} archive{(project.Installed.Count == 1 ? "" : "s")} installed" : "");
        MenuStats.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

        var visible = has ? Visibility.Visible : Visibility.Collapsed;
        MenuRule1.Visibility = MenuRule2.Visibility = MenuUndo.Visibility = MenuRedo.Visibility = MenuFolder.Visibility = MenuClose.Visibility = visible;
        MenuUndo.Content = project?.UndoLabel is { } undo ? $"Undo: {undo}" : "Undo";
        MenuUndo.IsEnabled = project?.CanUndo == true;
        MenuRedo.Content = project?.RedoLabel is { } redo ? $"Redo: {redo}" : "Redo";
        MenuRedo.IsEnabled = project?.CanRedo == true;
        MenuPuckPeak.Visibility = _services.Workspace.Vfs is { } vfs && PuckPeakMod.Find(vfs.Install) != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMenuUndo(object sender, RoutedEventArgs e)
    {
        ProjectMenu.IsOpen = false;
        OnUndo(sender, e);
    }

    private void OnMenuRedo(object sender, RoutedEventArgs e)
    {
        ProjectMenu.IsOpen = false;
        OnRedo(sender, e);
    }

    private void OnMenuNew(object sender, RoutedEventArgs e)
    {
        ProjectMenu.IsOpen = false;
        NewProjectFlow();
    }

    private void OnMenuPuckPeak(object sender, RoutedEventArgs e)
    {
        ProjectMenu.IsOpen = false;
        _ = PuckPeakFlow();
    }

    /// <summary>
    /// Makes a new project from the Puck Peak Overtime mod beside the game: every file its manifest says it changed, copied
    /// from the mods folder, as one undo step.
    /// </summary>
    private async Task<bool> PuckPeakFlow()
    {
        if (_services.Workspace.Vfs is not { } vfs || PuckPeakMod.Find(vfs.Install) is not { } mod)
        {
            _services.Notify($"No Puck Peak Overtime mod was found beside the game ({PuckPeakMod.ManifestName} in the mods folder).");
            return false;
        }
        var dialog = new NewProjectDialog { Owner = this, InitialName = "Puck Peak Overtime" };
        if (dialog.ShowDialog() != true || _services.CreateProject(dialog.ProjectFolder, dialog.ProjectName) is not { } project)
            return false;
        PuckPeakImportResult? copied = null;
        await _services.Jobs.RunAsync($"Opening Puck Peak Overtime ({mod.FileCount:N0} files)", context =>
            mod.Copy(project, new Progress<(string Stage, int Done, int Total)>(p => context.Progress($"{p.Stage}: {p.Done:N0} of {p.Total:N0}", p.Done, p.Total)), context.Cancel),
            result => copied = result);
        if (copied == null)
            return true;
        project.AddStored(copied.Changes, "Open Puck Peak Overtime");
        foreach (var warning in copied.Warnings)
            _services.Log.Warning(warning);
        _services.Notify($"Opened Puck Peak Overtime: {copied.Files:N0} files ({copied.Bytes / 1048576.0:N0} MB)."
            + (copied.Warnings.Count > 0 ? " " + string.Join(" ", copied.Warnings) : ""));
        return true;
    }

    private void OnMenuOpen(object sender, RoutedEventArgs e)
    {
        ProjectMenu.IsOpen = false;
        OpenProjectFlow();
    }

    private void OnMenuFolder(object sender, RoutedEventArgs e)
    {
        ProjectMenu.IsOpen = false;
        if (_services.Workspace.Project is { } project && Directory.Exists(project.Folder))
            Process.Start(new ProcessStartInfo(project.Folder) { UseShellExecute = true });
    }

    private void OnMenuClose(object sender, RoutedEventArgs e)
    {
        ProjectMenu.IsOpen = false;
        _services.Workspace.SetProject(null);
        _services.Notify("Project closed");
    }

    private void OnMenuStart(object sender, RoutedEventArgs e)
    {
        ProjectMenu.IsOpen = false;
        ShowStart();
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
            return;
        var project = _services.Workspace.Project;
        if (project?.CanUndo == true)
        {
            var label = project.UndoLabel;
            project.Undo();
            _services.Notify($"Undid: {label}");
        }
    }

    private void OnRedo(object sender, RoutedEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
            return;
        var project = _services.Workspace.Project;
        if (project?.CanRedo == true)
        {
            var label = project.RedoLabel;
            project.Redo();
            _services.Notify($"Redid: {label}");
        }
    }

    private void OnJobUpdated(string? text, double? fraction)
    {
        var busy = text != null;
        Start.ShowProgress(text, fraction);
        JobBar.Visibility = CancelJob.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy)
        {
            StatusText.Text = "Ready";
            return;
        }
        StatusText.Text = text;
        JobBar.IsIndeterminate = fraction == null;
        JobBar.Value = (fraction ?? 0) * 100;
    }

    private void ShowBanner(string title, string message, bool isError)
    {
        BannerTitle.Text = title;
        BannerText.Text = message;
        BannerLog.Visibility = isError ? Visibility.Visible : Visibility.Collapsed;
        Banner.Visibility = Visibility.Visible;
    }

    private void OnDismissBanner(object sender, RoutedEventArgs e) => Banner.Visibility = Visibility.Collapsed;

    private void OnOpenLog(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_services.Log.FilePath) { UseShellExecute = true });

    private void OnToggleLog(object sender, RoutedEventArgs e) =>
        LogPanel.Visibility = LogPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void OnCancelJob(object sender, RoutedEventArgs e) => _services.Jobs.CancelCurrent();
}
