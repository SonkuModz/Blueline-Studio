using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using BCnEncoder.Encoder;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core;
using Blueline.Core.Teams;
using Blueline.Formats.Textures;
using Microsoft.Win32;

namespace Blueline.App.Modules;

/// <summary>
/// Makes a jersey's normal (`_nm`) and coeff (`_coeff`) maps from its colour sheet, or from a picture of what should be
/// raised, on a plain base built from the game, the jersey's original, or the user's own blank.
/// </summary>
public partial class JerseyMapsModule : UserControl, IEditorModule
{
    private enum BaseKind { Plain, Original, Blank }

    /// <summary>The selected jersey's sheets, decoded once per selection.</summary>
    private sealed record SheetSet(JerseySheets Sheets, RgbaImage Colour, RgbaImage OriginalNormal, RgbaImage OriginalCoeff,
        RgbaImage CurrentNormal, RgbaImage CurrentCoeff, bool NormalEdited, bool CoeffEdited);

    private sealed record Made(RaisedAreas Areas, RgbaImage Normal, RgbaImage Coeff, JerseyMapOptions Options);

    private const int BlankItem = 2, ChooseItem = 3;

    private readonly AppServices _services;
    private readonly DispatcherTimer _debounce;
    private readonly ConcurrentDictionary<(string Brand, string Suffix), Lazy<Task<RgbaImage>>> _plain = new();
    private VirtualFileSystem? _plainFor;
    private SheetSet? _loaded;
    private Made? _made;
    private string? _picturePath;
    private RgbaImage? _picture;
    private string _view = "Normal";
    private int _loadGeneration, _makeGeneration;
    private bool _quiet;

