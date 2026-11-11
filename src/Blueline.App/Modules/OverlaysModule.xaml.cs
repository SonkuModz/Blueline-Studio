using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core;
using Blueline.Core.Overlays;
using Blueline.Core.Teams;
using Blueline.Formats.Scaleform;
using Blueline.Formats.Textures;
using Microsoft.Win32;

namespace Blueline.App.Modules;

/// <summary>Every on-screen overlay: its layout, textures, settings and source files, all editable in the project.</summary>
public partial class OverlaysModule : UserControl, IEditorModule
{
    private const int ThumbSize = 128;
    private const string LayoutNote = "Built from this file with the game's own textures, fonts, tints and blend modes on a 1920 by 1080 screen. Scores, names and team colours are sample data, and the game's code decides timing.";
    private const string MovieNote = "Drawn from the movie's own shapes and textures. The game can also run code inside a movie, which is not played here, and text is left out.";
    private const string MomentNote = "Steps and timing are copied from the game's score clock code. Team colours, scores, player names and messages are sample data, and the game adds a small random shift to one set of streaks during a goal.";
    private const double GameFramesPerSecond = 30;
    private readonly AppServices _services;
    private readonly ThumbnailLoader _thumbs;
    private OverlayCatalog? _catalog;
    private ICollectionView? _view;
    private GameOverlay? _overlay;
    private int _catalogRun;
    private int _layoutRun;
    private int _artRun;

    private bool _choosing;
    private bool _sameOverlay;
    private string? _layoutPath;
    private string _layoutOtherEdits = "";
    private byte[]? _definitionsSaved;
    private LayoutDocument? _layout;
    private byte[]? _layoutSaved;
    private readonly Dictionary<string, LayoutDocument?> _widgetLayouts = [];
    private WidgetRow? _rootRow;
    private WidgetRow? _row;
    private bool _treeBusy;
    private OverlaySample _sample = new(new SampleTeam(0, "AWY", 0x3050A0, 2), new SampleTeam(0, "HOM", 0xA03030, 3));
    private GfxTimeline? _timeline;
    private readonly Dictionary<int, OverlayPicture?> _moviePictures = [];
    private readonly System.Windows.Threading.DispatcherTimer _movieTimer = new();
    private bool _movieBusy;
    private ScoreClockScene? _scene;
    private OverlayTimeline? _moment;
    private readonly System.Diagnostics.Stopwatch _momentClock = new();
    private int _momentStartFrame;

    private JsonNode? _definitions;
    private JsonObject? _definition;
    private string? _filePath;

    public OverlaysModule(AppServices services)
    {
        _services = services;
        InitializeComponent();
        _thumbs = new ThumbnailLoader(services.Workspace, Dispatcher);
        Canvas.WidgetChosen += OnCanvasChosen;
        PreviewNote.Text = LayoutNote;
        _movieTimer.Tick += OnMovieTick;
        MomentBox.ItemsSource = ScoreClockScene.Choices;
        MomentBox.SelectedIndex = 0;
        Canvas.WidgetMoved += () =>
        {
            SaveLayout("Moved widget");
            ShowWidgetValues();
        };
        WidgetEditor.Edited += what =>
        {
            SaveLayout(what);
            Canvas.Redraw();
            RebuildMoment(keepFrame: true);
        };
        DefinitionEditor.Edited += SaveDefinitions;
        EntryEditor.Edited += what =>
        {
            SaveDefinitions(what);
            ShowEntries();
        };
        DropTarget.Attach(ArtCard, f => File.Exists(f) && ImageFiles.IsSupported(f), files => _ = ImportArt(files[0]), ArtCard);
        services.Workspace.Changed += () => Dispatcher.BeginInvoke(OnWorkspaceChanged);
        ShowPage("Layout");
    }

    public string Title => "Overlays";
    public FrameworkElement View => this;
    public OverlayCatalog? Catalog => _catalog;
    public OverlayCanvas CanvasControl => Canvas;
    public TexturePreview ArtPreviewControl => ArtPreview;
    public JsonPropertyEditor WidgetEditorControl => WidgetEditor;
    public JsonPropertyEditor DefinitionEditorControl => DefinitionEditor;
    public TextBox FileTextControl => FileText;
    public bool IsBusy { get; private set; }
    public bool ThumbsBusy => _thumbs.IsBusy;
    public int ArtShown => ArtList.Items.Count;
    public string? SelectedLayout => _layoutPath;
    public string FileProblemText => FileProblem.Text;
    public int MovieFrameCount => _timeline?.FrameCount ?? 0;

    public void SetCloseUp(bool closeUp) => ViewBox.SelectedIndex = closeUp ? 1 : 0;

    public void SetShowHidden(bool show)
    {
        ShowHiddenBox.IsChecked = show;
        OnShowHidden(this, new RoutedEventArgs());
    }

    public void SetMovieFrame(int frame) => MovieSlider.Value = frame;

    public int MomentFrameCount => _moment?.FrameCount ?? 0;

    public int MomentFrame => (int)MovieSlider.Value;

    public bool MomentPlaying => _movieTimer.IsEnabled;

    public void PressPlay() => OnMoviePlay(this, new RoutedEventArgs());

    public void SetMoment(ScoreClockEvent moment) => MomentBox.SelectedItem = ScoreClockScene.Choices.First(c => c.Event == moment);

    public void SetTeams(string away, string home)
    {
        AwayBox.SelectedItem = AwayBox.Items.Cast<TeamInfo>().First(t => t.Name == away);
        HomeBox.SelectedItem = HomeBox.Items.Cast<TeamInfo>().First(t => t.Name == home);
    }

    public void ChooseLayout(string name) => LayoutBox.SelectedItem = LayoutBox.Items.Cast<OverlayFile>().First(f => f.Name == name);

    public void Activated()
    {
        if (_catalog == null)
            LoadCatalog();
    }

    public void OpenPath(string path)
    {
        if (_catalog?.Overlays.FirstOrDefault(o => o.Files.Any(f => f.Path == path) || o.Art.Any(a => a.Path == path)) is { } overlay)
            OverlayList.SelectedItem = overlay;
    }

