using System.Windows;
using System.Windows.Controls;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core;
using Blueline.Formats.Video;
using Microsoft.Win32;

namespace Blueline.App.Modules;

/// <summary>The game's videos: watch them and their mattes, export them, and replace them. RESEARCH section 6.8.</summary>
public partial class VideoModule : UserControl, IEditorModule
{
    private const string VideoFilter = "Videos (*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v)|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v|All files|*.*";
    private const string AviFilter = "AVI encoded as VP60 (*.avi)|*.avi";
    private const double LengthWarningSeconds = 0.5;

    private readonly AppServices _services;
    private int _generation;
    private byte[]? _content;
    private VideoInfo? _info;
    private string? _shownHash;

    public VideoModule(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Browser.SetScope(r => r.Category == AssetCategory.Video);
        Browser.SelectionChanged += () => _ = ShowSelection();
        DropTarget.Attach(PlayerCard, File.Exists, files => _ = ReplaceFrom(files[0]), PlayerCard);
        services.Workspace.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && Browser.Selected is { } row && Hash(row.Path) != _shownHash)
                _ = ShowSelection();
        });
        Clear();
    }

    public string Title => "Video";
    public FrameworkElement View => this;
    public AssetBrowser BrowserControl => Browser;
    public VideoPlayer PlayerControl => Player;
    public VideoInfo? Info => _info;
    public bool IsLoading { get; private set; }

    public void Activated()
    {
        Browser.Load(_services.Index);
        ToolNote.Text = _services.Ffmpeg == null ? VideoPlayer.NoToolMessage : "";
        ToolNote.Visibility = _services.Ffmpeg == null ? Visibility.Visible : Visibility.Collapsed;
    }

    public void OpenPath(string path)
    {
        Browser.Load(_services.Index);
        Browser.Reveal(path);
    }

    private VideoTrack Track => MatteChip.IsChecked == true ? VideoTrack.Matte : VideoTrack.Picture;

    private string Hash(string path) => $"{path}:{_services.Workspace.IsEdited(path)}:{_services.Workspace.Project?.ChangeCount}";

    private void Clear()
    {
        _content = null;
        _info = null;
        _shownHash = null;
        NameText.Text = "None selected";
        FactsText.Text = StateText.Text = "";
        TrackChoice.Visibility = NewMatte.Visibility = Visibility.Collapsed;
        ExportOriginal.IsEnabled = ExportMp4.IsEnabled = ReplaceButton.IsEnabled = RevertButton.IsEnabled = false;
        ReplaceButton.Content = "Replace video";
        ReplaceNote.Text = "Choose a video, or drop one on the player. It is encoded to match the game's file.";
        Player.Clear("Select a video to preview it.");
    }

    private async Task ShowSelection()
    {
        var generation = ++_generation;
        var row = Browser.Selected;
        if (row == null)
        {
            Clear();
            return;
        }

        IsLoading = true;
        _content = null;
        _info = null;
        _shownHash = Hash(row.Path);
        MatteChip.IsChecked = false;
        PictureChip.IsChecked = true;
        TrackChoice.Visibility = Visibility.Collapsed;
        NameText.Text = row.Name;
        FactsText.Text = "Reading";
        var edited = _services.Workspace.IsEdited(row.Path);
        StateText.Text = edited ? "Edited in this project" : "";
        RevertButton.IsEnabled = edited;
        ExportOriginal.IsEnabled = ExportMp4.IsEnabled = ReplaceButton.IsEnabled = false;
        Player.Clear("Reading");

        var path = row.Path;
        var extension = Path.GetExtension(path);
        var workspace = _services.Workspace;
        try
        {
            var content = await Task.Run(() => workspace.ReadCurrent(path) ?? []);
            if (generation != _generation)
                return;
            _content = content;
            ExportOriginal.IsEnabled = true;

            if (_services.VideoPreviews is not { } previews)
            {
                FactsText.Text = "FFmpeg is needed to read this video.";
                Player.Clear(VideoPlayer.NoToolMessage);
                return;
            }

            var info = await previews.ProbeAsync(content, extension);
            if (generation != _generation)
                return;
            _info = info;
            FactsText.Text = info.Summary;
            TrackChoice.Visibility = info.HasMatte ? Visibility.Visible : Visibility.Collapsed;
            ExportMp4.IsEnabled = true;
            ShowReplaceOptions(row, info);
            await Player.ShowAsync(_services, content, extension, VideoTrack.Picture);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            if (generation == _generation)
            {
                FactsText.Text = "";
                Player.Clear("This video could not be read: " + e.Message);
            }
        }
        finally
        {
            if (generation == _generation)
                IsLoading = false;
        }
    }

    private void ShowReplaceOptions(AssetRow row, VideoInfo info)
    {
        var loose = row.File?.IsLoose == true;
        ReplaceButton.IsEnabled = !loose && (info.IsMatroska || info.IsVp6);
        ReplaceButton.Content = info.IsVp6 ? "Replace from AVI" : "Replace video";
        NewMatte.IsChecked = false;
        NewMatte.Visibility = info.IsVp6 && info.HasVp6Matte ? Visibility.Visible : Visibility.Collapsed;
        ReplaceNote.Text = loose ? "Loose files cannot be replaced yet: their priority against archives has not been verified."
            : info.IsVp6 ? "FFmpeg cannot write VP6. Encode an AVI with the VP6 VFW codec in VirtualDub, as VP60 Simple Profile, at this exact size and frame count. Its frames go into the game's file, and the sound stays."
            : info.HasMatte ? "Choose a video, or drop one on the player. It is encoded to match the game's file. The matte comes from the video's transparency, or is solid white without any."
            : "Choose a video, or drop one on the player. It is encoded to match the game's file.";
    }

    private void OnTrack(object sender, RoutedEventArgs e) => _ = ShowTrack(sender == MatteChip ? VideoTrack.Matte : VideoTrack.Picture);

    public async Task ShowTrack(VideoTrack track)
    {
        PictureChip.IsChecked = track == VideoTrack.Picture;
        MatteChip.IsChecked = track == VideoTrack.Matte;
        if (_content != null && Browser.Selected is { } row)
            await Player.ShowAsync(_services, _content, Path.GetExtension(row.Path), track);
    }

    private void OnExportOriginal(object sender, RoutedEventArgs e)
    {
        if (Browser.Selected is not { } row || _content is not { } content)
            return;
        var dialog = new SaveFileDialog { Title = "Export video", FileName = row.Name };
        if (dialog.ShowDialog() == true)
        {
            File.WriteAllBytes(dialog.FileName, content);
            _services.Notify($"Saved {dialog.FileName}");
        }
    }

    private void OnExportMp4(object sender, RoutedEventArgs e)
    {
        if (Browser.Selected is not { } row)
            return;
        var dialog = new SaveFileDialog { Title = "Export MP4", FileName = Path.ChangeExtension(row.Name, ".mp4"), Filter = "MP4 video|*.mp4" };
        if (dialog.ShowDialog() == true)
            _ = ExportMp4To(row, dialog.FileName);
    }

    public Task<bool> ExportMp4To(AssetRow row, string destination)
    {
        var (content, info, tool) = (_content, _info, _services.Ffmpeg);
        if (content == null || info == null || tool == null)
            return Task.FromResult(false);
        var folder = Path.GetDirectoryName(destination)!;
        var source = Path.Combine(folder, $"{Guid.NewGuid():N}{Path.GetExtension(row.Path)}");
        return _services.Jobs.RunAsync<string>($"Exporting {row.Name}", async context =>
        {
            await File.WriteAllBytesAsync(source, content, context.Cancel);
            try
            {
                await tool.MakeMp4Async(source, destination, 0, info.AudioCodec != null, null, info.Seconds, Reporter(context, "Converting"), context.Cancel);
            }
            finally
            {
                File.Delete(source);
            }
            return destination;
        }, path => _services.Notify($"Saved {path}"));
    }

    private static Action<double> Reporter(JobContext context, string text) => done => context.Progress(text, (int)(done * 100), 100);

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        if (_info is not { } info)
            return;
        var dialog = new OpenFileDialog
        {
            Title = info.IsVp6 ? "Choose the AVI encoded as VP60" : "Choose the new video",
            Filter = info.IsVp6 ? AviFilter : VideoFilter,
        };
        if (dialog.ShowDialog() != true)
            return;

        string? matte = null;
        if (info.IsVp6 && NewMatte.IsChecked == true)
        {
            var second = new OpenFileDialog { Title = "Choose the matte AVI encoded as VP60", Filter = AviFilter };
            if (second.ShowDialog() != true)
                return;
            matte = second.FileName;
        }
        _ = ReplaceFrom(dialog.FileName, matte);
    }

    /// <summary>Replaces the selected video, converting for .mkv and swapping in frames for .vp6.</summary>
    public async Task ReplaceFrom(string source, string? matte = null)
    {
        if (Browser.Selected is not { } row || _info is not { } info || _content is not { } content)
        {
            _services.ReportError("Nothing to replace", "Select a video first.");
            return;
        }
        if (row.File?.IsLoose == true)
        {
            _services.ReportError("Loose files cannot be replaced yet", "Their priority against archives has not been verified, so a replacement might silently do nothing.");
            return;
        }
        if (_services.RequireProject() is not { } project)
            return;

        var name = Path.GetFileName(source);
        if (info.IsVp6)
        {
            await _services.Jobs.RunAsync($"Replacing {row.Name}", _ =>
            {
                var picture = AviVideo.Read(File.ReadAllBytes(source));
                var alpha = matte != null ? AviVideo.Read(File.ReadAllBytes(matte)) : null;
                var original = _services.Workspace.Vfs?.Find(row.Path) is { } file ? _services.Workspace.Vfs.ReadContent(file) : content;
                return Vp6File.ReplaceFrames(original, picture, alpha);
            }, bytes =>
            {
                project.Set(row.Path, bytes, $"Video from {name}");
                _services.Notify($"{row.Name} now uses {name} in this project.");
            });
            return;
        }

        if (_services.Ffmpeg is not { } tool)
        {
            _services.ReportError("FFmpeg was not found", VideoPlayer.NoToolMessage);
            return;
        }
        await _services.Jobs.RunAsync($"Encoding {name}", async context =>
            await tool.ConvertForGameAsync(source, info, Reporter(context, $"Encoding for {row.Name}"), context.Cancel), result =>
        {
            project.Set(row.Path, result.Content, $"Video from {name}");
            var notes = new List<string>();
            if (Math.Abs(result.SourceSeconds - result.TargetSeconds) > LengthWarningSeconds)
                notes.Add($"it runs {result.SourceSeconds:F1} s and the game's runs {result.TargetSeconds:F1} s");
            if (result.AddedSilence)
                notes.Add("it had no sound, so silence was added");
            _services.Notify($"{row.Name} now uses {name} in this project" + (notes.Count > 0 ? $". Note: {string.Join(", and ", notes)}." : "."));
        });
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        if (Browser.Selected is { } row && _services.Workspace.Project is { } project)
            project.Revert([row.Path], $"Reverted {row.Name}");
    }
}