    public JerseyMapsModule(AppServices services)
    {
        _services = services;
        _quiet = true;
        InitializeComponent();
        Browser.SetScope(r => r.Category == AssetCategory.Texture && JerseySheets.IsColourSheet(r.Path));
        Browser.SelectionChanged += LoadSelection;
        QualityBox.ItemsSource = new[] { "Fast", "Balanced", "Best" };
        QualityBox.SelectedIndex = 1;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Make();
        };
        DropTarget.Attach(PreviewCard, f => File.Exists(f) && ImageFiles.IsSupported(f), files => UsePicture(files[0]), PreviewCard);
        services.Workspace.Changed += () => Dispatcher.BeginInvoke(OnWorkspaceChanged);
        FillBaseChoices(null);
        _quiet = false;
        ShowValues();
        Clear("Choose a jersey colour sheet on the left. Its normal and coeff maps are made from it.");
    }

    public string Title => "Jersey Maps";
    public FrameworkElement View => this;
    public AssetBrowser BrowserControl => Browser;
    public TexturePreview PreviewControl => Preview;

    /// <summary>True while sheets are loading or maps are being made.</summary>
    public bool IsBusy { get; private set; }
    public bool HasMaps => _made != null;

    public void Activated() => Browser.Load(_services.Index);

    public void OpenPath(string path)
    {
        Browser.Load(_services.Index);
        // Opened from another tab to make maps, so show the new normal map rather than whatever view was last used.
        ShowView("Normal");
        if (JerseySheets.IsColourSheet(path))
            Browser.Reveal(path);
        else if (JerseySheets.FromColour(path.Replace("_nm.gnf", "_cm.gnf").Replace("_coeff.gnf", "_cm.gnf")) is { } sheets)
            Browser.Reveal(sheets.Colour);
    }

    private CompressionQuality Quality => QualityBox.SelectedIndex switch
    {
        0 => CompressionQuality.Fast,
        2 => CompressionQuality.BestQuality,
        _ => CompressionQuality.Balanced,
    };

    private JerseyMapOptions Options => new(
        Height: HeightSlider.Value,
        Softness: SoftnessSlider.Value,
        Tolerance: (int)ToleranceSlider.Value,
        MarkCoeff: MarkCoeff.IsChecked == true,
        CoeffValue: (byte)CoeffSlider.Value,
        InnerDetail: DetailSlider.Value,
        ClearMargin: ClearSlider.Value,
        Stitch: StitchBox.IsChecked == true,
        Grain: GrainSlider.Value);

    private bool FromPicture => FromPictureChip.IsChecked == true;

    private void OnWorkspaceChanged()
    {
        if (!ReferenceEquals(_plainFor, _services.Workspace.Vfs))
        {
            _plain.Clear();
            _plainFor = _services.Workspace.Vfs;
            Browser.Load(_services.Index);
        }
        if (_loaded != null && IsVisible)
            LoadSelection();
    }

    private void Clear(string message)
    {
        _loaded = null;
        _made = null;
        IsBusy = false;
        NameText.Text = "None selected";
        KitText.Text = StateText.Text = "";
        ViewNote.Text = "";
        MainSwatch.Background = null;
        Preview.ShowMessage(message);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        ApplyButton.IsEnabled = ExportButton.IsEnabled = _made != null && !IsBusy;
        RevertButton.IsEnabled = _loaded is { } l && (l.NormalEdited || l.CoeffEdited);
        ColourOptions.Visibility = FromPicture ? Visibility.Collapsed : Visibility.Visible;
        PictureOptions.Visibility = FromPicture ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SelectJersey(string path)
    {
        Browser.Load(_services.Index);
        Browser.Reveal(path);
    }

    private void LoadSelection()
    {
        var generation = ++_loadGeneration;
        if (Browser.Selected is not { } row || JerseySheets.FromColour(row.Path) is not { } sheets || _services.Workspace.Vfs is not { } vfs)
        {
            Clear("Choose a jersey colour sheet on the left. Its normal and coeff maps are made from it.");
            return;
        }

        var workspace = _services.Workspace;
        NameText.Text = row.Name.Replace("_cm.gnf", "");
        KitText.Text = $"{sheets.BrandLabel}: making {Path.GetFileName(sheets.Normal)} and {Path.GetFileName(sheets.Coeff)}";
        StateText.Text = "";
        FillBaseChoices(sheets.Brand);
        IsBusy = true;
        if (_loaded?.Sheets != sheets)
            Preview.ShowMessage("Reading the sheets");

        Task.Run(() =>
        {
            RgbaImage Decode(byte[]? content, string path) =>
                (GameTexture.TryOpen(content ?? throw new InvalidDataException($"{path} is not in the game.")) ?? throw new InvalidDataException($"{path} is not a texture.")).DecodeMip(0);
            byte[]? Original(string path) => vfs.Find(path) is { } file ? vfs.ReadContent(file) : null;

            var (normalStock, coeffStock) = (Original(sheets.Normal), Original(sheets.Coeff));
            var normalEdited = workspace.IsEdited(sheets.Normal);
            var coeffEdited = workspace.IsEdited(sheets.Coeff);
            var originalNormal = Decode(normalStock, sheets.Normal);
            var originalCoeff = Decode(coeffStock, sheets.Coeff);
            return new SheetSet(sheets, Decode(workspace.ReadCurrent(sheets.Colour), sheets.Colour), originalNormal, originalCoeff,
                normalEdited ? Decode(workspace.ReadCurrent(sheets.Normal), sheets.Normal) : originalNormal,
                coeffEdited ? Decode(workspace.ReadCurrent(sheets.Coeff), sheets.Coeff) : originalCoeff,
                normalEdited, coeffEdited);
        }).ContinueWith(task =>
        {
            if (generation != _loadGeneration)
                return;
            if (task.IsFaulted)
            {
                IsBusy = false;
                Clear("This jersey's sheets could not be read: " + task.Exception!.GetBaseException().Message);
                return;
            }
            _loaded = task.Result;
            var main = JerseyMaps.MainColour(_loaded.Colour);
            MainSwatch.Background = new SolidColorBrush(Color.FromRgb(main.R, main.G, main.B));
            MainText.Text = $"Main colour  #{main.R:X2}{main.G:X2}{main.B:X2}";
            StateText.Text = (_loaded.NormalEdited, _loaded.CoeffEdited) switch
            {
                (true, true) => "Both maps are edited in this project",
                (true, false) => "The normal map is edited in this project",
                (false, true) => "The coeff map is edited in this project",
                _ => "",
            };
            Make();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The fixed choices plus the user's blank for this brand, if they chose one.</summary>
    private void FillBaseChoices(string? brand)
    {
        var label = brand switch { "adidas" => "Adidas", "rbk" => "Reebok", "nineties" => "Nineties", _ => "brand" };
        var wasQuiet = _quiet;
        _quiet = true;
        foreach (var (box, suffix) in new[] { (NormalBaseBox, "nm"), (CoeffBaseBox, "coeff") })
        {
            var index = Math.Max(0, box.SelectedIndex);
            var blank = brand != null && _services.Settings.JerseyBlanks.TryGetValue($"{brand}_{suffix}", out var file) ? Path.GetFileName(file) : null;
            box.ItemsSource = new[]
            {
                $"Plain {label} fabric, from the game",
                "This jersey's original map",
                blank != null ? $"Your blank: {blank}" : "Your blank (none chosen)",
                "Choose a blank fileâ€¦",
            };
            box.SelectedIndex = index == BlankItem && blank == null ? 0 : Math.Min(index, BlankItem);
        }
        _quiet = wasQuiet;
    }

    private void OnBaseChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_quiet || sender is not ComboBox box)
            return;
        var suffix = box == NormalBaseBox ? "nm" : "coeff";
        var brand = _loaded?.Sheets.Brand;
        var key = $"{brand}_{suffix}";
        if (box.SelectedIndex == ChooseItem || (box.SelectedIndex == BlankItem && brand != null && !_services.Settings.JerseyBlanks.ContainsKey(key)))
        {
            if (brand == null)
            {
                _services.ReportError("Choose a jersey first", "A blank belongs to a brand, so pick a jersey of that brand first.");
                _quiet = true;
                box.SelectedIndex = 0;
                _quiet = false;
                return;
            }
            var dialog = new OpenFileDialog { Title = $"Choose a blank {(suffix == "nm" ? "normal" : "coeff")} map for {_loaded!.Sheets.BrandLabel} jerseys", Filter = ImageFiles.OpenFilter };
            if (dialog.ShowDialog() == true)
                SetBlank(brand, suffix, dialog.FileName);
            _quiet = true;
            box.SelectedIndex = _services.Settings.JerseyBlanks.ContainsKey(key) ? BlankItem : 0;
            _quiet = false;
        }
        OnOption(sender, e);
    }

    /// <summary>Remembers the user's blank for a brand and map, such as "adidas" and "nm".</summary>
    public void SetBlank(string brand, string suffix, string file)
    {
        _services.Settings.JerseyBlanks[$"{brand}_{suffix}"] = file;
        _services.Settings.Save();
        FillBaseChoices(brand);
        (suffix == "nm" ? NormalBaseBox : CoeffBaseBox).SelectedIndex = BlankItem;
    }

    private void OnSourceChip(object sender, RoutedEventArgs e)
    {
        var picture = sender == FromPictureChip;
        FromPictureChip.IsChecked = picture;
        FromColourChip.IsChecked = !picture;
        UpdateButtons();
        Make();
    }

    private void OnChoosePicture(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose a picture of what is raised", Filter = ImageFiles.OpenFilter };
        if (dialog.ShowDialog() == true)
            UsePicture(dialog.FileName);
    }

    /// <summary>Uses a picture for the raised areas: its transparency, or else its brightness.</summary>
    public void UsePicture(string file)
    {
        try
        {
            _picture = ImageFiles.Load(file);
            _picturePath = file;
            PictureText.Text = $"{Path.GetFileName(file)}, {_picture.Width}x{_picture.Height}. " +
                (TeamLogoImport.HasTransparency(_picture) ? "Its transparency marks what is raised." : "It has no transparency, so white is raised and black is flat.");
            FromPictureChip.IsChecked = true;
            FromColourChip.IsChecked = false;
            UpdateButtons();
            Make();
        }
        catch (Exception e) when (e is IOException or NotSupportedException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            _services.ReportError("The picture could not be read", e);
        }
    }

    private void OnOption(object sender, RoutedEventArgs e)
    {
        if (_quiet || !IsLoaded)
            return;
        ShowValues();
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnOptionClick(object sender, RoutedEventArgs e) => OnOption(sender, e);

    private void ShowValues()
    {
        ToleranceValue.Text = $"{ToleranceSlider.Value:F0}";
        DetailValue.Text = $"{DetailSlider.Value:F2}";
        ClearText.Text = $"{ClearSlider.Value:F0}";
        HeightValue.Text = $"{HeightSlider.Value:F2}";
        SoftnessValue.Text = $"{SoftnessSlider.Value:F0}";
        GrainValue.Text = $"{GrainSlider.Value:F2}";
        CoeffValue.Text = $"{CoeffSlider.Value:F0}";
        CoeffSlider.IsEnabled = MarkCoeff.IsChecked == true;
    }

    private Task<RgbaImage> PlainBase(VirtualFileSystem vfs, string brand, string suffix)
    {
        if (!ReferenceEquals(_plainFor, vfs))
        {
            _plain.Clear();
            _plainFor = vfs;
        }
        return _plain.GetOrAdd((brand, suffix), key => new Lazy<Task<RgbaImage>>(() => Task.Run(() => JerseyMaps.PlainBase(vfs, key.Brand, key.Suffix)))).Value;
    }

    private async Task<RgbaImage> BaseFor(BaseKind kind, string suffix, SheetSet loaded, VirtualFileSystem vfs)
    {
        switch (kind)
        {
            case BaseKind.Original:
                return suffix == "nm" ? loaded.OriginalNormal : loaded.OriginalCoeff;
            case BaseKind.Blank when _services.Settings.JerseyBlanks.TryGetValue($"{loaded.Sheets.Brand}_{suffix}", out var file):
                var image = await Task.Run(() => ImageFiles.Load(file));
                var size = suffix == "nm" ? loaded.OriginalNormal : loaded.OriginalCoeff;
                return image.Width == size.Width && image.Height == size.Height ? image : await Task.Run(() => TexturePipeline.Resize(image, size.Width, size.Height));
            default:
                return await PlainBase(vfs, loaded.Sheets.Brand, suffix);
        }
    }

    private static BaseKind KindOf(ComboBox box) => box.SelectedIndex switch { 1 => BaseKind.Original, BlankItem => BaseKind.Blank, _ => BaseKind.Plain };

    /// <summary>Makes both maps with the current options. The newest request wins.</summary>
    private async void Make()
    {
        if (_loaded is not { } loaded || _services.Workspace.Vfs is not { } vfs)
            return;
        if (FromPicture && _picture == null)
        {
            _made = null;
            IsBusy = false;
            UpdateButtons();
            Preview.ShowMessage("Choose a picture of what is raised, or drop one here. Its transparency, or else its brightness, marks the design.");
            return;
        }

        var generation = ++_makeGeneration;
        var options = Options;
        var (normalKind, coeffKind) = (KindOf(NormalBaseBox), KindOf(CoeffBaseBox));
        var picture = FromPicture ? _picture : null;
        IsBusy = true;
        UpdateButtons();
        if (_made == null)
            Preview.ShowMessage(normalKind == BaseKind.Plain && !_plain.ContainsKey((loaded.Sheets.Brand, "nm"))
                ? $"Building the plain {loaded.Sheets.BrandLabel} base from the game's jerseys" : "Making the maps");
        try
        {
            var normalBase = await BaseFor(normalKind, "nm", loaded, vfs);
            var coeffBase = await BaseFor(coeffKind, "coeff", loaded, vfs);
            var made = await Task.Run(() =>
            {
                var areas = picture != null
                    ? JerseyMaps.FromPicture(picture, normalBase.Width, normalBase.Height)
                    : JerseyMaps.FromColour(loaded.Colour, options.Tolerance, normalBase.Width, normalBase.Height);
                return new Made(areas, JerseyMaps.Normal(normalBase, areas, options), JerseyMaps.Coeff(coeffBase, areas, options), options);
            });
            if (generation != _makeGeneration || !ReferenceEquals(loaded, _loaded))
                return;
            var first = _made == null;
            _made = made;
            ShowView(_view, keepView: !first);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            if (generation != _makeGeneration)
                return;
            _made = null;
            Preview.ShowMessage("The maps could not be made: " + e.Message);
            _services.Log.Warning($"Jersey maps for {loaded.Sheets.Colour}: {e.Message}");
        }
        finally
        {
            if (generation == _makeGeneration)
            {
                IsBusy = false;
                UpdateButtons();
            }
        }
    }

    private void OnView(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string view })
            ShowView(view, keepView: true);
    }

    /// <summary>Shows one of: Colour, Raised, Normal, Coeff, GameNormal, GameCoeff.</summary>
    public void ShowView(string view, bool keepView = false)
    {
        _view = view;
        foreach (var chip in Views.Children.OfType<ToggleButton>())
            chip.IsChecked = (string)chip.Tag == view;
        if (_loaded is not { } loaded)
            return;

        var (image, note) = view switch
        {
            "Colour" => (loaded.Colour, "The colour sheet the maps are made from" + (_services.Workspace.IsEdited(loaded.Sheets.Colour) ? ", as edited in this project." : ", as the game has it.")),
            "Raised" => (_made is { } m ? JerseyMaps.MaskImage(JerseyMaps.HeightOf(m.Areas, m.Options.InnerDetail), m.Areas.Width, m.Areas.Height) : null,
                "White is raised. Grey steps inside come from inner detail."),
            "Coeff" => (_made?.Coeff, "Raised areas are one grey in all three channels, as in the game's crests. Use R, G and B above to see each channel."),
            "GameNormal" => (loaded.CurrentNormal, loaded.NormalEdited ? "The normal map now in this project." : "The game's normal map for this jersey."),
            "GameCoeff" => (loaded.CurrentCoeff, loaded.CoeffEdited ? "The coeff map now in this project." : "The game's coeff map for this jersey."),
            _ => (_made?.Normal, "Red and green hold the surface direction, as in the game's own maps. Blue is empty."),
        };
        ViewNote.Text = note;
        if (image != null)
            Preview.SetImage(image, keepView);
    }

    private void OnApply(object sender, RoutedEventArgs e) => _ = ApplyAsync();

    /// <summary>Puts both maps in the project as one undo step, each in its game file's size and format.</summary>
    public async Task ApplyAsync()
    {
        if (_made is not { } made || _loaded is not { } loaded || _services.Workspace.Vfs is not { } vfs || _services.RequireProject() is not { } project)
            return;
        var sheets = loaded.Sheets;
        var quality = Quality;
        var name = Path.GetFileName(sheets.Colour).Replace("_cm.gnf", "");
        await _services.Jobs.RunAsync($"Saving maps for {name}", context =>
        {
            var items = new List<(string, byte[], string)>();
            foreach (var (path, image, what) in new[] { (sheets.Normal, made.Normal, "Jersey normal map"), (sheets.Coeff, made.Coeff, "Jersey coeff map") })
            {
                var original = vfs.Find(path) is { } file ? vfs.ReadContent(file) : throw new InvalidDataException($"{path} is not in the game.");
                if (TexturePipeline.ImportBlocker(original) is { } blocker)
                    throw new NotSupportedException($"{Path.GetFileName(path)}: {blocker}");
                context.Status($"Encoding {Path.GetFileName(path)}");
                items.Add((path, TexturePipeline.Import(original, image, quality).Gnf, what));
            }
            return items;
        }, items =>
        {
            project.SetMany(items, $"Jersey maps for {name}");
            _services.Notify($"Saved the normal and coeff maps for {name}.");
        });
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_made is not { } made || _loaded is not { } loaded || AppServices.PickFolder("Export the maps as PNG") is not { } folder)
            return;
        var name = Path.GetFileName(loaded.Sheets.Colour).Replace("_cm.gnf", "");
        _ = _services.Jobs.RunAsync($"Exporting maps for {name}", _ =>
        {
            File.WriteAllBytes(Path.Combine(folder, name + "_nm.png"), PngFile.Write(made.Normal));
            File.WriteAllBytes(Path.Combine(folder, name + "_coeff.png"), PngFile.Write(made.Coeff));
            return folder;
        }, path => _services.Notify($"Saved {name}_nm.png and {name}_coeff.png in {path}"));
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        if (_loaded is { } loaded && _services.Workspace.Project is { } project)
            project.Revert([loaded.Sheets.Normal, loaded.Sheets.Coeff], $"Reverted maps for {Path.GetFileName(loaded.Sheets.Colour).Replace("_cm.gnf", "")}");
    }
}