    public void Select(string name)
    {
        OverlayList.SelectedItem = _catalog?.Overlays.First(o => o.Name == name);
        OverlayList.ScrollIntoView(OverlayList.SelectedItem);
    }

    public void SelectWidget(string name)
    {
        if (_rootRow?.Find(name) is { } row)
            Choose(row);
    }

    /// <summary>A row inside a collapsed branch has no tree item yet, so the page is updated here and not from the tree's event.</summary>
    private void Choose(WidgetRow row)
    {
        _row = row;
        row.Reveal();
        row.IsSelected = true;
        ShowWidget(row.Widget, updateCanvas: true);
    }

    public void ChooseArt(int index) => ArtList.SelectedIndex = index;

    public void ChooseFile(string name) => FileList.SelectedItem = FileList.Items.Cast<FileRow>().First(f => f.Name == name);

    public void SaveFileText() => OnFileSave(this, new RoutedEventArgs());

    private void LoadCatalog()
    {
        if (_services.Workspace.Vfs is not { } vfs)
        {
            CountText.Text = "No game loaded";
            return;
        }

        var run = ++_catalogRun;
        IsBusy = true;
        if (_catalog == null)
            LoadingText.Visibility = Visibility.Visible;
        var workspace = _services.Workspace;
        var paths = vfs.Files.Select(f => f.Path).ToList();
        var needTeams = AwayBox.Items.Count == 0;
        Task.Run(() => (OverlayCatalog.Build(workspace.ReadCurrent, paths), needTeams ? TeamCatalog.Load(workspace.ReadCurrent, paths).Teams : null)).ContinueWith(task =>
        {
            if (run != _catalogRun)
                return;
            LoadingText.Visibility = Visibility.Collapsed;
            IsBusy = false;
            if (task.IsFaulted)
            {
                _services.ReportError("Overlays could not be read", task.Exception!.GetBaseException());
                return;
            }
            if (task.Result.Item2 is { } teams)
                FillTeams(teams);
            ApplyCatalog(task.Result.Item1);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void FillTeams(IReadOnlyList<TeamInfo> teams)
    {
        const int Nhl = 0;
        var clubs = teams.Where(t => t.LeagueId == Nhl && !t.Name.Contains("All-Star", StringComparison.OrdinalIgnoreCase) && t.Colors.Count > 0).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        _choosing = true;
        AwayBox.ItemsSource = clubs;
        HomeBox.ItemsSource = clubs;
        AwayBox.SelectedItem = clubs.FirstOrDefault(t => t.Name.Contains("Canadiens")) ?? clubs.FirstOrDefault();
        HomeBox.SelectedItem = clubs.FirstOrDefault(t => t.Name.Contains("Maple Leafs")) ?? clubs.LastOrDefault();
        _choosing = false;
        ReadSample();
    }

    private void ReadSample()
    {
        const int AwayScore = 2, HomeScore = 3;
        static SampleTeam Team(TeamInfo info, int score) => new(info.ArtId, info.Abbreviation, (uint)info.Colors[0] & 0xFFFFFF, score, info.Colors.Count > 1 ? (uint)info.Colors[1] & 0xFFFFFF : 0xFFFFFF);
        if (AwayBox.SelectedItem is TeamInfo away && HomeBox.SelectedItem is TeamInfo home)
            _sample = new OverlaySample(Team(away, AwayScore), Team(home, HomeScore));
    }

    private void ApplyCatalog(OverlayCatalog catalog)
    {
        var keep = _overlay?.Name;
        _catalog = catalog;
        _view = CollectionViewSource.GetDefaultView(new ObservableCollection<GameOverlay>(catalog.Overlays));
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GameOverlay.Group)));
        ApplyFilter();
        OverlayList.ItemsSource = _view;
        CountText.Text = $"{catalog.Overlays.Count} overlays, {catalog.Overlays.SelectMany(o => o.Art).Select(a => a.Path).Distinct().Count():N0} textures";
        OverlayList.SelectedItem = catalog.Overlays.FirstOrDefault(o => o.Name == keep) ?? catalog.Overlays.FirstOrDefault(o => o.Name == "ScoreClock");
    }

