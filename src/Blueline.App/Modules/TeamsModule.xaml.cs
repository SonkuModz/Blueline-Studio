using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BCnEncoder.Encoder;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core;
using Blueline.Core.Teams;
using Blueline.Formats.Textures;
using Microsoft.Win32;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Blueline.App.Modules;

/// <summary>Things that own a thumbnail, so one handler can start and stop its decoding as it scrolls.</summary>
public interface IHasThumb
{
    ThumbItem Thumb { get; }
}

public sealed class TeamRow(TeamInfo team, ThumbItem thumb) : IHasThumb, INotifyPropertyChanged
{
    private bool _edited;

    public TeamInfo Team { get; } = team;
    public ThumbItem Thumb { get; } = thumb;
    public string EditedMark => _edited ? "●" : "";

    public bool Edited
    {
        set
        {
            if (_edited == value)
                return;
            _edited = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditedMark)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class TeamTile(TeamArt art, bool edited) : IHasThumb, INotifyPropertyChanged
{
    private bool _selected;

    public TeamArt Art { get; } = art;
    public ThumbItem Thumb { get; } = CreateThumb(art);
    public string Label { get; set; } = System.IO.Path.GetFileNameWithoutExtension(art.Name);
    public string EditedMark { get; } = edited ? "●" : "";

    public bool IsSelected
    {
        get => _selected;
        set
        {
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static ThumbItem CreateThumb(TeamArt art)
    {
        var thumb = new ThumbItem(art.IsTexture ? art.Path : null, 128, art.Category == TeamArtCategory.Logos);
        if (!art.IsTexture)
            thumb.Status = "3D model";
        return thumb;
    }
}

public sealed record GalleryHeader(string Text);

public sealed record GalleryRow(IReadOnlyList<TeamTile> Tiles);

/// <summary>
/// One team at a time: its logos, center ice, uniforms, gear, arena and crowd, as pictures. Teams and their numbered
/// art come from the game's team table; see <see cref="TeamCatalog"/> for how the rest is matched. Editing goes
/// through the same texture pipeline as the Textures tab.
/// </summary>
public partial class TeamsModule : UserControl, IEditorModule
{
    private const string AllLeagues = "All leagues";
    private const int TileWidth = 132;
    private readonly AppServices _services;
    private readonly ThumbnailLoader _thumbs;
    private AssetIndex? _index;
    private TeamCatalog? _catalog;
    private bool _loading;
    private bool _dirty;
    private string? _pendingPath;
    private TeamInfo? _team;
    private IReadOnlyList<TeamArt> _art = [];
    private List<TeamTile> _shown = [];
    private TeamTile? _selected;
    private TeamArtCategory _category = TeamArtCategory.Logos;
    private int _columns;
    private int _previewGeneration;
    private ThumbItem? _headerThumb;
    private IReadOnlyList<UniformKit> _kits = [];
    private bool _uniformMode;
    private bool _uniformEverShown;
    private string? _uniformKey;
    private int _uniformGeneration;
    private bool _uniformLoading;

    public TeamsModule(AppServices services)
    {
        _services = services;
        _thumbs = new ThumbnailLoader(services.Workspace, Dispatcher);
        InitializeComponent();
        UniformSizeBox.ItemsSource = Enum.GetValues<UniformSize>();
        UniformTuckBox.ItemsSource = Enum.GetValues<UniformTuck>();
        UniformPantsBox.ItemsSource = Enum.GetValues<UniformPants>();
        UniformSizeBox.SelectedIndex = UniformTuckBox.SelectedIndex = UniformPantsBox.SelectedIndex = 0;
        ShowNoSelection();
        SetTeamButtons();
        DropTarget.Attach(PreviewCard, f => File.Exists(f) && ImageFiles.IsSupported(f), files => _ = ReplaceSelected(files[0]), PreviewCard);
        services.Workspace.Changed += () => Dispatcher.BeginInvoke(OnWorkspaceChanged);
    }

    public string Title => "Teams";
    public FrameworkElement View => this;
    public TexturePreview PreviewControl => Preview;
    public bool IsLoading => _loading;
    public bool ThumbnailsBusy => _thumbs.IsBusy;
    public IReadOnlyList<TeamTile> ShownTiles => _shown;
    public bool UniformBusy => _uniformLoading;
    public ModelViewport UniformView => Uniform;

    public void Activated()
    {
        if (_services.Index is not { } index)
            return;
        if (!ReferenceEquals(index, _index))
            LoadCatalog(index);
        else if (_dirty)
            RefreshAfterEdit();
    }

    /// <summary>Opens the team a game file belongs to, on that file.</summary>
    public void OpenPath(string path)
    {
        if (_catalog == null)
        {
            _pendingPath = path;
            return;
        }
        var teams = _catalog.TeamsUsing(path);
        if (teams.Count == 0)
        {
            _services.Notify($"{path[(path.LastIndexOf('/') + 1)..]} is not filed under a team.");
            return;
        }
        var team = _team != null && teams.Contains(_team) ? _team : teams.FirstOrDefault(t => t.LeagueId == 0) ?? teams[0];
        SelectTeam(team.Name);
        var art = _art.FirstOrDefault(a => a.Path == path);
        if (art == null)
            return;
        AssetSearch.Text = "";
        if (art.IsTechnical)
            ShowMaps.IsChecked = true;
        EditedOnly.IsChecked = false;
        SelectCategory(art.Category);
        SelectAsset(path);
    }

    private void LoadCatalog(AssetIndex index)
    {
        _index = index;
        _catalog = null;
        _loading = true;
        ModsOnly.IsChecked = false;
        ModsOnly.Visibility = _services.Workspace.Vfs?.Install.ModsFolder != null ? Visibility.Visible : Visibility.Collapsed;
        var workspace = _services.Workspace;
        var paths = index.Rows.Where(r => r.File != null).Select(r => r.Path).ToList();
        GalleryMessage.Text = "Reading the team table";
        Task.Run(() => TeamCatalog.Load(workspace.ReadCurrent, paths)).ContinueWith(task =>
        {
            _loading = false;
            if (task.IsFaulted)
            {
                GalleryMessage.Text = "The teams could not be read from this game.";
                _services.ReportError("The teams could not be read", task.Exception!.GetBaseException());
                return;
            }
            _catalog = task.Result;
            GalleryMessage.Text = "";
            LeagueBox.ItemsSource = new[] { AllLeagues }.Concat(_catalog.Leagues).ToList();
            LeagueBox.SelectedItem = "NHL";
            if (_pendingPath is { } pending)
            {
                _pendingPath = null;
                OpenPath(pending);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---- Team list

    private void OnFilterTeams(object sender, RoutedEventArgs e)
    {
        TeamSearchHint.Visibility = TeamSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowTeams();
    }

    private void ShowTeams()
    {
        if (_catalog == null)
            return;
        var league = LeagueBox.SelectedItem as string ?? AllLeagues;
        var words = TeamSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = _team;
        var rows = _catalog.Teams
            .Where(t => (league == AllLeagues && t.LeagueId != 23) || t.League == league)
            .Where(t => words.All(w => t.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase) || t.City.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                                       || t.Abbreviation.Equals(w, StringComparison.OrdinalIgnoreCase)))
            .Select(t => new TeamRow(t, new ThumbItem(_catalog.MainLogo(t), 48, true)) { Edited = TeamIsEdited(t) })
            .ToList();
        Teams.ItemsSource = rows;
        Teams.SelectedItem = rows.FirstOrDefault(r => r.Team == current) ?? rows.FirstOrDefault();
        if (rows.Count == 0)
            ShowTeam(null);
    }

    private bool TeamIsEdited(TeamInfo team)
    {
        if (_services.Workspace.Project is not { } project || project.Changes.Count == 0)
            return false;
        return _catalog!.ArtOf(team).Any(a => _services.Workspace.IsEdited(a.Path));
    }

    private void OnTeamChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Teams.SelectedItem is TeamRow row)
            ShowTeam(row.Team);
    }

    /// <summary>Selects a team by name, widening the list if the current filter hides it.</summary>
    public void SelectTeam(string name)
    {
        if (_catalog?.Teams.FirstOrDefault(t => t.Name == name) is not { } team)
            return;
        if (Teams.Items.Cast<TeamRow>().All(r => r.Team != team))
        {
            TeamSearch.Text = "";
            LeagueBox.SelectedItem = team.League;
        }
        Teams.SelectedItem = Teams.Items.Cast<TeamRow>().FirstOrDefault(r => r.Team == team);
        Teams.ScrollIntoView(Teams.SelectedItem);
    }

    private void ShowTeam(TeamInfo? team)
    {
        _team = team;
        _art = team == null ? [] : _catalog!.ArtOf(team);
        _kits = UniformKits.From(_art);
        if (_headerThumb != null)
        {
            _headerThumb.Wanted = false;
            _headerThumb.PropertyChanged -= OnHeaderThumb;
        }
        HeaderLogo.Source = null;
        Swatches.Children.Clear();
        if (team == null)
        {
            TeamName.Text = TeamFacts.Text = TeamCounts.Text = "";
            BuildChips();
            ShowGallery();
            SetTeamButtons();
            return;
        }

        TeamName.Text = team.Name;
        var arena = team.ArenaName is { Length: > 0 } name
            ? name + (team.Capacity > 0 ? $", seats {team.Capacity:N0}" : "") + (team.YearOpened > 0 ? $", opened {team.YearOpened}" : "")
            : null;
        TeamFacts.Text = string.Join("  ·  ", new[] { team.League, arena }.Where(s => s != null));
        foreach (var color in team.Colors)
            Swatches.Children.Add(new Rectangle
            {
                Width = 18,
                Height = 18,
                RadiusX = 3,
                RadiusY = 3,
                Margin = new Thickness(4, 0, 0, 0),
                Stroke = (Brush)FindResource("LineBrush"),
                Fill = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color)),
            });

        _headerThumb = new ThumbItem(_catalog!.MainLogo(team), 176, true);
        _headerThumb.PropertyChanged += OnHeaderThumb;
        _thumbs.Request(_headerThumb);
        BuildChips();
        ShowGallery();
        SetTeamButtons();
    }

    private void OnHeaderThumb(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThumbItem.Image) && sender is ThumbItem thumb && ReferenceEquals(thumb, _headerThumb))
            HeaderLogo.Source = thumb.Image;
    }

    private void SetTeamButtons()
    {
        var hasLogos = _art.Any(a => a.Category == TeamArtCategory.Logos);
        var edited = _art.Count(a => _services.Workspace.IsEdited(a.Path));
        LogoSetButton.IsEnabled = hasLogos;
        ImportPackButton.IsEnabled = _team != null;
        RevertTeamButton.IsEnabled = edited > 0;
        var textures = _art.Count(a => a.IsTexture);
        TeamCounts.Text = _team == null ? "" : $"{textures:N0} textures, {_art.Count - textures:N0} models" + (edited > 0 ? $", {edited:N0} edited" : "");
    }

    // ---- Gallery

    private bool Passes(TeamArt art) => ShowMaps.IsChecked == true || !art.IsTechnical;

    private void BuildChips()
    {
        CategoryChips.Children.Clear();
        var counts = _art.Where(Passes).GroupBy(a => a.Category).ToDictionary(g => g.Key, g => g.Count());
        if (counts.Count > 0 && !counts.ContainsKey(_category))
            _category = counts.Keys.Min();
        foreach (var (category, count) in counts.OrderBy(p => p.Key))
        {
            var chip = new ToggleButton
            {
                Style = (Style)FindResource("Chip"),
                Content = $"{TeamCatalog.CategoryNames[category]}  {count:N0}",
                IsChecked = category == _category,
                Margin = new Thickness(0, 0, 4, 4),
                Tag = category,
            };
            chip.Click += OnChip;
            CategoryChips.Children.Add(chip);
        }
    }

    private void OnChip(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: TeamArtCategory category })
            SelectCategory(category);
    }

    public void SelectCategory(TeamArtCategory category)
    {
        _category = category;
        foreach (var chip in CategoryChips.Children.OfType<ToggleButton>())
            chip.IsChecked = Equals(chip.Tag, category);
        ShowGallery();
    }

    public void SelectAsset(string path)
    {
        if (_shown.FirstOrDefault(t => t.Art.Path == path) is { } tile)
            SelectTile(tile);
    }

    private void OnGalleryOption(object sender, RoutedEventArgs e)
    {
        BuildChips();
        ShowGallery();
    }

    private void OnAssetSearch(object sender, TextChangedEventArgs e)
    {
        AssetSearchHint.Visibility = AssetSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowGallery();
    }

    private void ShowGallery()
    {
        var keep = _selected?.Art.Path;
        var words = AssetSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var workspace = _services.Workspace;
        bool FromMods(TeamArt art) => workspace.Vfs?.Find(art.Path)?.IsFromModsFolder == true;
        var matching = _art
            .Where(a => a.Category == _category && Passes(a))
            .Where(a => EditedOnly.IsChecked != true || workspace.IsEdited(a.Path))
            .Where(a => ModsOnly.IsChecked != true || FromMods(a))
            .Where(a => words.All(w => a.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Sections a mod changed come first, so a team's modded kit is what opens rather than a stock one it also owns.
        var modded = matching.Where(FromMods).Select(a => a.Section).ToHashSet(StringComparer.Ordinal);
        _shown = matching.OrderByDescending(a => modded.Contains(a.Section))
            .Select(a => new TeamTile(a, workspace.IsEdited(a.Path)))
            .ToList();
        LayoutGallery();

        if (_team != null)
            Count.Text = $"{_shown.Count:N0} shown in {TeamCatalog.CategoryNames[_category]}";
        else
            Count.Text = "";
        GalleryMessage.Text = _catalog == null ? GalleryMessage.Text
            : _team == null ? "No team matches."
            : _shown.Count == 0 ? "Nothing here matches these filters." : "";
        ExportShownButton.IsEnabled = _shown.Any(t => t.Art.IsTexture);

        var again = keep == null ? null : _shown.FirstOrDefault(t => t.Art.Path == keep);
        SelectTile(again ?? _shown.FirstOrDefault(t => t.Art.IsTexture) ?? _shown.FirstOrDefault());
    }

    /// <summary>Lays tiles into rows, so the list only has to create the rows on screen.</summary>
    private void LayoutGallery()
    {
        var width = Gallery.ActualWidth > 0 ? Gallery.ActualWidth : 600;
        _columns = Math.Max(1, (int)((width - 14) / TileWidth));
        var rows = new List<object>();

        // A section of one file needs no heading: its tile says what it is, and such tiles share rows.
        var loose = new List<TeamTile>();
        void FlushLoose()
        {
            rows.AddRange(loose.Chunk(_columns).Select(tiles => new GalleryRow(tiles)));
            loose.Clear();
        }
        foreach (var group in _shown.GroupBy(t => t.Art.Section))
        {
            if (group.Count() == 1)
            {
                var tile = group.First();
                tile.Label = group.Key;
                loose.Add(tile);
                continue;
            }
            FlushLoose();
            rows.Add(new GalleryHeader($"{group.Key.ToUpperInvariant()}  ·  {group.Count():N0}"));
            rows.AddRange(group.Chunk(_columns).Select(tiles => new GalleryRow(tiles)));
        }
        FlushLoose();
        Gallery.ItemsSource = rows;
    }

    private void OnGallerySized(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && Math.Max(1, (int)((Gallery.ActualWidth - 14) / TileWidth)) != _columns)
            LayoutGallery();
    }

    private void OnThumbOwnerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IHasThumb owner })
            _thumbs.Request(owner.Thumb);
    }

    private void OnThumbOwnerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IHasThumb owner })
            owner.Thumb.Wanted = false;
    }

    private void OnTileClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TeamTile tile })
            SelectTile(tile);
    }

    // ---- Selected file

    private void ShowNoSelection()
    {
        AssetName.Text = AssetSummary.Text = "";
        Preview.ShowMessage("Select a picture to see it here.");
        ReplaceButton.IsEnabled = RevertButton.IsEnabled = ExportButton.IsEnabled = EditorButton.IsEnabled = CopyButton.IsEnabled = false;
        SharedText.Visibility = BlockerText.Visibility = ShowOriginal.Visibility = JerseyMapsButton.Visibility = Visibility.Collapsed;
    }

    private void SelectTile(TeamTile? tile)
    {
        if (_selected != null)
            _selected.IsSelected = false;
        var samePicture = _selected?.Art.Path == tile?.Art.Path;
        _selected = tile;
        if (tile != null)
            tile.IsSelected = true;
        if (!samePicture)
            ShowOriginal.IsChecked = false;
        ShowSelected();
    }

    private void OnShowOriginal(object sender, RoutedEventArgs e) => ShowSelected();

    private void ShowSelected()
    {
        var generation = ++_previewGeneration;
        UpdateUniformView();
        if (_selected is not { } tile)
        {
            ShowNoSelection();
            return;
        }

        var art = tile.Art;
        var workspace = _services.Workspace;
        var edited = workspace.IsEdited(art.Path);
        AssetName.Text = art.Name;
        AssetSummary.Text = art.Section;
        EditorButton.IsEnabled = CopyButton.IsEnabled = true;
        JerseyMapsButton.Visibility = JerseySheets.IsColourSheet(art.Path) ? Visibility.Visible : Visibility.Collapsed;
        RevertButton.IsEnabled = edited;
        ExportButton.IsEnabled = art.IsTexture;
        ReplaceButton.IsEnabled = false;
        BlockerText.Visibility = Visibility.Collapsed;
        ShowOriginal.Visibility = edited ? Visibility.Visible : Visibility.Collapsed;

        var sharedWith = _catalog!.TeamsUsing(art.Path).Where(t => t != _team).ToList();
        SharedText.Visibility = sharedWith.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SharedText.Text = sharedWith.Count == 0 ? "" : "Also used by " + string.Join(", ", sharedWith.Take(4).Select(t => t.Name))
            + (sharedWith.Count > 4 ? $" and {sharedWith.Count - 4} more" : "") + ". A change here shows for them too.";

        if (!art.IsTexture)
        {
            Preview.ShowMessage("This is a model. Open it in the editor for a 3D preview.");
            return;
        }

        var showOriginal = edited && ShowOriginal.IsChecked == true;
        var vfs = workspace.Vfs!;
        var path = art.Path;
        Task.Run(() =>
        {
            var stock = vfs.Find(path) is { } file ? vfs.ReadContent(file) : null;
            var content = showOriginal ? stock ?? [] : workspace.ReadCurrent(path) ?? [];
            return (GameTexture.TryOpen(content), stock == null ? null : TexturePipeline.ImportBlocker(stock));
        }).ContinueWith(task =>
        {
            if (generation != _previewGeneration)
                return;
            if (task.IsFaulted)
            {
                Preview.ShowMessage("This file could not be read: " + task.Exception!.GetBaseException().Message);
                return;
            }
            var (texture, blocker) = task.Result;
            if (texture == null)
            {
                Preview.ShowMessage("This file could not be read as a texture.");
                return;
            }
            AssetSummary.Text = $"{art.Section}  ·  {texture.Summary}";
            AssetSummary.ToolTip = vfs.Find(path)?.SourceName;
            if (vfs.Find(path)?.IsFromModsFolder == true)
                AssetSummary.Text = $"From the mods folder  ·  {AssetSummary.Text}";
            Preview.SetTexture(texture, showAlpha: art.Category == TeamArtCategory.Logos);
            ReplaceButton.IsEnabled = blocker == null;
            if (blocker != null)
            {
                BlockerText.Text = blocker;
                BlockerText.Visibility = Visibility.Visible;
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---- 3D uniform

    private void OnPictureView(object sender, RoutedEventArgs e) => ShowUniform(false);
    private void OnUniformView(object sender, RoutedEventArgs e) => ShowUniform(true);
    private void OnUniformOption(object sender, SelectionChangedEventArgs e) => UpdateUniformView();

    /// <summary>Switches the preview between the flat sheet and the kit on the game's meshes. It stays that way between files.</summary>
    public void ShowUniform(bool on)
    {
        _uniformMode = on;
        UpdateUniformView();
    }

    public void SetUniformOptions(UniformSize size, UniformTuck tuck, UniformPants pants)
    {
        UniformSizeBox.SelectedItem = size;
        UniformTuckBox.SelectedItem = tuck;
        UniformPantsBox.SelectedItem = pants;
    }

    private void UpdateUniformView()
    {
        var kit = _selected is { } tile ? UniformKits.KitOf(_kits, tile.Art.Path) : null;
        var show = _uniformMode && kit != null;
        ViewChoice.Visibility = kit != null ? Visibility.Visible : Visibility.Collapsed;
        PictureChip.IsChecked = !_uniformMode;
        UniformChip.IsChecked = _uniformMode;
        Preview.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        UniformPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
            LoadUniform(kit!);
    }

    private void LoadUniform(UniformKit kit)
    {
        var workspace = _services.Workspace;
        var vfs = workspace.Vfs;
        if (vfs == null || UniformSizeBox.SelectedItem is not UniformSize size || UniformTuckBox.SelectedItem is not UniformTuck tuck || UniformPantsBox.SelectedItem is not UniformPants pants)
            return;
        var showOriginal = ShowOriginal.IsChecked == true && ShowOriginal.Visibility == Visibility.Visible;
        var key = $"{kit.Key}|{size}|{tuck}|{pants}|{showOriginal}";
        if (key == _uniformKey)
            return;
        _uniformKey = key;
        var generation = ++_uniformGeneration;
        _uniformLoading = true;
        var keepView = _uniformEverShown;
        if (!keepView)
            Uniform.ShowMessage("Dressing the player");

        byte[]? Read(string path) => showOriginal && vfs.Find(path) is { } file ? vfs.ReadContent(file) : workspace.ReadCurrent(path);
        Task.Run(() =>
        {
            var models = UniformKits.ModelsFor(kit.Brand, size, tuck, pants);
            var jersey = SheetBitmap(Read(kit.Jersey));
            var parts = new List<ModelPart>();
            void Add(string path, System.Windows.Media.Imaging.BitmapSource? texture)
            {
                if (Read(path) is { } data)
                    parts.Add(new ModelPart(Blueline.Formats.Models.McdFile.Parse(data), texture));
            }
            Add(models.Jersey, jersey);
            Add(models.JerseyTie, jersey);
            Add(models.Pant, kit.Pant == null ? null : SheetBitmap(Read(kit.Pant)));
            Add(models.Sock, kit.Sock == null ? null : SheetBitmap(Read(kit.Sock)));
            foreach (var gear in models.Gear)
                Add(gear, null);
            return ModelViewport.Prepare(parts);
        }).ContinueWith(task =>
        {
            if (generation != _uniformGeneration)
                return;
            _uniformLoading = false;
            if (task.IsFaulted)
            {
                _uniformKey = null;
                _services.Log.Error($"3D uniform {kit.Key}", task.Exception!.GetBaseException());
                Uniform.ShowMessage("This uniform could not be drawn: " + task.Exception.GetBaseException().Message);
                UniformNote.Text = "";
                return;
            }
            Uniform.Show(task.Result, keepView, closeness: 2.1);
            if (!keepView)
                Uniform.SetView(0.45, 0.12);
            _uniformEverShown = true;

            var missing = new[] { kit.Pant == null ? "pants" : null, kit.Sock == null ? "socks" : null }.OfType<string>().ToList();
            UniformNote.Text = kit.Label + (showOriginal ? " (original)" : "")
                + (missing.Count > 0 ? $". The game has no {string.Join(" or ", missing)} sheet for this kit, so they are grey" : "")
                + ". Numbers, names, shine and fabric detail are not drawn; gloves and skates stay grey.";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>A sheet for wrapping on a mesh, at most 1024 wide. Its alpha holds other data, so it is left out.</summary>
    private static System.Windows.Media.Imaging.BitmapSource? SheetBitmap(byte[]? content)
    {
        if (content == null || GameTexture.TryOpen(content) is not { Blocker: null } texture)
            return null;
        var mip = 0;
        while (mip + 1 < texture.MipCount && Math.Max(texture.Width >> mip, texture.Height >> mip) > 1024)
            mip++;
        var image = texture.DecodeMip(mip);
        var pixels = new byte[image.Pixels.Length];
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            pixels[i] = image.Pixels[i + 2];
            pixels[i + 1] = image.Pixels[i + 1];
            pixels[i + 2] = image.Pixels[i];
            pixels[i + 3] = 255;
        }
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr32, null, pixels, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private void OnWorkspaceChanged()
    {
        _uniformKey = null;
        _thumbs.Invalidate();
        if (_catalog == null)
            return;
        if (IsVisible)
            RefreshAfterEdit();
        else
            _dirty = true;
    }

    private void RefreshAfterEdit()
    {
        _dirty = false;
        foreach (var row in Teams.Items.OfType<TeamRow>())
        {
            row.Edited = TeamIsEdited(row.Team);
            _thumbs.Request(row.Thumb);
        }
        if (_team != null)
        {
            if (_headerThumb != null)
                _thumbs.Request(_headerThumb);
            BuildChips();
            ShowGallery();
            SetTeamButtons();
        }
    }

    // ---- Actions

    private static string? ChooseImage(string title)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = ImageFiles.OpenFilter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        if (ChooseImage("Choose an image") is { } file)
            _ = ReplaceSelected(file);
    }

    public async Task ReplaceSelected(string source)
    {
        if (_selected is not { Art.IsTexture: true } tile)
        {
            _services.ReportError("Nothing to import into", "Select a texture first, then drop the image on the preview.");
            return;
        }
        if (_services.RequireProject() is not { } project)
            return;
        var vfs = _services.Workspace.Vfs!;
        var path = tile.Art.Path;
        var name = tile.Art.Name;
        await _services.Jobs.RunAsync($"Importing {Path.GetFileName(source)}", context =>
        {
            var original = vfs.ReadContent(vfs.Find(path) ?? throw new InvalidDataException($"{path} is not in the game."));
            var image = ImageFiles.Load(source);
            context.Status($"Encoding {name}");
            return TexturePipeline.Import(original, image, CompressionQuality.Balanced);
        }, result =>
        {
            project.Set(path, result.Gnf, $"Texture from {Path.GetFileName(source)}");
            _services.Notify($"Imported into {name} in {result.Elapsed.TotalSeconds:F1}s" + (result.Resized ? ", resized to match" : ""));
        });
    }

    private void OnReplaceLogoSet(object sender, RoutedEventArgs e)
    {
        if (ChooseImage("Choose the new logo, ideally a PNG with a transparent background") is { } file)
            _ = ReplaceLogoSet(file);
    }

    public async Task ReplaceLogoSet(string source)
    {
        if (_team is not { } team || _services.RequireProject() is not { } project)
            return;
        var vfs = _services.Workspace.Vfs!;
        var art = _art;
        await _services.Jobs.RunAsync($"Replacing the {team.Name} logos", context =>
            TeamLogoImport.Run(art, ImageFiles.Load(source), p => vfs.Find(p) is { } f ? vfs.ReadContent(f) : null, project, team.Name,
                CompressionQuality.Balanced, new JobProgress(context, "Encoding logos"), context.Cancel), result =>
        {
            foreach (var item in result.Items.Where(i => i.Outcome == LogoOutcome.Skipped))
                _services.Log.Info($"Logo set, left alone: {item.Path}: {item.Detail}");
            foreach (var item in result.Items.Where(i => i.Outcome == LogoOutcome.Failed))
                _services.Log.Warning($"Logo set, failed: {item.Path}: {item.Detail}");
            _services.Notify($"{team.Name} logos: {result.Imported} replaced, {result.Skipped} left alone" + (result.Failed > 0 ? $", {result.Failed} failed" : "") + ". The log lists why.");
            if (result.Imported == 0)
                _services.ReportError("No logos were replaced", result.Items.FirstOrDefault()?.Detail ?? "This team has no logo files.");
        });
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        if (_selected is { } tile && _services.Workspace.Project is { } project)
            project.Revert([tile.Art.Path], $"Reverted {tile.Art.Name}");
    }

    private void OnRevertTeam(object sender, RoutedEventArgs e) => RevertTeam();

    public void RevertTeam()
    {
        if (_team is not { } team || _services.Workspace.Project is not { } project)
            return;
        var edited = _art.Where(a => _services.Workspace.IsEdited(a.Path)).Select(a => a.Path).ToList();
        if (edited.Count > 0)
            project.Revert(edited, $"Reverted {team.Name}");
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (_selected is { } tile)
            _services.Navigate(tile.Art.IsTexture ? "Textures" : "Models", tile.Art.Path);
    }

    private void OnJerseyMaps(object sender, RoutedEventArgs e) => MakeJerseyMaps();

    /// <summary>Opens the selected jersey colour sheet in the Jersey Maps tab, which makes its maps.</summary>
    public void MakeJerseyMaps()
    {
        if (_selected is { } tile && JerseySheets.IsColourSheet(tile.Art.Path))
            _services.Navigate("Jersey Maps", tile.Art.Path);
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } tile)
            return;
        Clipboard.SetText(tile.Art.Path);
        _services.Notify("Path copied.");
    }

    private void OnExportOne(object sender, RoutedEventArgs e)
    {
        if (_selected is not { Art.IsTexture: true } tile)
            return;
        var dialog = new SaveFileDialog { Title = "Export as PNG", Filter = "PNG image|*.png", FileName = Path.GetFileNameWithoutExtension(tile.Art.Name) + ".png" };
        if (dialog.ShowDialog() != true)
            return;
        var workspace = _services.Workspace;
        var path = tile.Art.Path;
        _ = _services.Jobs.RunAsync($"Exporting {tile.Art.Name}", _ =>
        {
            var texture = GameTexture.TryOpen(workspace.ReadCurrent(path) ?? []) ?? throw new InvalidDataException("This file is not a readable texture.");
            File.WriteAllBytes(dialog.FileName, PngFile.Write(texture.DecodeMip(0)));
            return dialog.FileName;
        }, file => _services.Notify($"Saved {file}"));
    }

    private void OnExportShown(object sender, RoutedEventArgs e)
    {
        var paths = _shown.Where(t => t.Art.IsTexture).Select(t => t.Art.Path).ToList();
        if (paths.Count == 0 || AppServices.PickFolder($"Export {paths.Count} textures as PNG, keeping their game folders") is not { } folder)
            return;
        _ = ExportPaths(paths, folder);
    }

    public Task ExportPaths(IReadOnlyList<string> paths, string folder)
    {
        var workspace = _services.Workspace;
        return _services.Jobs.RunAsync($"Exporting {paths.Count} textures", context =>
        {
            var done = 0;
            var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.ForEach(paths, new ParallelOptions { CancellationToken = context.Cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, path =>
            {
                try
                {
                    var texture = GameTexture.TryOpen(workspace.ReadCurrent(path) ?? []) ?? throw new InvalidDataException("not a texture");
                    var target = Path.Combine(folder, Path.ChangeExtension(path, ".png").Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, PngFile.Write(texture.DecodeMip(0)));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    failed.Add($"{path}: {error.Message}");
                }
                context.Progress("Exporting team textures", Interlocked.Increment(ref done), paths.Count);
            });
            return failed.ToList();
        }, failed =>
        {
            foreach (var failure in failed)
                _services.Log.Warning($"Export failed: {failure}");
            _services.Notify($"Exported {paths.Count - failed.Count} of {paths.Count}. They keep game paths, so Import folder matches them back after editing.");
        });
    }

    private void OnImportFolder(object sender, RoutedEventArgs e)
    {
        if (AppServices.PickFolder("Choose a folder of images to import") is { } folder)
            _ = ImportFolder(folder);
    }

    public async Task ImportFolder(string folder)
    {
        if (_services.RequireProject() is not { } project)
            return;
        var vfs = _services.Workspace.Vfs!;
        await _services.Jobs.RunAsync($"Importing {Path.GetFileName(folder)}", context =>
            BatchTextureImport.Run(folder, vfs, project, CompressionQuality.Balanced, ImageFiles.Load, new JobProgress(context, "Encoding textures"), context.Cancel), results =>
        {
            foreach (var item in results.Where(r => r.Outcome != BatchOutcome.Imported))
                _services.Log.Warning($"Import, {item.Outcome.ToString().ToLowerInvariant()}: {Path.GetFileName(item.Source)}: {item.Detail}");
            var imported = results.Count(r => r.Outcome == BatchOutcome.Imported);
            _services.Notify($"Imported {imported} of {results.Count} images." + (imported < results.Count ? " The log lists the rest." : ""));
        });
    }

    private sealed class JobProgress(JobContext context, string text) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => context.Progress($"{text}: {value.Done} of {value.Total}", value.Done, value.Total);
    }
}
