using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Blueline.Core;
using Shape = System.Windows.Shapes.Shape;
using Microsoft.Win32;

namespace Blueline.App.Shell;

/// <summary>
/// What you see when the app starts: the crest arrives over rink artwork (or a picture you chose), the game loads with real
/// progress, and you pick up your last project or start another. It sits over the workspace and fades away when you go in.
/// </summary>
public partial class StartScreen : UserControl
{
    private const int MaxRecent = 6;

    private AppServices? _services;
    private string? _continueFolder;
    private bool _loading;
    private readonly List<AnimationClock> _clocks = [];

    public StartScreen()
    {
        InitializeComponent();
    }

    public event Action<string>? ProjectChosen;
    public event Action? OpenProjectRequested;
    public event Action? NewProjectRequested;
    public event Action? BrowseRequested;
    public event Action? ChooseGameRequested;

    /// <summary>True while the screen is up.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    public void Prepare(AppServices services)
    {
        _services = services;
        ApplyBackground();
    }

    /// <summary>Shows the screen and plays its entrance.</summary>
    public void Open()
    {
        Visibility = Visibility.Visible;
        Layer.Opacity = 1;
        IsHitTestVisible = true;
        RefreshRecent();
        RefreshGame();
        Play();
    }

    /// <summary>Fades the screen away. Finishes straight away when movement is off.</summary>
    public Task CloseAsync()
    {
        var done = new TaskCompletionSource();
        IsHitTestVisible = false;
        if (!Motion.Enabled)
        {
            Visibility = Visibility.Collapsed;
            done.SetResult();
            return done.Task;
        }

        var fade = new DoubleAnimation(1, 0, Motion.Of(Motion.Slow)) { EasingFunction = Motion.InOut, FillBehavior = FillBehavior.HoldEnd };
        fade.Completed += (_, _) =>
        {
            Layer.BeginAnimation(OpacityProperty, null);
            Visibility = Visibility.Collapsed;
            Layer.Opacity = 1;
            done.SetResult();
        };
        Layer.BeginAnimation(OpacityProperty, fade);
        var grow = new DoubleAnimation(1, 1.05, Motion.Of(Motion.Slow)) { EasingFunction = Motion.Out };
        var scale = (ScaleTransform)Crest.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        return done.Task;
    }

    // Game status

    /// <summary>A job such as mounting the game is running (text), or has finished (null).</summary>
    public void ShowProgress(string? text, double? fraction)
    {
        _loading = text != null;
        if (!_loading)
        {
            RefreshGame();
            return;
        }
        GameDot.SetResourceReference(Shape.FillProperty, "AccentTextBrush");
        GameLine.Text = text;
        ChooseGame.Visibility = Visibility.Collapsed;
        GameBar.Visibility = Visibility.Visible;
        GameBar.IsIndeterminate = fraction == null;
        GameBar.Value = (fraction ?? 0) * 100;
    }

    public void RefreshGame()
    {
        if (_services == null || _loading)
            return;
        GameBar.Visibility = Visibility.Collapsed;
        if (_services.Workspace.Vfs is { } vfs)
        {
            GameDot.SetResourceReference(Shape.FillProperty, "SuccessBrush");
            GameLine.Text = $"NHL 19 ready  ·  {vfs.Files.Count:N0} files" + (vfs.Install.ModsFolder is { } mods
                ? $"  ·  mods folder: {vfs.Install.ArchivesAddedByMods.Count} added, {vfs.Install.ArchivesReplacedByMods.Count} replaced"
                : "");
            ChooseGame.Visibility = Visibility.Collapsed;
        }
        else
        {
            GameDot.SetResourceReference(Shape.FillProperty, "WarningBrush");
            GameLine.Text = "No game loaded. Choose the folder that holds eboot.bin.";
            ChooseGame.Visibility = Visibility.Visible;
        }
    }

    // Recent projects

    public void RefreshRecent()
    {
        RecentList.Children.Clear();
        _continueFolder = null;
        if (_services == null)
            return;

        var shown = 0;
        foreach (var folder in _services.Settings.RecentProjects.ToList())
        {
            if (shown >= MaxRecent || !Directory.Exists(folder))
                continue;
            ModProject project;
            try
            {
                project = ModProject.Open(folder);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                continue;
            }

            var installed = IsInstalled(project);
            if (_continueFolder == null)
            {
                _continueFolder = folder;
                ContinueName.Text = project.Name;
                ContinueDetail.Text = Describe(project, installed);
            }
            RecentList.Children.Add(Row(folder, project, installed));
            shown++;
        }

        ContinueHost.Visibility = _continueFolder != null ? Visibility.Visible : Visibility.Collapsed;
        NoRecent.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;

        // With nothing to continue, opening a project is the main action.
        if (_continueFolder != null)
            OpenButton.ClearValue(StyleProperty);
        else
            OpenButton.SetResourceReference(StyleProperty, "Primary");
    }

