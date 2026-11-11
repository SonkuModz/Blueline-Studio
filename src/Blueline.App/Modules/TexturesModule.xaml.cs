using System.Windows;
using System.Windows.Controls;
using BCnEncoder.Encoder;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core;
using Blueline.Formats.Textures;
using Microsoft.Win32;

namespace Blueline.App.Modules;

/// <summary>Texture workspace: preview, lossless export, import with format matching, and batches.</summary>
public partial class TexturesModule : UserControl, IEditorModule
{
    private readonly AppServices _services;
    private int _generation;

    public TexturesModule(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Browser.SetScope(r => r.Category == AssetCategory.Texture);
        Browser.SelectionChanged += ShowSelection;
        QualityBox.ItemsSource = new[] { "Fast", "Balanced", "Best" };
        QualityBox.SelectedIndex = 1;
        DropTarget.Attach(PreviewCard, f => File.Exists(f) && ImageFiles.IsSupported(f), files => _ = ImportFrom(files[0]), PreviewCard);
        services.Workspace.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible)
                ShowSelection();
        });
        ShowSelection();
    }

    public string Title => "Textures";
    public FrameworkElement View => this;
    public AssetBrowser BrowserControl => Browser;
    public TexturePreview PreviewControl => Preview;
    public string SummaryLine => SummaryText.Text;

    public void Activated() => Browser.Load(_services.Index);

    public void OpenPath(string path)
    {
        Browser.Load(_services.Index);
        Browser.Reveal(path);
    }

    private CompressionQuality Quality => QualityBox.SelectedIndex switch
    {
        0 => CompressionQuality.Fast,
        2 => CompressionQuality.BestQuality,
        _ => CompressionQuality.Balanced,
    };

    private void ShowSelection()
    {
        var generation = ++_generation;
        var row = Browser.Selected;
        var has = row != null;
        ExportPng.IsEnabled = ExportDds.IsEnabled = ShowTeamButton.IsEnabled = has;
        ExportSelected.IsEnabled = Browser.SelectedRows.Count > 1;
        ImportButton.IsEnabled = false;
        RevertButton.IsEnabled = has && _services.Workspace.IsEdited(row!.Path);
        BlockerText.Visibility = Visibility.Collapsed;

        if (row == null)
        {
            NameText.Text = "None selected";
            SummaryText.Text = StateText.Text = "";
            CompareRow.Visibility = Visibility.Collapsed;
            Preview.ShowMessage("Select a texture to preview it.");
            return;
        }

        var edited = _services.Workspace.IsEdited(row.Path);
        var original = ShowOriginal.IsChecked == true && edited;
        NameText.Text = row.Name;
        StateText.Text = edited ? (original ? "Showing the original" : "Edited in this project") : "";
        CompareRow.Visibility = edited ? Visibility.Visible : Visibility.Collapsed;

        var workspace = _services.Workspace;
        var path = row.Path;
        var file = row.File;
        Task.Run(() =>
        {
            var content = original && file != null ? workspace.Vfs!.ReadContent(file) : workspace.ReadCurrent(path) ?? [];
            if (ImageFiles.IsPlain(content))
                return ((GameTexture?)null, (string?)null, ImageFiles.Decode(content), PngFile.IsPng(content) ? "PNG image" : "JPEG image");
            var stock = file != null ? workspace.Vfs!.ReadContent(file) : content;
            return (GameTexture.TryOpen(content), TexturePipeline.ImportBlocker(stock), (RgbaImage?)null, "");
        }).ContinueWith(task =>
        {
            if (generation != _generation)
                return;
            if (task.IsFaulted)
            {
                SummaryText.Text = "";
                Preview.ShowMessage("This file could not be read: " + task.Exception!.GetBaseException().Message);
                return;
            }
            var (texture, blocker, plain, label) = task.Result;
            if (plain != null)
            {
                SummaryText.Text = $"{plain.Width}x{plain.Height}, {label}";
                Preview.SetImage(plain);
                ImportButton.IsEnabled = true;
                ExportDds.IsEnabled = false;
                return;
            }
            if (texture == null)
            {
                SummaryText.Text = "Not a GNF or DDS texture";
                Preview.ShowMessage("This file is not a GNF or DDS texture, or it is empty.");
                return;
            }
            SummaryText.Text = texture.Summary;
            Preview.SetTexture(texture);
            ImportButton.IsEnabled = blocker == null;
            if (blocker != null)
            {
                BlockerText.Text = blocker;
                BlockerText.Visibility = Visibility.Visible;
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnCompare(object sender, RoutedEventArgs e) => ShowSelection();

    private void OnShowTeam(object sender, RoutedEventArgs e)
    {
        if (Browser.Selected is { } row)
            _services.Navigate("Teams", row.Path);
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose an image", Filter = ImageFiles.OpenFilter };
        if (dialog.ShowDialog() == true)
            _ = ImportFrom(dialog.FileName);
    }

    public async Task ImportFrom(string source)
    {
        if (Browser.Selected is not { File: { } file } row)
        {
            _services.ReportError("Nothing to import into", "Select a texture first, then drop the image on the preview.");
            return;
        }
        if (_services.RequireProject() is not { } project)
            return;

        var vfs = _services.Workspace.Vfs!;
        var quality = Quality;
        await _services.Jobs.RunAsync($"Importing {Path.GetFileName(source)}", context =>
        {
            var original = vfs.ReadContent(file);
            var image = ImageFiles.Load(source);
            context.Status($"Encoding {row.Name}");
            if (!ImageFiles.IsPlain(original))
                return TexturePipeline.Import(original, image, quality);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var encoded = ImageFiles.EncodeLike(original, image, out var resized);
            return new TextureImportResult(encoded, resized, clock.Elapsed);
        }, result =>
        {
            project.Set(row.Path, result.Gnf, $"Texture from {Path.GetFileName(source)}");
            _services.Notify($"Imported into {row.Name} in {result.Elapsed.TotalSeconds:F1}s" + (result.Resized ? ", resized to match" : ""));
            ShowOriginal.IsChecked = false;
            ShowSelection();
        });
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        if (Browser.Selected is { } row && _services.Workspace.Project is { } project)
            project.Revert([row.Path], $"Reverted {row.Name}");
    }

    private void OnExportPng(object sender, RoutedEventArgs e)
    {
        if (Browser.Selected is not { } row)
            return;
        var dialog = new SaveFileDialog { Title = "Export PNG", FileName = Path.ChangeExtension(row.Name, ".png"), Filter = "PNG image|*.png" };
        if (dialog.ShowDialog() == true)
            _ = ExportPngTo(row, dialog.FileName);
    }

    public Task<bool> ExportPngTo(AssetRow row, string destination)
    {
        var workspace = _services.Workspace;
        return _services.Jobs.RunAsync($"Exporting {row.Name}", _ =>
        {
            var image = ImageFiles.DecodeAny(workspace.ReadCurrent(row.Path) ?? []) ?? throw new InvalidDataException($"{row.Name} is not a texture.");
            File.WriteAllBytes(destination, PngFile.Write(image));
            return destination;
        }, path => _services.Notify($"Saved {path}"));
    }

    private void OnExportDds(object sender, RoutedEventArgs e)
    {
        if (Browser.Selected is not { } row)
            return;
        var dialog = new SaveFileDialog { Title = "Export DDS", FileName = Path.ChangeExtension(row.Name, ".dds"), Filter = "DDS texture|*.dds" };
        if (dialog.ShowDialog() != true)
            return;
        var workspace = _services.Workspace;
        var destination = dialog.FileName;
        _ = _services.Jobs.RunAsync($"Exporting {row.Name}", _ =>
        {
            var content = workspace.ReadCurrent(row.Path) ?? [];
            if (DdsFile.IsDds(content))
            {
                File.WriteAllBytes(destination, content);
                return destination;
            }
            var gnf = GnfTexture.Parse(content);
            if (gnf.DecodeBlocker() is { } blocker)
                throw new NotSupportedException(blocker);
            if (gnf.Info.Faces != 1)
                throw new NotSupportedException("Cubemaps and arrays export as PNG, one face at a time. DDS export covers 2D textures.");
            var mips = Enumerable.Range(0, gnf.Info.MipCount).Select(m => gnf.DetiledSurface(m, 0)).ToList();
            File.WriteAllBytes(destination, DdsFile.Create(gnf.Info.Width, gnf.Info.Height, mips, gnf.Info.Codec, gnf.Info.IsSrgb, gnf.Info.IsSigned));
            return destination;
        }, path => _services.Notify($"Saved {path}"));
    }

    private void OnExportSelected(object sender, RoutedEventArgs e)
    {
        var rows = Browser.SelectedRows;
        if (AppServices.PickFolder($"Export {rows.Count} textures as PNG") is not { } folder)
            return;
        var workspace = _services.Workspace;
        _ = _services.Jobs.RunAsync($"Exporting {rows.Count} textures", context =>
        {
            var done = 0;
            var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.ForEach(rows, new ParallelOptions { CancellationToken = context.Cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, row =>
            {
                try
                {
                    var image = ImageFiles.DecodeAny(workspace.ReadCurrent(row.Path) ?? []) ?? throw new InvalidDataException("not a texture");
                    var target = Path.Combine(folder, Path.ChangeExtension(row.Path, ".png").Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, PngFile.Write(image));
                }
                catch (Exception e2) when (e2 is not OperationCanceledException)
                {
                    failed.Add($"{row.Path}: {e2.Message}");
                }
                context.Progress("Exporting textures", Interlocked.Increment(ref done), rows.Count);
            });
            return failed.ToList();
        }, failed =>
        {
            foreach (var failure in failed)
                _services.Log.Warning($"Export failed: {failure}");
            _services.Notify($"Exported {rows.Count - failed.Count} of {rows.Count} textures." + (failed.Count > 0 ? " See the log for the rest." : ""));
        });
    }

    private void OnBatch(object sender, RoutedEventArgs e)
    {
        if (AppServices.PickFolder("Choose a folder of images to import") is { } folder)
            _ = BatchFrom(folder);
    }

    public async Task BatchFrom(string folder)
    {
        if (_services.Workspace.Vfs is not { } vfs || _services.RequireProject() is not { } project)
            return;

        var quality = Quality;
        await _services.Jobs.RunAsync("Importing folder", context =>
            BatchTextureImport.Run(folder, vfs, project, quality, ImageFiles.Load, new Reporter(context), context.Cancel),
            results =>
            {
                var groups = results.GroupBy(r => r.Outcome).OrderBy(g => g.Key);
                BatchResults.ItemsSource = groups.Select(g => $"{g.Count()}  {g.Key}").ToList();
                foreach (var item in results.Where(r => r.Outcome != BatchOutcome.Imported))
                    _services.Log.Warning($"{Path.GetFileName(item.Source)}: {item.Outcome}. {item.Detail}");
                _services.Notify($"Folder import finished: {results.Count(r => r.Outcome == BatchOutcome.Imported)} of {results.Count} imported. Details are in the log.");
            });
    }

    private sealed class Reporter(JobContext context) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => context.Progress($"Encoding {value.Done} of {value.Total}", value.Done, value.Total);
    }
}