    private void ApplyFilter()
    {
        var text = SearchBox.Text.Trim();
        if (_view != null)
            _view.Filter = item => text.Length == 0 || ((GameOverlay)item).Name.Contains(text, StringComparison.OrdinalIgnoreCase);
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnWorkspaceChanged()
    {
        if (_services.Workspace.Vfs == null)
            return;
        _thumbs.Invalidate();
        if (IsVisible || _catalog != null)
            LoadCatalog();
    }

    private void OnOverlayChosen(object sender, SelectionChangedEventArgs e)
    {
        var chosen = OverlayList.SelectedItem as GameOverlay;
        if (chosen == null)
            return;
        var same = chosen.Name == _overlay?.Name;
        _sameOverlay = same;
        _overlay = chosen;
        NameText.Text = chosen.Name;
        SummaryText.Text = Describe(chosen);

        var previousLayout = same ? _layoutPath : null;
        var layouts = chosen.Layouts.Concat(chosen.Movies).ToList();
        _choosing = true;
        LayoutBox.ItemsSource = layouts;
        NoLayoutText.Visibility = layouts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var pick = layouts.FirstOrDefault(l => l.Path == previousLayout) ?? layouts.FirstOrDefault();
        LayoutBox.SelectedItem = pick;
        _choosing = false;
        if (pick != null && LayoutMatches(pick.Path))
            RefreshLayoutLabels();
        else
            ShowLayout(pick);

        ShowArtList();
        ShowDefinitions();
        ShowFileList();
    }

    private static string Describe(GameOverlay overlay)
    {
        var parts = new List<string> { overlay.Summary, GameOverlay.Count(overlay.Files.Count, "file") };
        if (overlay.Videos.Count > 0)
            parts.Add(GameOverlay.Count(overlay.Videos.Count, "video"));
        if (overlay.Definitions.Count > 0)
            parts.Add("game name: " + string.Join(", ", overlay.Definitions));
        return string.Join(", ", parts);
    }

    private void OnPage(object sender, RoutedEventArgs e) => ShowPage((string)((ToggleButton)sender).Tag);

    public void ShowPage(string page)
    {
        LayoutPage.Visibility = page == "Layout" ? Visibility.Visible : Visibility.Collapsed;
        TexturesPage.Visibility = page == "Textures" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility = page == "Files" ? Visibility.Visible : Visibility.Collapsed;
        LayoutChip.IsChecked = page == "Layout";
        TexturesChip.IsChecked = page == "Textures";
        SettingsChip.IsChecked = page == "Settings";
        FilesChip.IsChecked = page == "Files";
    }

    private void Save(string path, byte[] content, string description)
    {
        if (_services.RequireProject() is { } project)
            project.Set(path, content, description);
    }

    private void Revert(string path, string label)
    {
        if (_services.Workspace.Project is { } project)
            project.Revert([path], label);
    }

    // Layout page

    private void OnLayoutChosen(object sender, SelectionChangedEventArgs e)
    {
        if (!_choosing)
            ShowLayout(LayoutBox.SelectedItem as OverlayFile);
    }

    /// <summary>True when what is on screen is still right, so a save does not reload the page and take focus from the box being typed in.</summary>
    private bool LayoutMatches(string path) =>
        _layoutPath == path && _layoutSaved != null && _layoutOtherEdits == OtherEdits(path)
        && _services.Workspace.ReadCurrent(path) is { } now && now.AsSpan().SequenceEqual(_layoutSaved);

    private string OtherEdits(string layoutPath)
    {
        var project = _services.Workspace.Project;
        return project == null ? "" : string.Join("|", project.Changes.Where(c => c.Path != layoutPath && c.Path != OverlayCatalog.DefinitionPath).Select(c => c.Path + c.Blob).Order(StringComparer.Ordinal));
    }

    private void RefreshLayoutLabels()
    {
        RevertLayout.IsEnabled = _layoutPath != null && _services.Workspace.IsEdited(_layoutPath);
    }

    private void ShowLayout(OverlayFile? file)
    {
        var run = ++_layoutRun;
        StopMovie();
        if (file == null || file.Kind == OverlayFileKind.Movie)
            ClearMoment();
        if (file?.Kind == OverlayFileKind.Movie)
        {
            ShowMovie(file, run);
            return;
        }

        SetMovieMode(false);
        if (file == null)
        {
            _layoutPath = null;
            _layout = null;
            _layoutSaved = null;
            if (_catalog != null)
                Canvas.Show(null, _catalog, new Dictionary<string, OverlayPicture?>(), new Dictionary<string, string>(), _sample, _ => null);
            WidgetTree.ItemsSource = null;
            WidgetEditor.Show(null);
            CopyWidget.IsEnabled = DeleteWidget.IsEnabled = RevertLayout.IsEnabled = false;
            return;
        }

        var catalog = _catalog!;
        var workspace = _services.Workspace;
        var previousSelection = _layoutPath == file.Path ? _row?.Widget.Name : null;
        var sample = _sample;
        IsBusy = true;
        Task.Run(() => PrepareLayout(file.Path, catalog, workspace, sample)).ContinueWith(task =>
        {
            if (run != _layoutRun)
                return;
            IsBusy = false;
            if (task.IsFaulted)
            {
                _services.ReportError("This layout could not be read", task.Exception!.GetBaseException());
                return;
            }

            var (document, saved, pictures, widgets, fonts) = task.Result;
            var first = _layoutPath != file.Path;
            _layoutPath = file.Path;
            _layout = document;
            _layoutSaved = saved;
            _layoutOtherEdits = OtherEdits(file.Path);
            _widgetLayouts.Clear();
            foreach (var (name, layout) in widgets)
                _widgetLayouts[name] = layout;
            Canvas.Pose = null;
            Canvas.Show(document, catalog, pictures, fonts, sample, name => _widgetLayouts.GetValueOrDefault(name));
            _scene = ScoreClockScene.Handles(file.Path) ? new ScoreClockScene(document, sample) : null;
            MomentPanel.Visibility = _scene != null ? Visibility.Visible : Visibility.Collapsed;
            RebuildMoment(keepFrame: !first);
            BuildTree(previousSelection);
            RefreshLayoutLabels();
            if (first)
                Dispatcher.BeginInvoke(FitCanvas, System.Windows.Threading.DispatcherPriority.Loaded);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static (LayoutDocument, byte[], Dictionary<string, OverlayPicture?>, Dictionary<string, LayoutDocument?>, Dictionary<string, string>) PrepareLayout(string path, OverlayCatalog catalog, Workspace workspace, OverlaySample sample)
    {
        var saved = workspace.ReadCurrent(path) ?? throw new InvalidDataException($"{path} is empty.");
        var document = LayoutDocument.Parse(saved);
        var pictures = new Dictionary<string, OverlayPicture?>();
        var widgets = new Dictionary<string, LayoutDocument?>();
        var fonts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Gather(LayoutWidget widget, int depth)
        {
            if (widget.Kind == "Asset" && widget.AssetType.Length > 0)
            {
                var key = OverlayCanvas.PictureKey(widget, sample);
                if (!pictures.ContainsKey(key))
                    pictures[key] = Decode(catalog.AssetPath(widget.AssetType, sample.AssetNumber(widget)), workspace);
                if (pictures[key] != null || !pictures.ContainsKey(widget.AssetType))
                    pictures[widget.AssetType] = pictures[key];

                // During a goal the game shows the scoring team's logo here, which can be either team's.
                if (widget.AssetType.Contains("team_logo", StringComparison.OrdinalIgnoreCase))
                    foreach (var team in new[] { sample.Away, sample.Home })
                        if (!pictures.ContainsKey($"{widget.AssetType}#{team.ArtId}"))
                            pictures[$"{widget.AssetType}#{team.ArtId}"] = Decode(catalog.AssetPath(widget.AssetType, team.ArtId), workspace);
            }
            if (widget.Kind == "Label")
            {
                var face = catalog.Look(widget).Face;
                if (!fonts.ContainsKey(face) && catalog.FontPath(face) is { } fontPath && workspace.ReadCurrent(fontPath) is { Length: > 0 } font)
                    fonts[face] = SaveFont(font);
            }

            if (depth < 12 && catalog.WidgetLayout(widget.Kind) is { } custom && !widgets.ContainsKey(widget.Kind) && workspace.ReadCurrent(custom) is { Length: > 0 } content)
            {
                var layout = LayoutDocument.Parse(content);
                widgets[widget.Kind] = layout;
                Gather(layout.Root, depth + 1);
            }
            foreach (var child in widget.Children)
                Gather(child, depth + 1);
        }

        Gather(document.Root, 0);
        return (document, saved, pictures, widgets, fonts);
    }

    /// <summary>WPF loads fonts from files, so the game's are saved to the temp folder under a name taken from their content.</summary>
    private static string SaveFont(byte[] font)
    {
        var folder = Path.Combine(Path.GetTempPath(), "BluelineStudio", "fonts");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(font))[..24] + ".ttf");
        if (!File.Exists(file))
            File.WriteAllBytes(file, font);
        return file;
    }

    private static OverlayPicture? Decode(string? path, Workspace workspace)
    {
        if (path == null || !path.EndsWith(".gnf", StringComparison.OrdinalIgnoreCase) || GameTexture.TryOpen(workspace.ReadCurrent(path) ?? []) is not { Blocker: null } texture)
            return null;
        return OverlayPicture.FromImage(texture.DecodeMip(0));
    }

    private void SetMovieMode(bool movie)
    {
        MovieBar.Visibility = MovieHint.Visibility = movie ? Visibility.Visible : Visibility.Collapsed;
        PreviewBar.Visibility = movie ? Visibility.Collapsed : Visibility.Visible;
        PreviewNote.Text = movie ? MovieNote : LayoutNote;
        if (!movie)
            _timeline = null;
    }

    private void ShowMovie(OverlayFile file, int run)
    {
        var workspace = _services.Workspace;
        var first = _layoutPath != file.Path;
        IsBusy = true;
        Task.Run(() =>
        {
            var content = workspace.ReadCurrent(file.Path) ?? throw new InvalidDataException($"{file.Path} is empty.");
            return (Content: content, Timeline: GfxTimeline.Parse(content));
        }).ContinueWith(task =>
        {
            if (run != _layoutRun)
                return;
            IsBusy = false;
            if (task.IsFaulted)
            {
                _services.ReportError("This movie could not be read", task.Exception!.GetBaseException());
                return;
            }

            _layoutPath = file.Path;
            _layout = null;
            _layoutSaved = task.Result.Content;
            _layoutOtherEdits = OtherEdits(file.Path);
            _timeline = task.Result.Timeline;
            _moviePictures.Clear();
            WidgetTree.ItemsSource = null;
            _rootRow = _row = null;
            ShowWidget(null);
            RefreshLayoutLabels();
            SetMovieMode(true);
            MovieSlider.Maximum = Math.Max(0, _timeline.FrameCount - 1);
            // Most movies animate in and then hold, so the last frame is the settled look.
            if (first)
                MovieSlider.Value = MovieSlider.Maximum;
            RenderMovieFrame();
            if (first)
                Dispatcher.BeginInvoke(FitCanvas, System.Windows.Threading.DispatcherPriority.Loaded);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void RenderMovieFrame()
    {
        if (_timeline is not { } timeline || _layoutPath is not { } moviePath || _catalog is not { } catalog)
            return;
        var frame = (int)MovieSlider.Value;
        MovieFrameText.Text = $"Frame {frame + 1} of {timeline.FrameCount}";
        var pieces = timeline.Frame(frame);
        var missing = pieces.Where(p => p.ImageId >= 0 && !_moviePictures.ContainsKey(p.ImageId)).Select(p => p.ImageId).Distinct().ToList();
        if (missing.Count == 0)
        {
            Canvas.ShowMovieFrame(timeline, pieces, _moviePictures);
            return;
        }

        var run = _layoutRun;
        var workspace = _services.Workspace;
        _movieBusy = true;
        IsBusy = true;
        Task.Run(() => missing.ToDictionary(id => id, id => timeline.Images.TryGetValue(id, out var name) ? Decode(catalog.MoviePicturePath(moviePath, name), workspace) : null)).ContinueWith(task =>
        {
            _movieBusy = false;
            IsBusy = false;
            if (run != _layoutRun)
                return;
            if (task.IsFaulted)
            {
                _services.ReportError("A texture of this movie could not be read", task.Exception!.GetBaseException());
                return;
            }
            foreach (var (id, picture) in task.Result)
                _moviePictures[id] = picture;
            RenderMovieFrame();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnMomentChosen(object sender, SelectionChangedEventArgs e) => RebuildMoment(keepFrame: false);

    private void OnFlatLook(object sender, RoutedEventArgs e) => _ = ApplyFlatLook();

    private void OnRestoreShading(object sender, RoutedEventArgs e) => RestoreShading();

    /// <summary>Replaces the score clock's shading textures with flat ones, as one undo step. The layout is left alone.</summary>
    public async Task ApplyFlatLook()
    {
        if (_catalog is not { } catalog || _services.RequireProject() is not { } project)
            return;
        var workspace = _services.Workspace;
        await _services.Jobs.RunAsync("Taking the shading out of the score clock", _ => FlatScorebug.Build(catalog, workspace.ReadCurrent), items =>
        {
            if (items.Count == 0)
            {
                _services.ReportError("Flat look not applied", "None of the score clock's textures were found in this game.");
                return;
            }
            project.SetMany(items, "Flat scorebug");
            _services.Notify($"Shading removed from {items.Count} textures. Restore shading puts the originals back.");
            ShowLayout(LayoutBox.SelectedItem as OverlayFile);
        });
    }

    public void RestoreShading()
    {
        if (_catalog is not { } catalog || _services.Workspace.Project is not { } project)
            return;
        var edited = FlatScorebug.Paths(catalog).Where(_services.Workspace.IsEdited).ToList();
        if (edited.Count == 0)
        {
            _services.Notify("The score clock's shading has not been changed in this project.");
            return;
        }
        project.Revert(edited, "Restored the score clock's shading");
        ShowLayout(LayoutBox.SelectedItem as OverlayFile);
    }

    private void OnSportsnetLook(object sender, RoutedEventArgs e) => _ = ApplySportsnetLook();

    /// <summary>Restyles the open score clock layout and puts the SN logo into the texture that carries it. Each is its own undo step.</summary>
    public async Task ApplySportsnetLook()
    {
        if (_layout == null || _layoutPath == null || _catalog == null || !ScoreClockScene.Handles(_layoutPath))
            return;
        if (SportsnetBug.IsApplied(_layout))
        {
            _services.Notify("This layout already has the Sportsnet look. Use Revert layout to start again.");
            return;
        }
        if (SportsnetBug.Problem(_layout) is { } problem)
        {
            _services.ReportError("Sportsnet look not applied", problem);
            return;
        }
        if (_services.RequireProject() is not { } project)
            return;

        var logoPath = _catalog.AssetPath(SportsnetBug.LogoAsset);
        var logoFile = Path.Combine(AppContext.BaseDirectory, "Assets", "sportsnet_logo.png");
        if (logoPath == null || !File.Exists(logoFile))
        {
            _services.ReportError("Sportsnet look not applied", logoPath == null ? "The texture for the SN logo was not found in this game." : "The SN logo image is missing from the app folder.");
            return;
        }

        var workspace = _services.Workspace;
        var image = ImageFiles.Load(logoFile);
        await _services.Jobs.RunAsync("Preparing the SN logo", _ =>
            TexturePipeline.Import(workspace.ReadCurrent(logoPath) ?? throw new InvalidDataException(logoPath + " could not be read."), image), result =>
        {
            if (_layout == null || SportsnetBug.IsApplied(_layout))
                return;
            SportsnetBug.Apply(_layout);
            SaveLayout("Sportsnet look");
            project.Set(logoPath, result.Gnf, "Overlay texture: SN logo for the Sportsnet look");
            _services.Notify("Sportsnet look applied. Revert layout restores the original.");

            // The new logo texture and the new parts have to be read again.
            ShowLayout(LayoutBox.SelectedItem as OverlayFile);
        });
    }

    /// <summary>Works out the chosen score clock moment again, for a new choice, new teams or an edited layout.</summary>
    private void RebuildMoment(bool keepFrame)
    {
        if (_scene == null)
            return;
        StopMovie();
        var choice = (MomentBox.SelectedItem as ScoreClockChoice)?.Event ?? ScoreClockEvent.InPlay;
        _moment = _scene.Build(choice);
        MovieBar.Visibility = _moment != null ? Visibility.Visible : Visibility.Collapsed;
        ShowHiddenBox.IsEnabled = _moment == null;
        PreviewNote.Text = _moment != null ? MomentNote : LayoutNote;
        if (_moment == null)
        {
            Canvas.Pose = null;
            return;
        }

        MovieSlider.Maximum = Math.Max(0, _moment.FrameCount - 1);
        if (!keepFrame)
            MovieSlider.Value = 0;
        RenderMoment();
    }

    private void ClearMoment()
    {
        _scene = null;
        _moment = null;
        Canvas.Pose = null;
        MomentPanel.Visibility = Visibility.Collapsed;
        ShowHiddenBox.IsEnabled = true;
    }

    private void RenderMoment()
    {
        if (_moment is not { } moment)
            return;
        var frame = (int)MovieSlider.Value;
        MovieFrameText.Text = $"Frame {frame + 1} of {moment.FrameCount}";
        Canvas.Pose = moment.At(OverlayScript.Frames(frame));
    }

    private void OnMovieFrame(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_moment != null)
            RenderMoment();
        else if (!_movieBusy)
            RenderMovieFrame();
    }

    private void OnMoviePlay(object sender, RoutedEventArgs e)
    {
        if (_movieTimer.IsEnabled)
        {
            StopMovie();
            return;
        }
        if (_moment is { FrameCount: > 1 })
        {
            if (MovieSlider.Value >= MovieSlider.Maximum)
                MovieSlider.Value = 0;
            _momentStartFrame = (int)MovieSlider.Value;
            _momentClock.Restart();
            _movieTimer.Interval = TimeSpan.FromSeconds(1.0 / GameFramesPerSecond);
            _movieTimer.Start();
            MoviePlay.Content = "Pause";
            return;
        }
        if (_timeline is not { FrameCount: > 1 } timeline)
            return;
        _movieTimer.Interval = TimeSpan.FromSeconds(1 / Math.Max(1, timeline.FrameRate));
        _movieTimer.Start();
        MoviePlay.Content = "Pause";
    }

    private void StopMovie()
    {
        _movieTimer.Stop();
        MoviePlay.Content = "Play";
    }

    private void OnMovieTick(object? sender, EventArgs e)
    {
        if (_moment is { } moment)
        {
            // Goes by the clock, so a slow redraw skips frames instead of slowing the animation down.
            var frame = _momentStartFrame + (int)(_momentClock.Elapsed.TotalSeconds * GameFramesPerSecond);
            MovieSlider.Value = Math.Min(frame, moment.FrameCount - 1);
            if (frame >= moment.FrameCount - 1)
                StopMovie();
            return;
        }
        if (!_movieBusy && IsVisible && _timeline is { } timeline)
            MovieSlider.Value = ((int)MovieSlider.Value + 1) % timeline.FrameCount;
    }

    private void OnViewChosen(object sender, SelectionChangedEventArgs e)
    {
        if (Canvas == null || !IsInitialized)
            return;
        Canvas.GameScreen = ViewBox.SelectedIndex == 0;
        Dispatcher.BeginInvoke(FitCanvas, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnShowHidden(object sender, RoutedEventArgs e) => Canvas.ShowHidden = ShowHiddenBox.IsChecked == true;

    private void OnTeamChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_choosing)
            return;
        ReadSample();
        if (_layoutPath != null && _layout != null)
            ShowLayout(LayoutBox.SelectedItem as OverlayFile);
    }

    private void BuildTree(string? selectName)
    {
        _treeBusy = true;
        _row = null;
        _rootRow = new WidgetRow(_layout!.Root) { IsExpanded = true };
        foreach (var row in _rootRow.Children)
            row.IsExpanded = true;
        WidgetTree.ItemsSource = new[] { _rootRow };
        _treeBusy = false;
        if ((selectName != null ? _rootRow.Find(selectName) : null) is { } target)
            Choose(target);
        else
            ShowWidget(null);
    }

    private void OnTreeChosen(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_treeBusy || WidgetTree.SelectedItem is not WidgetRow row || ReferenceEquals(row, _row))
            return;
        _row = row;
        ShowWidget(row.Widget, updateCanvas: true);
    }

    private void OnCanvasChosen(LayoutWidget? widget)
    {
        if (widget == null)
        {
            if (_row != null)
                _row.IsSelected = false;
            _row = null;
            ShowWidget(null);
            return;
        }
        if (_rootRow?.FindWidget(widget) is { } row)
            Choose(row);
    }

    private void ShowWidget(LayoutWidget? widget, bool updateCanvas = false)
    {
        WidgetTitle.Text = widget == null ? "PROPERTIES" : widget.Label.ToUpperInvariant();
        WidgetEditor.Show(widget?.Node, "children");
        CopyWidget.IsEnabled = DeleteWidget.IsEnabled = widget?.Parent != null;
        if (updateCanvas)
            Canvas.Select(widget);
    }

    private void SaveLayout(string what)
    {
        if (_layout == null || _layoutPath == null || _services.RequireProject() == null)
        {
            if (_layoutPath != null)
                ShowLayout(LayoutBox.SelectedItem as OverlayFile);
            return;
        }
        _layoutSaved = _layout.ToBytes();
        Save(_layoutPath, _layoutSaved, $"{Path.GetFileName(_layoutPath)}: {what}");
        RefreshLayoutLabels();
    }

    private void ShowWidgetValues()
    {
        if (_row != null)
            WidgetEditor.Show(_row.Widget.Node, "children");
        RefreshLayoutLabels();
    }

    private void OnCopyWidget(object sender, RoutedEventArgs e)
    {
        if (_row is not { } row || _layout == null)
            return;
        var name = _layout.Duplicate(row.Widget);
        SaveLayout($"Copied {row.Widget.Name}");
        Canvas.Redraw();
        BuildTree(name);
    }

    private void OnDeleteWidget(object sender, RoutedEventArgs e)
    {
        if (_row is not { } row || _layout == null)
            return;
        var name = row.Widget.Name;
        _layout.Remove(row.Widget);
        SaveLayout($"Deleted {name}");
        Canvas.Redraw();
        BuildTree(null);
    }

    private void OnRevertLayout(object sender, RoutedEventArgs e)
    {
        if (_layoutPath == null)
            return;

        // The Sportsnet look also replaces one texture, so putting the clock back puts that back too, in one undo step.
        var paths = new List<string> { _layoutPath };
        if (ScoreClockScene.Handles(_layoutPath) && _catalog?.AssetPath(SportsnetBug.LogoAsset) is { } logo && _services.Workspace.IsEdited(logo))
            paths.Add(logo);
        if (_services.Workspace.Project is { } project)
            project.Revert(paths, $"Reverted {Path.GetFileName(_layoutPath)}" + (paths.Count > 1 ? " and its logo texture" : ""));
    }

    private void OnZoom(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Canvas != null)
            Canvas.Zoom = e.NewValue;
    }

    private void OnFit(object sender, RoutedEventArgs e) => FitCanvas();

    private void FitCanvas()
    {
        if (Canvas.Width > 0 && Canvas.Height > 0 && CanvasFrame.ActualWidth > 0)
            ZoomSlider.Value = Math.Clamp(Math.Min((CanvasFrame.ActualWidth - 24) / Canvas.Width, (CanvasFrame.ActualHeight - 24) / Canvas.Height), ZoomSlider.Minimum, ZoomSlider.Maximum);
    }

    // Textures page

    private ArtRow? SelectedArt => ArtList.SelectedItem as ArtRow;

    private void ShowArtList()
    {
        var keep = SelectedArt?.Path;
        foreach (var old in ArtList.Items.OfType<ArtRow>())
            old.Thumb.Wanted = false;
        var rows = _overlay!.Art.Select(a => new ArtRow(a, _services.Workspace.IsEdited(a.Path))).ToList();
        ArtList.ItemsSource = rows;
        foreach (var row in rows)
            _thumbs.Request(row.Thumb);
        NoArtText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ArtList.SelectedItem = rows.FirstOrDefault(r => r.Path == keep) ?? rows.FirstOrDefault();
        if (rows.Count == 0)
            ShowArt();
    }

    private void OnArtChosen(object sender, SelectionChangedEventArgs e) => ShowArt();

    private void ShowArt()
    {
        var run = ++_artRun;
        var row = SelectedArt;
        ArtImport.IsEnabled = false;
        ArtExport.IsEnabled = ArtOpen.IsEnabled = row != null;
        ArtRevert.IsEnabled = row != null && _services.Workspace.IsEdited(row.Path);
        ArtBlocker.Visibility = Visibility.Collapsed;
        if (row == null)
        {
            ArtName.Text = "None selected";
            ArtFacts.Text = "";
            ArtPreview.ShowMessage("Select a texture to preview it.");
            return;
        }

        ArtName.Text = row.Name;
        ArtFacts.Text = row.Usage;
        var workspace = _services.Workspace;
        var path = row.Path;
        Task.Run(() =>
        {
            var content = workspace.ReadCurrent(path) ?? [];
            var stock = workspace.Vfs!.Find(path) is { } file ? workspace.Vfs.ReadContent(file) : content;
            return (GameTexture.TryOpen(content), TexturePipeline.ImportBlocker(stock));
        }).ContinueWith(task =>
        {
            if (run != _artRun)
                return;
            if (task.IsFaulted || task.Result.Item1 == null)
            {
                ArtPreview.ShowMessage("This file could not be read as a texture.");
                return;
            }
            var (texture, blocker) = task.Result;
            ArtFacts.Text = $"{row.Usage}\n{texture!.Summary}" + (row.Edited ? "\nEdited in this project" : "");
            ArtPreview.SetTexture(texture, showAlpha: true);
            ArtImport.IsEnabled = blocker == null;
            if (blocker != null)
            {
                ArtBlocker.Text = blocker;
                ArtBlocker.Visibility = Visibility.Visible;
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnArtImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose an image", Filter = ImageFiles.OpenFilter };
        if (dialog.ShowDialog() == true)
            _ = ImportArt(dialog.FileName);
    }

    public async Task ImportArt(string source)
    {
        if (SelectedArt is not { } row)
        {
            _services.ReportError("Nothing to import into", "Select a texture first.");
            return;
        }
        if (_services.RequireProject() is not { } project)
            return;

        var workspace = _services.Workspace;
        await _services.Jobs.RunAsync($"Importing {Path.GetFileName(source)}", _ =>
        {
            var original = workspace.Vfs!.ReadContent(workspace.Vfs.Find(row.Path)!);
            return TexturePipeline.Import(original, ImageFiles.Load(source));
        }, result =>
        {
            project.Set(row.Path, result.Gnf, $"Overlay texture from {Path.GetFileName(source)}");
            _services.Notify($"Imported into {row.Name}" + (result.Resized ? ", resized to match" : ""));
        });
    }

    private void OnArtExport(object sender, RoutedEventArgs e)
    {
        if (SelectedArt is not { } row)
            return;
        var dialog = new SaveFileDialog { Title = "Export PNG", FileName = Path.ChangeExtension(row.Name, ".png"), Filter = "PNG image|*.png" };
        if (dialog.ShowDialog() != true)
            return;
        var workspace = _services.Workspace;
        var destination = dialog.FileName;
        _ = _services.Jobs.RunAsync($"Exporting {row.Name}", _ =>
        {
            File.WriteAllBytes(destination, PngFile.Write(ImageFiles.DecodeAny(workspace.ReadCurrent(row.Path) ?? []) ?? throw new InvalidDataException($"{row.Name} is not a texture.")));
            return destination;
        }, path => _services.Notify($"Saved {path}"));
    }

    private void OnArtRevert(object sender, RoutedEventArgs e)
    {
        if (SelectedArt is { } row)
            Revert(row.Path, $"Reverted {row.Name}");
    }

    private void OnArtOpen(object sender, RoutedEventArgs e)
    {
        if (SelectedArt is { } row)
            _services.Navigate("Textures", row.Path);
    }

    // Settings page

    private void ShowDefinitions()
    {
        var current = _overlay!.Definitions.Count > 0 ? _services.Workspace.ReadCurrent(OverlayCatalog.DefinitionPath) : null;
        RevertDefinitions.IsEnabled = _services.Workspace.IsEdited(OverlayCatalog.DefinitionPath);
        if (_sameOverlay && current != null && _definitionsSaved != null && current.AsSpan().SequenceEqual(_definitionsSaved))
            return;
        _definitionsSaved = current;
        _definitions = current != null ? JsonEdit.Parse(current) : null;
        var keep = DefinitionBox.SelectedItem as string;
        DefinitionBox.ItemsSource = _overlay.Definitions;
        DefinitionBox.Visibility = _overlay.Definitions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        NoDefinitionText.Visibility = _overlay.Definitions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DefinitionBox.SelectedItem = _overlay.Definitions.Contains(keep ?? "") ? keep : _overlay.Definitions.FirstOrDefault();
        if (DefinitionBox.SelectedItem == null || DefinitionBox.SelectedItem as string == keep)
            ShowDefinition();
    }

    private void OnDefinitionChosen(object sender, SelectionChangedEventArgs e) => ShowDefinition();

    private void ShowDefinition()
    {
        var name = DefinitionBox.SelectedItem as string;
        _definition = name == null ? null : _definitions?["overlays"]?.AsArray().OfType<JsonObject>().FirstOrDefault(d => JsonEdit.String(d["name"]) == name);
        DefinitionEditor.Show(_definition, "dataEntries");
        ShowEntries();
    }

    private void ShowEntries()
    {
        var index = EntryList.SelectedIndex;
        var entries = _definition?["dataEntries"] as JsonArray;
        EntryList.ItemsSource = entries?.OfType<JsonObject>().Select(e => new EntryRow(e)).ToList();
        EntryList.SelectedIndex = Math.Min(index, EntryList.Items.Count - 1);
        if (EntryList.SelectedIndex < 0 && EntryList.Items.Count > 0)
            EntryList.SelectedIndex = 0;
        if (EntryList.Items.Count == 0)
            EntryEditor.Show(null);
    }

    private void OnEntryChosen(object sender, SelectionChangedEventArgs e)
    {
        var entry = (EntryList.SelectedItem as EntryRow)?.Entry;
        CopyEntry.IsEnabled = DeleteEntry.IsEnabled = entry != null;
        if (entry != null)
            EntryEditor.Show(entry);
    }

    private void SaveDefinitions(string what)
    {
        if (_definitions == null)
            return;
        if (_services.RequireProject() == null)
        {
            _definitionsSaved = null;
            ShowDefinitions();
            return;
        }
        _definitionsSaved = JsonEdit.ToBytes(_definitions);
        Save(OverlayCatalog.DefinitionPath, _definitionsSaved, $"{DefinitionBox.SelectedItem}: {what}");
    }

    private void OnCopyEntry(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not EntryRow row || _definition?["dataEntries"] is not JsonArray entries)
            return;
        entries.Insert(entries.IndexOf(row.Entry) + 1, row.Entry.DeepClone());
        SaveDefinitions("Copied data entry");
        ShowEntries();
    }

    private void OnDeleteEntry(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not EntryRow row || _definition?["dataEntries"] is not JsonArray entries)
            return;
        entries.Remove(row.Entry);
        SaveDefinitions("Deleted data entry");
        ShowEntries();
    }

    private void OnRevertDefinitions(object sender, RoutedEventArgs e) => Revert(OverlayCatalog.DefinitionPath, "Reverted definition.overlays");

    // Files page

    private void ShowFileList()
    {
        var keep = (FileList.SelectedItem as FileRow)?.Path;
        var rows = _overlay!.Files.Select(f => new FileRow(f, _services.Workspace.IsEdited(f.Path))).ToList();
        FileList.ItemsSource = rows;
        FileList.SelectedItem = rows.FirstOrDefault(r => r.Path == keep) ?? rows.FirstOrDefault();
    }

    private void OnFileChosen(object sender, SelectionChangedEventArgs e)
    {
        var row = FileList.SelectedItem as FileRow;
        _filePath = row?.Path;
        FileProblem.Text = "";
        var edited = row != null && _services.Workspace.IsEdited(row.Path);
        FileRevert.IsEnabled = MovieRevert.IsEnabled = edited;
        FileSave.IsEnabled = row != null;
        if (row == null)
            return;

        var content = _services.Workspace.ReadCurrent(row.Path) ?? [];
        var movie = row.Kind == OverlayFileKind.Movie;
        TextPane.Visibility = movie ? Visibility.Collapsed : Visibility.Visible;
        MoviePane.Visibility = movie ? Visibility.Visible : Visibility.Collapsed;
        if (movie)
        {
            var info = GfxMovie.Parse(content);
            MovieFacts.Text = $"{row.Name}\n{info.Signature} version {info.Version}, {content.Length:N0} bytes, loads {info.Images.Count} textures";
        }
        else
        {
            var text = Encoding.UTF8.GetString(content);
            if (FileText.Text != text)
                FileText.Text = text;
        }
    }

    private void OnFileSave(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileRow row)
            return;
        var text = FileText.Text;
        if (row.Kind is not OverlayFileKind.Script && JsonEdit.Problem(text) is { } problem)
        {
            FileProblem.Text = "Not saved. " + problem;
            return;
        }
        FileProblem.Text = row.Kind == OverlayFileKind.Script ? "Saved. Lua is not checked here, so a typo only shows up in the game." : "";
        Save(row.Path, Encoding.UTF8.GetBytes(text), $"Edited {row.Name}");
    }

    private void OnFileRevert(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is FileRow row)
            Revert(row.Path, $"Reverted {row.Name}");
    }

    private void OnMovieExport(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileRow row)
            return;
        var dialog = new SaveFileDialog { Title = "Export movie", FileName = row.Name, Filter = "Scaleform movie|*.gfx;*.swf" };
        if (dialog.ShowDialog() != true)
            return;
        File.WriteAllBytes(dialog.FileName, _services.Workspace.ReadCurrent(row.Path) ?? []);
        _services.Notify($"Saved {dialog.FileName}");
    }

    private void OnMovieReplace(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileRow row)
            return;
        var dialog = new OpenFileDialog { Title = "Choose a movie", Filter = "Scaleform movie|*.gfx;*.swf" };
        if (dialog.ShowDialog() == true)
            ReplaceMovie(row, dialog.FileName);
    }

    /// <summary>Refuses a movie the game could not finish loading, because it names a texture that is not there.</summary>
    public void ReplaceMovie(FileRow row, string source)
    {
        var content = File.ReadAllBytes(source);
        try
        {
            var folder = row.Path[..(row.Path.LastIndexOf('/') + 1)];
            var missing = GfxMovie.Parse(content).Images.Where(i => _services.Workspace.Vfs!.Find(folder + i.FileName.ToLowerInvariant()) == null).Select(i => i.FileName).ToList();
            if (missing.Count > 0)
            {
                _services.ReportError("Movie not replaced", $"It needs textures that are not in this folder: {string.Join(", ", missing.Take(6))}");
                return;
            }
        }
        catch (Exception problem) when (problem is InvalidDataException or ArgumentException or IOException or IndexOutOfRangeException)
        {
            _services.ReportError("Movie not replaced", problem.Message);
            return;
        }
        Save(row.Path, content, $"Movie from {Path.GetFileName(source)}");
    }
}

public sealed class WidgetRow : INotifyPropertyChanged
{
    private bool _selected;
    private bool _expanded;

    public WidgetRow(LayoutWidget widget, WidgetRow? parent = null)
    {
        Widget = widget;
        Parent = parent;
        Children = widget.Children.Select(c => new WidgetRow(c, this)).ToList();
    }

    public LayoutWidget Widget { get; }
    public WidgetRow? Parent { get; }
    public List<WidgetRow> Children { get; }
    public string Label => Widget.Label;

    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public bool IsExpanded
    {
        get => _expanded;
        set { _expanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); }
    }

    public WidgetRow? Find(string name) => Widget.Name == name ? this : Children.Select(c => c.Find(name)).FirstOrDefault(r => r != null);

    public WidgetRow? FindWidget(LayoutWidget widget) => ReferenceEquals(Widget, widget) ? this : Children.Select(c => c.FindWidget(widget)).FirstOrDefault(r => r != null);

    public void Reveal()
    {
        for (var row = Parent; row != null; row = row.Parent)
            row.IsExpanded = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ArtRow(OverlayArt art, bool edited)
{
    public string Path { get; } = art.Path;
    public string Name { get; } = art.Name;
    public string Usage { get; } = art.Usage == "Same folder" ? "In this overlay's folder" : "Used as: " + art.Usage;
    public bool Edited { get; } = edited;
    public string State => Edited ? "Edited" : art.Usage;
    public ThumbItem Thumb { get; } = new(art.Path, 128, isPicture: true);
}

public sealed class FileRow(OverlayFile file, bool edited)
{
    public string Path { get; } = file.Path;
    public string Name { get; } = file.Name;
    public OverlayFileKind Kind { get; } = file.Kind;
    public string State => file.Kind + (edited ? ", edited" : "");
}

public sealed class EntryRow(JsonObject entry)
{
    public JsonObject Entry { get; } = entry;
    public string Label => $"{JsonEdit.String(Entry["name"])}  ({JsonEdit.String(Entry["valueId"]) ?? JsonEdit.String(Entry["noaDataName"]) ?? JsonEdit.String(Entry["type"])})";
}
