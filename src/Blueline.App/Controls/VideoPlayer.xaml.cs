using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Blueline.App.Shell;
using Blueline.Core;

namespace Blueline.App.Controls;

/// <summary>Plays a game video by way of an MP4 preview, because Windows cannot be relied on for VP8 or VP6.</summary>
public partial class VideoPlayer : UserControl
{
    public const string NoToolMessage = "Playing video needs FFmpeg, which Blueline does not include. Install it, or choose ffmpeg.exe in Settings.";

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? _cancel;
    private int _generation;
    private bool _playing;
    private bool _dragging;
    private bool _moving;

    public VideoPlayer()
    {
        InitializeComponent();
        Media.Volume = Volume.Value;
        _timer.Tick += (_, _) => ShowPosition();
        Seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true));
        Seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _dragging = false;
            MoveTo(Seek.Value);
        }));
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
                Pause();
        };
        Clear("Select a video to preview it.");
    }

    public bool IsPreparing { get; private set; }
    public bool IsReady { get; private set; }
    public bool IsPlaying => _playing;
    public int VideoWidth => Media.NaturalVideoWidth;
    public TimeSpan Position => Media.Position;

    /// <summary>Stops, cancels any conversion and shows a message instead of a video.</summary>
    public void Clear(string message = "")
    {
        _generation++;
        _cancel?.Cancel();
        _cancel = null;
        CloseMedia();
        IsPreparing = false;
        Say(message, null);
    }

    public async Task ShowAsync(AppServices services, byte[] content, string extension, VideoTrack track)
    {
        Clear("Preparing the preview");
        var generation = _generation;
        if (services.VideoPreviews is not { } previews)
        {
            Say(NoToolMessage, null);
            return;
        }

        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        IsPreparing = true;
        Working.Value = 0;
        Say("Preparing the preview", 0);
        void Report(double done) => Dispatcher.BeginInvoke(() =>
        {
            if (generation == _generation)
                Say("Preparing the preview", done);
        });

        try
        {
            var file = await Task.Run(() => previews.GetAsync(content, extension, track, Report, cancel.Token), cancel.Token);
            if (generation != _generation)
                return;
            Media.Source = new Uri(file);
            // Playing then pausing is what makes a MediaElement open the file and show its first frame.
            Media.Play();
            Media.Pause();
        }
        catch (OperationCanceledException)
        {
            // A newer selection took over.
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            services.Log.Warning($"Video preview failed: {e.Message}");
            if (generation == _generation)
            {
                IsPreparing = false;
                Say(e.Message, null);
            }
        }
    }

    public void Play()
    {
        if (!IsReady)
            return;
        Media.Play();
        _playing = true;
        PlayButton.Content = "Pause";
        _timer.Start();
    }

    public void Pause()
    {
        if (!IsReady)
            return;
        Media.Pause();
        _playing = false;
        PlayButton.Content = "Play";
        _timer.Stop();
        ShowPosition();
    }

    private void CloseMedia()
    {
        _timer.Stop();
        Media.Stop();
        Media.Close();
        Media.Source = null;
        IsReady = _playing = false;
        PlayButton.Content = "Play";
        PlayButton.IsEnabled = Seek.IsEnabled = false;
        _moving = true;
        Seek.Value = 0;
        _moving = false;
        TimeText.Text = "0:00 / 0:00";
    }

    private void Say(string text, double? progress)
    {
        Message.Text = text;
        MessagePanel.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Working.Visibility = progress == null ? Visibility.Collapsed : Visibility.Visible;
        Working.Value = progress ?? 0;
    }

    private void OnOpened(object sender, RoutedEventArgs e)
    {
        IsPreparing = false;
        IsReady = true;
        Say("", null);
        PlayButton.IsEnabled = true;
        Seek.IsEnabled = Media.NaturalDuration.HasTimeSpan;
        Seek.Maximum = Media.NaturalDuration.HasTimeSpan ? Math.Max(0.001, Media.NaturalDuration.TimeSpan.TotalSeconds) : 1;
        ShowPosition();
    }

    private void OnFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        CloseMedia();
        IsPreparing = false;
        Say("Windows could not play the preview: " + e.ErrorException.Message, null);
    }

    private void OnEnded(object sender, RoutedEventArgs e)
    {
        Media.Position = TimeSpan.Zero;
        Pause();
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_playing)
            Pause();
        else
            Play();
    }

    private void OnVolume(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Media != null)
            Media.Volume = e.NewValue;
    }

    private void OnSeek(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_moving && !_dragging && IsReady)
            MoveTo(e.NewValue);
        else if (_dragging)
            TimeText.Text = TimeLabel(TimeSpan.FromSeconds(e.NewValue));
    }

    private void MoveTo(double seconds)
    {
        if (IsReady)
            Media.Position = TimeSpan.FromSeconds(seconds);
        ShowPosition();
    }

    private void ShowPosition()
    {
        if (!IsReady || _dragging)
            return;
        _moving = true;
        Seek.Value = Math.Min(Media.Position.TotalSeconds, Seek.Maximum);
        _moving = false;
        TimeText.Text = TimeLabel(Media.Position);
    }

    private string TimeLabel(TimeSpan at)
    {
        var total = Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan : TimeSpan.Zero;
        return $"{(int)at.TotalMinutes}:{at.Seconds:00} / {(int)total.TotalMinutes}:{total.Seconds:00}";
    }
}
