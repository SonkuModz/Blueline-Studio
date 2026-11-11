using System.Text;
using System.Windows;
using System.Windows.Controls;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core;
using Blueline.Formats.Detect;
using Blueline.Formats.Textures;
using Microsoft.Win32;

namespace Blueline.App.Modules;

/// <summary>The whole game as files: browse, identify, preview, export and replace anything.</summary>
public partial class AssetsModule : UserControl, IEditorModule
{
    private const int MaxPreviewText = 4 << 20;
    private const int HexBytes = 16 << 10;
    private readonly AppServices _services;
    private int _generation;
    private bool _loadingText;
    private string _targetTab = "";

    public AssetsModule(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Browser.SelectionChanged += ShowSelection;
        DropTarget.Attach(PreviewCard, File.Exists, files => ReplaceFrom(files[0]), PreviewCard);
        services.Workspace.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible)
                ShowSelection();
        });
        ShowSelection();
    }

    public string Title => "Assets";
    public FrameworkElement View => this;
    public AssetBrowser BrowserControl => Browser;
    public TexturePreview Preview => Texture;
    public VideoPlayer VideoControl => Video;
    public FontPreview FontControl => FontView;

    public void Activated() => Browser.Load(_services.Index);

    public void OpenPath(string path)
    {
        Browser.Load(_services.Index);
        Browser.Reveal(path);
    }

    private void Show(UIElement? element, string? placeholder = null)
    {
        foreach (var candidate in new UIElement[] { Texture, TextView, HexView, Video, FontView })
            candidate.Visibility = candidate == element ? Visibility.Visible : Visibility.Collapsed;
        Placeholder.Visibility = element == null ? Visibility.Visible : Visibility.Collapsed;
        if (placeholder != null)
            Placeholder.Text = placeholder;
    }

    private void ShowSelection()
    {
        var generation = ++_generation;
        var row = Browser.Selected;
        var count = Browser.SelectedRows.Count;
        Video.Clear();

        SaveTextButton.Visibility = Visibility.Collapsed;
        OpenInButton.Visibility = Visibility.Collapsed;
        ExportButton.IsEnabled = row != null;
        ExportButton.Content = count > 1 ? $"Export {count} files" : "Export";
        ReplaceButton.IsEnabled = row?.File is { IsLoose: false } || row?.File == null && row != null;
        ReplaceButton.ToolTip = row?.File is { IsLoose: true }
            ? "Loose files cannot be replaced yet: their priority against archives has not been verified."
            : "Use a file from disk for this path in the project. You can also drop a file here.";
        RevertButton.IsEnabled = row != null && _services.Workspace.IsEdited(row.Path);

        if (row == null)
        {
            NameText.Text = "No file selected";
            PathText.Text = FactsText.Text = LayersText.Text = "";
            Show(null, "Select a file to preview it.");
            return;
        }

        NameText.Text = row.Name;
        PathText.Text = row.Path;
        FactsText.Text = "Reading";
        LayersText.Text = "";
        Show(null, "Loading");

        var workspace = _services.Workspace;
        var path = row.Path;
        var file = row.File;
        var vfs = workspace.Vfs;

        Task.Run(() =>
        {
            var content = workspace.ReadCurrent(path) ?? [];
            var (kind, label) = FormatDetector.Identify(content, path);
            var wrapped = file != null && vfs != null && vfs.IsWrapped(file);
            var layers = file != null && vfs != null ? vfs.Layers(path).Select(l => l.SourceName).ToList() : [];
            var texture = kind == AssetKind.Texture ? GameTexture.TryOpen(content) : null;
            var image = kind == AssetKind.Image ? TryDecodeImage(content) : null;
            return (content, kind, label, wrapped, layers, texture, image);
        }).ContinueWith(task =>
        {
            if (generation != _generation)
                return;
            if (task.IsFaulted)
            {
                FactsText.Text = "";
                Show(null, "This file could not be read: " + task.Exception!.GetBaseException().Message);
                return;
            }
            var (content, kind, label, wrapped, layers, texture, image) = task.Result;
            var edited = workspace.IsEdited(path);
            var facts = new List<string> { label, FormatSize.Of(content.Length) + (file != null && wrapped ? $" ({FormatSize.Of(file.StoredSize)} stored, chunkzip)" : "") };
            if (edited) facts.Add("edited in this project");
            FactsText.Text = string.Join("  ·  ", facts);
            LayersText.Text = layers.Count > 0 ? $"Game uses {layers[^1]}" + (layers.Count > 1 ? $", overriding {string.Join(", ", layers.Take(layers.Count - 1))}" : "") : file?.IsLoose == true ? "Loose file on disk" : "Not in the stock game";
            ShowContent(path, content, kind, texture, image);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static RgbaImage? TryDecodeImage(byte[] content)
    {
        try
        {
            return ImageFiles.Decode(content);
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private void ShowContent(string path, byte[] content, AssetKind kind, GameTexture? texture, RgbaImage? image)
    {
        if (image != null)
        {
            Show(Texture);
            Texture.SetImage(image);
            FactsText.Text += $"  ·  {image.Width}x{image.Height}";
            OfferModule("Open in Textures", "Textures");
            return;
        }

        if (kind == AssetKind.Video)
        {
            Show(Video);
            _ = Video.ShowAsync(_services, content, Path.GetExtension(path), VideoTrack.Picture);
            OfferModule("Open in Video", "Video");
            return;
        }

        if (kind == AssetKind.Font && FontView.Show(content))
        {
            Show(FontView);
            FactsText.Text += "  ·  " + FontView.Description;
            return;
        }

        if (kind == AssetKind.Texture && texture != null)
        {
            Show(Texture);
            Texture.SetTexture(texture);
            FactsText.Text += "  ·  " + texture.Summary;
            OfferModule("Open in Textures", "Textures");
            return;
        }

        if (kind == AssetKind.Text && content.Length < MaxPreviewText)
        {
            _loadingText = true;
            TextView.Text = Encoding.UTF8.GetString(content);
            _loadingText = false;
            TextView.IsReadOnly = false;
            Show(TextView);
            return;
        }

        if (kind == AssetKind.Database)
            OfferModule("Open in Database", "Database");
        if (kind == AssetKind.Audio)
            OfferModule("Open in Audio", "Audio");

        HexView.Text = HexDump(content);
        Show(HexView);
    }

    private void OfferModule(string label, string tab)
    {
        _targetTab = tab;
        OpenInButton.Content = label;
        OpenInButton.Visibility = Visibility.Visible;
    }

    private void OnOpenIn(object sender, RoutedEventArgs e)
    {
        if (Browser.Selected is { } row)
            _services.Navigate(_targetTab, row.Path);
    }

    private static string HexDump(byte[] content)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < Math.Min(content.Length, HexBytes); i += 16)
        {
            var line = content.AsSpan(i, Math.Min(16, content.Length - i));
            builder.Append(i.ToString("X8")).Append("  ");
            for (var k = 0; k < 16; k++)
                builder.Append(k < line.Length ? line[k].ToString("X2") + " " : "   ");
            builder.Append(' ');
            foreach (var b in line)
                builder.Append(b is >= 32 and < 127 ? (char)b : '.');
            builder.AppendLine();
        }
        if (content.Length > HexBytes)
            builder.AppendLine($"... showing the first {FormatSize.Of(HexBytes)} of {FormatSize.Of(content.Length)}");
        return builder.ToString();
    }

    private void OnTextEdited(object sender, TextChangedEventArgs e)
    {
        if (!_loadingText)
            SaveTextButton.Visibility = Visibility.Visible;
    }

    private void OnSaveText(object sender, RoutedEventArgs e)
    {
        if (Browser.Selected is not { } row || _services.RequireProject() is not { } project)
            return;
        project.Set(row.Path, new UTF8Encoding(false).GetBytes(TextView.Text), "Edited text");
        _services.Notify($"Saved {row.Name} to the project.");
    }

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose the replacement file" };
        if (dialog.ShowDialog() == true)
            ReplaceFrom(dialog.FileName);
    }

    private void ReplaceFrom(string source)
    {
        if (Browser.Selected is not { } row)
        {
            _services.ReportError("Nothing to replace", "Select a game file first, then drop the replacement onto the preview.");
            return;
        }
        if (row.File?.IsLoose == true)
        {
            _services.ReportError("Loose files cannot be replaced yet", "Their priority against archives has not been verified, so a replacement might silently do nothing.");
            return;
        }
        if (_services.RequireProject() is not { } project)
            return;

        var content = File.ReadAllBytes(source);
        var original = row.File != null ? _services.Workspace.Vfs?.ReadHead(row.File, 16) : null;
        if (original != null && FormatDetector.Identify(original, row.Path).Kind != FormatDetector.Identify(content, row.Path).Kind)
        {
            if (!ConfirmDialog.Ask("Different file type",
                    $"{Path.GetFileName(source)} does not look like the same kind of file as {row.Name}. The game may fail when it loads this.", "Replace anyway"))
                return;
        }

        project.Set(row.Path, content, $"Replaced with {Path.GetFileName(source)}");
        _services.Notify($"{row.Name} now uses {Path.GetFileName(source)} in this project.");
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        if (Browser.Selected is { } row && _services.Workspace.Project is { } project)
            project.Revert([row.Path], $"Reverted {row.Name}");
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var rows = Browser.SelectedRows;
        if (rows.Count == 0)
            return;

        if (rows.Count == 1)
        {
            var dialog = new SaveFileDialog { FileName = rows[0].Name, Title = "Export file" };
            if (dialog.ShowDialog() != true)
                return;
            ExportTo(rows, dialog.FileName, null);
        }
        else if (AppServices.PickFolder($"Export {rows.Count} files, keeping their game folders") is { } folder)
        {
            ExportTo(rows, null, folder);
        }
    }

    public Task<bool> ExportTo(IReadOnlyList<AssetRow> rows, string? file, string? folder)
    {
        var workspace = _services.Workspace;
        return _services.Jobs.RunAsync($"Exporting {rows.Count} file{(rows.Count == 1 ? "" : "s")}", context =>
        {
            for (var i = 0; i < rows.Count; i++)
            {
                context.Cancel.ThrowIfCancellationRequested();
                var target = file ?? Path.Combine(folder!, rows[i].Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, workspace.ReadCurrent(rows[i].Path) ?? []);
                context.Progress($"Exported {rows[i].Name}", i + 1, rows.Count);
            }
            return rows.Count;
        }, count => _services.Notify($"Exported {count} file{(count == 1 ? "" : "s")}."));
    }
}
