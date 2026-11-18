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

namespace Blueline.App.Modules;

/// <summary>Makes a number font's normal (`_nm`) and shine (`_sm`) maps from its colour sheet, one font or every edited one.</summary>
public partial class FontMapsModule : UserControl, IEditorModule
{
    /// <summary>The selected font's sheets, decoded once per selection. A font with no normal map in the game has none here.</summary>
    private sealed record SheetSet(FontSheets Sheets, RgbaImage Colour, RgbaImage? CurrentNormal, RgbaImage? CurrentSm, bool NormalEdited, bool SmEdited);

    private sealed record Made(FontLayers Layers, RgbaImage Normal, RgbaImage Sm);

    private const string ChooseMessage = "Choose a font colour sheet on the left. Its normal and sm maps are made from it.";

    private readonly AppServices _services;
    private readonly DispatcherTimer _debounce;
    private SheetSet? _loaded;
    private Made? _made;
    private string _view = "Normal";
    private int _loadGeneration, _makeGeneration;
    private bool _quiet;

    public FontMapsModule(AppServices services)
    {
        _services = services;
        _quiet = true;
        InitializeComponent();
        Browser.SetScope(r => r.Category == AssetCategory.Texture && FontSheets.IsColourSheet(r.Path));
        Browser.SelectionChanged += LoadSelection;
        QualityBox.ItemsSource = new[] { "Fast", "Balanced", "Best" };
        QualityBox.SelectedIndex = 1;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Make();
        };
        services.Workspace.Changed += () => Dispatcher.BeginInvoke(OnWorkspaceChanged);
        _quiet = false;
        ShowValues();
        Clear(ChooseMessage);
    }

    public string Title => "Font Maps";
    public FrameworkElement View => this;
    public AssetBrowser BrowserControl => Browser;
    public TexturePreview PreviewControl => Preview;

    public bool IsBusy { get; private set; }
    public bool HasMaps => _made != null;

    public void Activated() => Browser.Load(_services.Index);

    public void OpenPath(string path)
    {
        Browser.Load(_services.Index);
        ShowView("Normal");
        var colour = path.Replace("_nm.gnf", "_cm.gnf").Replace("_sm.gnf", "_cm.gnf");
        if (FontSheets.IsColourSheet(colour))
            Browser.Reveal(colour);
    }

    private CompressionQuality Quality => QualityBox.SelectedIndex switch
    {
        0 => CompressionQuality.Fast,
        2 => CompressionQuality.BestQuality,
        _ => CompressionQuality.Balanced,
    };

    public FontMapOptions Options => new(
        Depth: DepthSlider.Value,
        Bevel: BevelSlider.Value,
        LayerStep: StepSlider.Value,
        Tolerance: (int)ToleranceSlider.Value,
        Grain: GrainSlider.Value,
        FillValue: (byte)FillGreySlider.Value,
        OutlineValue: (byte)OutlineGreySlider.Value,
        BottomRowValue: (byte)BottomGreySlider.Value,
        BrighterBottomRow: BottomBox.IsChecked == true,
        Stitch: StitchBox.IsChecked == true);

    private void OnWorkspaceChanged()
    {
        if (_loaded != null && IsVisible)
            LoadSelection();
        UpdateButtons();
    }

    private void Clear(string message)
    {
        _loaded = null;
        _made = null;
        IsBusy = false;
        NameText.Text = "None selected";
        KitText.Text = StateText.Text = ViewNote.Text = "";
        FillSwatch.Background = null;
        Preview.ShowMessage(message);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        ApplyButton.IsEnabled = ExportButton.IsEnabled = _made != null && !IsBusy;
        RevertButton.IsEnabled = _loaded is { } l && (l.NormalEdited || l.SmEdited);
        AllButton.IsEnabled = EditedFonts().Count > 0;
        BottomGreySlider.IsEnabled = BottomBox.IsChecked == true;
    }

    /// <summary>Font colour sheets this project changes.</summary>
    private IReadOnlyList<string> EditedFonts() =>
        _services.Workspace.Project is { } project ? project.Changes.Select(c => c.Path).Where(FontSheets.IsColourSheet).ToList() : [];

    private void LoadSelection()
    {
        var generation = ++_loadGeneration;
        if (Browser.Selected is not { } row || FontSheets.FromColour(row.Path) is not { } sheets || _services.Workspace.Vfs is null)
        {
            Clear(ChooseMessage);
            return;
        }

        var workspace = _services.Workspace;
        NameText.Text = row.Name.Replace("_cm.gnf", "");
        KitText.Text = $"{sheets.BrandLabel}: making {Path.GetFileName(sheets.Normal)} and {Path.GetFileName(sheets.Sm)}";
        StateText.Text = "";
        IsBusy = true;
        if (_loaded?.Sheets != sheets)
            Preview.ShowMessage("Reading the sheets");

        Task.Run(() =>
        {
            RgbaImage? Decode(string path) => workspace.ReadCurrent(path) is { } content && GameTexture.TryOpen(content) is { } texture ? texture.DecodeMip(0) : null;
            var colour = Decode(sheets.Colour) ?? throw new InvalidDataException($"{sheets.Colour} is not in the game.");
            return new SheetSet(sheets, colour, Decode(sheets.Normal), Decode(sheets.Sm), workspace.IsEdited(sheets.Normal), workspace.IsEdited(sheets.Sm));
        }).ContinueWith(task =>
        {
            if (generation != _loadGeneration)
                return;
            if (task.IsFaulted)
            {
                Clear("This font's sheets could not be read: " + task.Exception!.GetBaseException().Message);
                return;
            }
            _loaded = task.Result;
            StateText.Text = (_loaded.NormalEdited, _loaded.SmEdited, _loaded.CurrentNormal == null) switch
            {
                (_, _, true) => "The game has no normal map for this font, so only the sm map is saved",
                (true, true, _) => "Both maps are edited in this project",
                (true, false, _) => "The normal map is edited in this project",
                (false, true, _) => "The sm map is edited in this project",
                _ => "",
            };
            Make();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnOption(object sender, RoutedEventArgs e)
    {
        if (_quiet || !IsLoaded)
            return;
        ShowValues();
        UpdateButtons();
        _debounce.Stop();
        _debounce.Start();
    }

    private void ShowValues()
    {
        ToleranceValue.Text = $"{ToleranceSlider.Value:F0}";
        StepValue.Text = $"{StepSlider.Value:F2}";
        DepthValue.Text = $"{DepthSlider.Value:F2}";
        BevelValue.Text = $"{BevelSlider.Value:F0}";
        GrainValue.Text = $"{GrainSlider.Value:F2}";
        FillGreyValue.Text = $"{FillGreySlider.Value:F0}";
        OutlineGreyValue.Text = $"{OutlineGreySlider.Value:F0}";
        BottomGreyValue.Text = $"{BottomGreySlider.Value:F0}";
    }

    /// <summary>Makes both maps with the current options at the game's 1024 size, or the sm map's own size. The newest request wins.</summary>
    private async void Make()
    {
        if (_loaded is not { } loaded)
            return;
        var generation = ++_makeGeneration;
        var options = Options;
        IsBusy = true;
        UpdateButtons();
        if (_made == null)
            Preview.ShowMessage("Making the maps");
        try
        {
            var size = loaded.CurrentSm ?? loaded.CurrentNormal ?? loaded.Colour;
            var made = await Task.Run(() =>
            {
                var layers = FontMaps.Layers(loaded.Colour, options.Tolerance, size.Width, size.Height);
                return new Made(layers, FontMaps.Normal(layers, options), FontMaps.Sm(layers, options));
            });
            if (generation != _makeGeneration || !ReferenceEquals(loaded, _loaded))
                return;
            var first = _made == null;
            _made = made;
            var fill = made.Layers.FillColour;
            FillSwatch.Background = new SolidColorBrush(Color.FromRgb(fill.R, fill.G, fill.B));
            FillText.Text = $"Fill colour  #{fill.R:X2}{fill.G:X2}{fill.B:X2}";
            ShowView(_view, keepView: !first);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            if (generation != _makeGeneration)
                return;
            _made = null;
            Preview.ShowMessage("The maps could not be made: " + e.Message);
            _services.Log.Warning($"Font maps for {loaded.Sheets.Colour}: {e.Message}");
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

    /// <summary>Shows one of: Colour, Layers, Normal, Sm, GameNormal, GameSm.</summary>
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
            "Layers" => (_made is { } m ? FontMaps.LayersImage(m.Layers) : null, "White is the fill, grey the outline, black is not a letter."),
            "Sm" => (_made?.Sm, "Black off the letters, a grey per layer with felt grain on them. The bottom row of small numbers can have its own grey."),
            "GameNormal" => (loaded.CurrentNormal, loaded.CurrentNormal == null ? "The game has no normal map for this font." : loaded.NormalEdited ? "The normal map now in this project." : "The game's normal map for this font."),
            "GameSm" => (loaded.CurrentSm, loaded.SmEdited ? "The sm map now in this project." : "The game's sm map for this font."),
            _ => (_made?.Normal, "Red and green hold the surface direction, as in the game's own maps. Blue is empty."),
        };
        ViewNote.Text = note;
        if (image != null)
            Preview.SetImage(image, keepView);
        else if (view == "GameNormal")
            Preview.ShowMessage("The game has no normal map for this font.");
    }

    private void OnApply(object sender, RoutedEventArgs e) => _ = ApplyAsync();

    /// <summary>Puts both maps in the project as one undo step, each in its game file's size and format.</summary>
    public async Task ApplyAsync()
    {
        if (_made is null || _loaded is not { } loaded || _services.Workspace.Vfs is not { } vfs || _services.RequireProject() is not { } project)
            return;
        var (sheets, colour, options, quality) = (loaded.Sheets, loaded.Colour, Options, Quality);
        var name = Path.GetFileName(sheets.Colour).Replace("_cm.gnf", "");
        await _services.Jobs.RunAsync($"Saving maps for {name}", context =>
        {
            context.Status($"Encoding the maps for {name}");
            return FontMaps.Encode(sheets, colour, Original(vfs), options, quality);
        }, items =>
        {
            project.SetMany(items, $"Font maps for {name}");
            _services.Notify($"Saved {items.Count} maps for {name}.");
        });
    }

    private static Func<string, byte[]?> Original(VirtualFileSystem vfs) => path => vfs.Find(path) is { } file ? vfs.ReadContent(file) : null;

    private void OnAll(object sender, RoutedEventArgs e) => _ = MakeAllAsync();

    /// <summary>Makes maps for every font colour sheet the project changes, with the current options, as one undo step.</summary>
    public async Task MakeAllAsync()
    {
        if (_services.Workspace.Vfs is not { } vfs || _services.RequireProject() is not { } project)
            return;
        var fonts = EditedFonts();
        if (fonts.Count == 0)
            return;
        if (!ConfirmDialog.Ask("Make maps for all edited fonts?",
                $"This makes the normal and sm maps for the {fonts.Count} font colour sheets this project changes, with the options shown, and replaces any maps it already has for them. It is one undo step.",
                "Make maps"))
            return;
        var (options, quality, workspace) = (Options, Quality, _services.Workspace);
        await _services.Jobs.RunAsync($"Making maps for {fonts.Count} fonts", context =>
        {
            var items = new System.Collections.Concurrent.ConcurrentBag<(string, byte[], string)>();
            var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
            var done = 0;
            Parallel.ForEach(fonts, new ParallelOptions { CancellationToken = context.Cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, path =>
            {
                try
                {
                    var sheets = FontSheets.FromColour(path)!;
                    var colour = GameTexture.TryOpen(workspace.ReadCurrent(path)!)?.DecodeMip(0) ?? throw new InvalidDataException("not a texture");
                    foreach (var item in FontMaps.Encode(sheets, colour, Original(vfs), options, quality))
                        items.Add(item);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or ArgumentException)
                {
                    failed.Add($"{Path.GetFileName(path)}: {e.Message}");
                }
                context.Progress("Making font maps", Interlocked.Increment(ref done), fonts.Count);
            });
            return (Items: items.ToList(), Failed: failed.ToList());
        }, result =>
        {
            if (result.Items.Count > 0)
                project.SetMany(result.Items, $"Font maps for {fonts.Count} fonts");
            foreach (var failure in result.Failed)
                _services.Log.Warning("Font maps: " + failure);
            _services.Notify($"Saved {result.Items.Count} maps for {fonts.Count - result.Failed.Count} fonts" + (result.Failed.Count > 0 ? $"; {result.Failed.Count} failed, see the log." : "."));
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
            File.WriteAllBytes(Path.Combine(folder, name + "_sm.png"), PngFile.Write(made.Sm));
            return folder;
        }, path => _services.Notify($"Saved {name}_nm.png and {name}_sm.png in {path}"));
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        if (_loaded is { } loaded && _services.Workspace.Project is { } project)
            project.Revert([loaded.Sheets.Normal, loaded.Sheets.Sm], $"Reverted maps for {Path.GetFileName(loaded.Sheets.Colour).Replace("_cm.gnf", "")}");
    }
}