    private bool IsInstalled(ModProject project)
    {
        var folder = _services?.Settings.GameFolder;
        return folder != null && project.Installed.Any(i => File.Exists(Path.Combine(folder, "databigs", i.FileName)));
    }

    private static string Describe(ModProject project, bool installed) =>
        $"{project.ChangeCount} changed file{(project.ChangeCount == 1 ? "" : "s")}" + (installed ? "  ·  installed in the game" : "");

    private FrameworkElement Row(string folder, ModProject project, bool installed)
    {
        var name = new TextBlock { Text = project.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var detail = new TextBlock { Text = Describe(project, false), TextTrimming = TextTrimming.CharacterEllipsis };
        detail.SetResourceReference(StyleProperty, "Dim");
        var text = new StackPanel { Children = { name, detail } };
        var dock = new DockPanel();
        if (installed)
        {
            var badge = new Border { CornerRadius = new CornerRadius(99), Padding = new Thickness(9, 2, 9, 3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Child = new TextBlock { Text = "installed", FontSize = 11 } };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            ((TextBlock)badge.Child).SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            DockPanel.SetDock(badge, Dock.Right);
            dock.Children.Add(badge);
        }
        dock.Children.Add(text);

        var card = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 9, 12, 9), Child = dock, BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        var button = new Button { Content = card, Tag = folder, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 6), ToolTip = folder };
        button.SetResourceReference(StyleProperty, "CardButton");
        button.Click += (_, _) => ProjectChosen?.Invoke(folder);
        return button;
    }

    // Background

