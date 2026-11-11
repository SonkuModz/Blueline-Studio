using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core;
using Blueline.Formats.Audio;
using Microsoft.Win32;

namespace Blueline.App.Modules;

/// <summary>Sound banks: list samples by walking the block chain, then decode and replace them through sx.exe.</summary>
public partial class AudioModule : UserControl, IEditorModule
{
    private readonly AppServices _services;
    private readonly MediaPlayer _player = new();
    private string? _bankPath;
    private string? _previewFile;
    private int _generation;

    public AudioModule(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Browser.SetScope(r => r.Category == AssetCategory.Audio);
        Browser.SelectionChanged += LoadBank;
        DropTarget.Attach(SamplesCard, f => File.Exists(f) && Path.GetExtension(f).Equals(".wav", StringComparison.OrdinalIgnoreCase),
            files => _ = ReplaceFrom(files[0]), SamplesCard);
        _player.MediaEnded += (_, _) => Stop.IsEnabled = false;
        services.Workspace.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible)
                UpdateButtons();
        });
        UpdateButtons();
    }

    public string Title => "Audio";
    public FrameworkElement View => this;
    public AssetBrowser BrowserControl => Browser;
    public ListView SampleList => Samples;
    public bool IsLoading { get; private set; }

    public void Activated()
    {
        Browser.Load(_services.Index);
        UpdateButtons();
    }

    public void OpenPath(string path)
    {
        Browser.Load(_services.Index);
        Browser.Reveal(path);
    }

    private SxTool? Tool => _services.Settings.SxPath is { Length: > 0 } path && File.Exists(path) ? new SxTool(path) : null;

    private void UpdateButtons()
    {
        var has = Tool != null;
        var sample = Samples.SelectedItem as SoundSample;
        Play.IsEnabled = has && sample != null;
        Export.IsEnabled = has && sample != null;
        Replace.IsEnabled = has && sample != null && SxTool.FlagFor(sample) != null;
        RevertBank.IsEnabled = _bankPath != null && _services.Workspace.IsEdited(_bankPath);
        ToolNote.Text = has ? "" : "Listening, export and replace need EA's sx.exe. Choose it in Settings.";
    }

    private void OnSampleSelected(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void LoadBank()
    {
        if (Browser.Selected is not { } row)
            return;
        var generation = ++_generation;
        _bankPath = row.Path;
        BankText.Text = row.Name;
        Samples.ItemsSource = null;
        Count.Text = "Reading";
        IsLoading = true;
        var workspace = _services.Workspace;
        var file = row.File;
        var path = row.Path;

        Task.Run(() =>
        {
            if (workspace.Project?.TryGet(path, out var change) == true || file == null)
            {
                var data = workspace.ReadCurrent(path) ?? [];
                return SoundBank.Scan(data.Length, (offset, buffer) =>
                {
                    var length = (int)Math.Min(buffer.Length, data.Length - offset);
                    data.AsSpan((int)offset, length).CopyTo(buffer);
                    return length;
                });
            }
            if (file.Entry == null)
                throw new InvalidOperationException("Sound banks on disk outside an archive are not listed.");
            return SoundBank.Scan(file.StoredSize, (offset, buffer) => file.Archive!.ReadRaw(file.Entry, offset, buffer));
        }).ContinueWith(task =>
        {
            if (generation != _generation)
                return;
            IsLoading = false;
            if (task.IsFaulted)
            {
                Count.Text = "This bank could not be read: " + task.Exception!.GetBaseException().Message;
                return;
            }
            var samples = task.Result;
            Samples.ItemsSource = samples;
            Count.Text = samples.Count == 0
                ? "No samples here. Streamed banks keep their audio in the .sbs file beside this index."
                : $"{samples.Count:N0} samples, {samples.Sum(s => s.Seconds) / 60:F1} minutes";
            UpdateButtons();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private byte[] ReadSample(SoundSample sample)
    {
        var path = _bankPath!;
        var workspace = _services.Workspace;
        if (workspace.Project?.TryGet(path, out _) == true)
            return workspace.ReadCurrent(path)!.AsSpan((int)sample.Offset, sample.Length).ToArray();
        var file = workspace.Vfs!.Find(path)!;
        var buffer = new byte[sample.Length];
        file.Archive!.ReadRaw(file.Entry!, sample.Offset, buffer);
        return buffer;
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (Samples.SelectedItem is not SoundSample sample || Tool is not { } tool)
            return;
        _ = _services.Jobs.RunAsync($"Decoding sample {sample.Index}", async context =>
        {
            var target = Path.Combine(Path.GetTempPath(), "Blueline", $"preview-{Guid.NewGuid():N}.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await tool.DecodeAsync(ReadSample(sample), target, context.Cancel);
            return target;
        }, wav =>
        {
            _player.Close();
            if (_previewFile != null)
                TryDelete(_previewFile);
            _previewFile = wav;
            _player.Open(new Uri(wav));
            _player.Play();
            Stop.IsEnabled = true;
        });
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        Stop.IsEnabled = false;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var selected = Samples.SelectedItems.Cast<SoundSample>().OrderBy(s => s.Index).ToList();
        if (selected.Count == 0 || Tool == null)
            return;
        var stem = Path.GetFileNameWithoutExtension(_bankPath);

        if (selected.Count == 1)
        {
            var dialog = new SaveFileDialog { Title = "Export WAV", FileName = $"{stem}_{selected[0].Index:D4}.wav", Filter = "WAV|*.wav" };
            if (dialog.ShowDialog() == true)
                _ = ExportSamples(selected, Path.GetDirectoryName(dialog.FileName)!, dialog.FileName);
        }
        else if (AppServices.PickFolder($"Export {selected.Count} samples as WAV") is { } folder)
        {
            _ = ExportSamples(selected, folder, null);
        }
    }

    public Task<bool> ExportSamples(IReadOnlyList<SoundSample> samples, string folder, string? exactName)
    {
        var tool = Tool!;
        var stem = Path.GetFileNameWithoutExtension(_bankPath);
        return _services.Jobs.RunAsync($"Exporting {samples.Count} sample{(samples.Count == 1 ? "" : "s")}", async context =>
        {
            for (var i = 0; i < samples.Count; i++)
            {
                context.Cancel.ThrowIfCancellationRequested();
                context.Progress($"Decoding sample {samples[i].Index}", i, samples.Count);
                var target = exactName ?? Path.Combine(folder, $"{stem}_{samples[i].Index:D4}.wav");
                await tool.DecodeAsync(ReadSample(samples[i]), target, context.Cancel);
            }
            return samples.Count;
        }, count => _services.Notify($"Exported {count} sample{(count == 1 ? "" : "s")} to {folder}."));
    }

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose a WAV", Filter = "WAV|*.wav" };
        if (dialog.ShowDialog() == true)
            _ = ReplaceFrom(dialog.FileName);
    }

    public async Task ReplaceFrom(string wav)
    {
        if (Samples.SelectedItem is not SoundSample sample || Tool is not { } tool || _bankPath == null)
        {
            _services.ReportError("Nothing to replace", "Select a sample first. Audio replacement also needs sx.exe, set in Settings.");
            return;
        }
        if (SxTool.FlagFor(sample) is not { } flag)
        {
            _services.ReportError("This codec cannot be encoded", $"{sample.CodecName} has no encoder through sx.exe here.");
            return;
        }
        if (_services.RequireProject() is not { } project)
            return;

        var workspace = _services.Workspace;
        var path = _bankPath;
        await _services.Jobs.RunAsync($"Replacing sample {sample.Index}", async context =>
        {
            var fitted = await tool.EncodeToFitAsync(wav, flag, sample.Length, new Status(context), context.Cancel)
                ?? throw new InvalidOperationException($"Even at the lowest quality this audio is larger than the slot of {sample.Length:N0} bytes. Use a shorter clip.");
            context.Status("Updating the bank");
            var bank = workspace.ReadCurrent(path)!;
            return (SoundBank.ReplaceSample(bank, sample, fitted.Sample), fitted);
        }, result =>
        {
            project.Set(path, result.Item1, $"Sample {sample.Index} from {Path.GetFileName(wav)}");
            _services.Notify($"Replaced sample {sample.Index} at quality {result.fitted.Quality}, {result.fitted.Sample.Length:N0} of {sample.Length:N0} bytes.");
            LoadBank();
        });
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        if (_bankPath != null && _services.Workspace.Project is { } project)
        {
            project.Revert([_bankPath], $"Reverted {Path.GetFileName(_bankPath)}");
            LoadBank();
        }
    }

    private sealed class Status(JobContext context) : IProgress<string>
    {
        public void Report(string value) => context.Status(value);
    }
}