    public void ApplyBackground()
    {
        var path = _services?.Settings.StartBackground;
        if (path != null && File.Exists(path))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = 1920;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                Wallpaper.Source = image;
                Wallpaper.Visibility = WallpaperDim.Visibility = Visibility.Visible;
                Rink.Opacity = 0.35;
                ResetBackground.Visibility = Visibility.Visible;
                return;
            }
            catch (Exception e) when (e is IOException or NotSupportedException or InvalidOperationException or FileFormatException)
            {
                _services!.Log.Warning($"The start screen background could not be read: {e.Message}");
            }
        }

        Wallpaper.Source = null;
        Wallpaper.Visibility = WallpaperDim.Visibility = Visibility.Collapsed;
        Rink.Opacity = 1;
        ResetBackground.Visibility = Visibility.Collapsed;
    }

    private void OnChangeBackground(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose a background picture", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
        if (dialog.ShowDialog() != true || _services == null)
            return;
        _services.Settings.StartBackground = dialog.FileName;
        _services.Settings.Save();
        ApplyBackground();
    }

    private void OnResetBackground(object sender, RoutedEventArgs e)
    {
        if (_services == null)
            return;
        _services.Settings.StartBackground = null;
        _services.Settings.Save();
        ApplyBackground();
    }

    // Buttons

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (_continueFolder != null)
            ProjectChosen?.Invoke(_continueFolder);
    }

    private void OnOpenProject(object sender, RoutedEventArgs e) => OpenProjectRequested?.Invoke();

    private void OnNewProject(object sender, RoutedEventArgs e) => NewProjectRequested?.Invoke();

    private void OnBrowse(object sender, RoutedEventArgs e) => BrowseRequested?.Invoke();

    private void OnChooseGame(object sender, RoutedEventArgs e) => ChooseGameRequested?.Invoke();

    // Entrance

    private void Play()
    {
        foreach (var clock in _clocks)
            clock.Controller?.Stop();
        _clocks.Clear();

        var recentRows = RecentList.Children.OfType<FrameworkElement>().ToList();
        var parts = new List<(UIElement Element, int Delay)>
        {
            (TitleText, 280), (TaglineText, 340), (ContinueHost, 420), (ButtonRow, 480),
        };
        foreach (var (element, delay) in parts)
            Reveal(element, delay);
        Reveal(RecentHost.Children[0], 400);
        for (var i = 0; i < recentRows.Count; i++)
            Reveal(recentRows[i], 460 + 60 * i);
        Reveal(NoRecent, 460);

        Fade(Rink, 0, 700);
        PlayCrest();
        PlaySweep();
    }

    /// <summary>The element fades in while drifting up a little, after a delay.</summary>
    private void Reveal(UIElement element, int delay, double fromY = 14)
    {
        if (!Motion.Enabled)
        {
            element.ClearValue(OpacityProperty);
            element.ClearValue(RenderTransformProperty);
            return;
        }

        var slide = new TranslateTransform(0, fromY);
        element.RenderTransform = slide;
        element.Opacity = 0;
        var fade = Clock(new DoubleAnimation(0, 1, Motion.Of(380)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = Motion.Out, FillBehavior = FillBehavior.HoldEnd });
        fade.Completed += (_, _) =>
        {
            element.ApplyAnimationClock(OpacityProperty, null);
            element.Opacity = 1;
        };
        element.ApplyAnimationClock(OpacityProperty, fade);
        var rise = Clock(new DoubleAnimation(fromY, 0, Motion.Of(380)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = Motion.Out, FillBehavior = FillBehavior.HoldEnd });
        rise.Completed += (_, _) =>
        {
            slide.ApplyAnimationClock(TranslateTransform.YProperty, null);
            slide.Y = 0;
        };
        slide.ApplyAnimationClock(TranslateTransform.YProperty, rise);
    }

    private void Fade(UIElement element, int delay, int milliseconds)
    {
        if (!Motion.Enabled)
        {
            element.ClearValue(OpacityProperty);
            return;
        }
        var baseOpacity = element.Opacity;
        element.Opacity = 0;
        var fade = Clock(new DoubleAnimation(0, baseOpacity, Motion.Of(milliseconds)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = Motion.Out, FillBehavior = FillBehavior.HoldEnd });
        fade.Completed += (_, _) =>
        {
            element.ApplyAnimationClock(OpacityProperty, null);
            element.Opacity = baseOpacity;
        };
        element.ApplyAnimationClock(OpacityProperty, fade);
    }

    private void PlayCrest()
    {
        var scale = (ScaleTransform)Crest.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = scale.ScaleY = 1;
        if (!Motion.Enabled)
        {
            Crest.ClearValue(OpacityProperty);
            return;
        }

        scale.ScaleX = scale.ScaleY = 0.9;
        var grow = new DoubleAnimation(0.9, 1, Motion.Of(520)) { BeginTime = TimeSpan.FromMilliseconds(80), EasingFunction = Motion.Out };
        scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, Clock(grow));
        scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, Clock(grow.Clone()));
        Fade(Crest, 80, 520);
    }

    private void PlaySweep()
    {
        // The line runs through the blue line of the crest, wherever the layout puts it.
        UpdateLayout();
        if (Crest.IsVisible && Crest.ActualHeight > 0)
        {
            var centre = Crest.TranslatePoint(new Point(0, Crest.ActualHeight / 2), Layer);
            Sweep.Margin = new Thickness(0, centre.Y - Sweep.Height / 2, 0, 0);
        }
        var scale = (ScaleTransform)Sweep.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        if (!Motion.Enabled)
        {
            Sweep.Opacity = 0;
            scale.ScaleX = 0;
            return;
        }

        scale.ScaleX = 0;
        Sweep.Opacity = 0;
        scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, Clock(new DoubleAnimation(0, 1, Motion.Of(Motion.Slow + 240)) { EasingFunction = Motion.InOut }));
        var glow = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        glow.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.Zero));
        glow.KeyFrames.Add(new LinearDoubleKeyFrame(0.85, TimeSpan.FromMilliseconds(220)));
        glow.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(Motion.Slow + 520)));
        Sweep.ApplyAnimationClock(OpacityProperty, Clock(glow));
    }

    private AnimationClock Clock(AnimationTimeline animation)
    {
        var clock = animation.CreateClock();
        _clocks.Add(clock);
        return clock;
    }

    /// <summary>Stops the entrance where it is, so a screenshot can be taken at an exact moment.</summary>
    public void PauseMovement()
    {
        foreach (var clock in _clocks)
            clock.Controller?.Pause();
    }

    public void SeekMovement(double milliseconds)
    {
        // A seek counts from when each animation begins, so the delay before it starts is taken off.
        foreach (var clock in _clocks)
        {
            var begin = clock.Timeline.BeginTime?.TotalMilliseconds ?? 0;
            clock.Controller?.Seek(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds - begin)), TimeSeekOrigin.BeginTime);
        }
    }

    public void ResumeMovement()
    {
        foreach (var clock in _clocks)
            clock.Controller?.Resume();
    }

    public bool IsMoving => _clocks.Any(c => c.CurrentState == ClockState.Active);
}
