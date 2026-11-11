using System.Windows;
using System.Windows.Controls;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core;
using Microsoft.Win32;

namespace Blueline.App.Modules;

public sealed class HomeModule : IEditorModule
{
    private readonly AppServices _services;
    private readonly HomeView _view;

    public HomeModule(AppServices services)
    {
        _services = services;
        _view = new HomeView(this, services);
    }

    public string Title => "Settings";
    public FrameworkElement View => _view;
    public void Activated() => _view.Refresh();
    public void OpenPath(string path) { }

    /// <summary>Mounts the game in the background; <paramref name="after"/> runs once it is ready, even if it failed.</summary>
    public async Task MountAsync(string folder, Func<Task>? after = null)
    {
        await _services.Jobs.RunAsync("Mounting the game", context =>
        {
            var install = GameInstall.Open(folder);
            var vfs = VirtualFileSystem.Mount(install, new Reporter(context));
            return (vfs, new AssetIndex(vfs));
        }, result =>
        {
            _services.SetGame(result.vfs, result.Item2);
            _services.Settings.GameFolder = result.vfs.Install.Root;
            _services.Settings.Save();
            _view.Refresh();
        });
        if (after != null)
            await after();
    }

    private sealed class Reporter(JobContext context) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => context.Progress($"Reading archive {value.Done} of {value.Total}", value.Done, value.Total);
    }
}

public partial class HomeView : UserControl
{
    private readonly HomeModule _module;
    private readonly AppServices _services;

    public HomeView(HomeModule module, AppServices services)
    {
        _module = module;
        _services = services;
        InitializeComponent();
        services.Workspace.Changed += () => Dispatcher.BeginInvoke(Refresh);
        ThemeManager.Changed += SyncAppearance;
        DropTarget.Attach(this, Directory.Exists, folders => _ = _module.MountAsync(folders[0]), GameCard);
    }

    public void Refresh()
    {
        SyncAppearance();
        var vfs = _services.Workspace.Vfs;
        if (vfs == null)
        {
            GameState.Text = "No game loaded";
            GamePath.Text = "Choose the folder that holds eboot.bin and databigs (CUSA11126), or drop it here.";
            BrowseGame.Content = "Choose game folder";
            Stats.ItemsSource = null;
        }
        else
        {
            GameState.Text = "NHL 19 (PS4) ready";
            GamePath.Text = vfs.Install.Root + (vfs.Install.ModsFolder is { } mods
                ? $"\nWith mods from {Path.GetFileName(mods)}: {vfs.Install.ArchivesAddedByMods.Count} archives added, {vfs.Install.ArchivesReplacedByMods.Count} replaced"
                : "");
            BrowseGame.Content = "Change folder";
            var groups = vfs.Files.GroupBy(f => AssetCategories.Of(f.Path)).ToDictionary(g => g.Key, g => g.Count());
            int Count(AssetCategory c) => groups.GetValueOrDefault(c);
            Stats.ItemsSource = new[]
            {
                new { Value = vfs.Archives.Count.ToString(), Label = "archives" },
                new { Value = vfs.Files.Count.ToString("N0"), Label = "files" },
                new { Value = FormatSize.Of(vfs.Archives.Sum(a => a.Length)), Label = "on disk" },
                new { Value = Count(AssetCategory.Texture).ToString("N0"), Label = "textures" },
                new { Value = Count(AssetCategory.Model).ToString("N0"), Label = "models" },
                new { Value = Count(AssetCategory.Audio).ToString("N0"), Label = "sound banks" },
            };
        }

        var project = _services.Workspace.Project;
        ProjectState.Text = project == null ? "No project open" : project.Name;
        ProjectPath.Text = project == null ? "A project starts automatically the first time you make an edit, or create one now." : project.Folder;
        CloseProject.Visibility = project == null ? Visibility.Collapsed : Visibility.Visible;

        var recent = _services.Settings.RecentProjects.Where(p => p != project?.Folder && Directory.Exists(p)).ToList();
        Recent.ItemsSource = recent;
        RecentLabel.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var sx = _services.Settings.SxPath;
        SxPath.Text = sx is { Length: > 0 } && File.Exists(sx) ? sx : "Not set. Sound banks can be browsed; export and replace need sx.exe.";
        FfmpegPath.Text = _services.Ffmpeg?.Executable ?? "Not found. Videos can be exported as they are; playing and replacing them need FFmpeg.";
    }

    private void OnBrowseFfmpeg(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Locate ffmpeg.exe (ffprobe.exe must be beside it)", Filter = "ffmpeg.exe|ffmpeg.exe|Programs (*.exe)|*.exe" };
        if (dialog.ShowDialog() == true)
        {
            _services.Settings.FfmpegPath = dialog.FileName;
            _services.Settings.Save();
            _services.ResetVideoTool();
            _services.Log.Info($"Video tool set to {dialog.FileName}");
            Refresh();
        }
    }

    // Appearance

    private void SyncAppearance()
    {
        ThemeDark.IsChecked = ThemeManager.IsDark;
        ThemeLight.IsChecked = !ThemeManager.IsDark;
        ReduceMotionBox.IsChecked = _services.Settings.ReduceMotion;
        var picture = _services.Settings.StartBackground;
        BackgroundText.Text = picture is { Length: > 0 } && File.Exists(picture) ? picture : "The built-in rink artwork";
    }

    private void OnThemeChosen(object sender, RoutedEventArgs e)
    {
        var dark = ReferenceEquals(sender, ThemeDark);
        if (dark != ThemeManager.IsDark)
            (Window.GetWindow(this) as MainWindow)?.SwitchTheme(dark ? UiPalette.Dark : UiPalette.Light);
        SyncAppearance();
    }

    private void OnReduceMotion(object sender, RoutedEventArgs e)
    {
        var reduce = ReduceMotionBox.IsChecked == true;
        _services.Settings.ReduceMotion = reduce;
        Motion.Reduce = reduce;
        _services.Settings.Save();
    }

    private void OnChangeBackground(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose a start screen picture", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
        if (dialog.ShowDialog() != true)
            return;
        _services.Settings.StartBackground = dialog.FileName;
        _services.Settings.Save();
        _services.NoteStartBackgroundChanged();
        SyncAppearance();
    }

    private void OnResetBackground(object sender, RoutedEventArgs e)
    {
        _services.Settings.StartBackground = null;
        _services.Settings.Save();
        _services.NoteStartBackgroundChanged();
        SyncAppearance();
    }

    private void OnBrowseGame(object sender, RoutedEventArgs e)
    {
        if (AppServices.PickFolder("Choose the NHL 19 folder that holds eboot.bin") is { } folder)
            _ = _module.MountAsync(folder);
    }

    private void OnNewProject(object sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            _services.CreateProject(dialog.ProjectFolder, dialog.ProjectName);
    }

    private void OnOpenProject(object sender, RoutedEventArgs e)
    {
        if (AppServices.PickFolder("Open a Blueline project folder") is { } folder)
            _services.OpenProject(folder);
    }

    private void OnCloseProject(object sender, RoutedEventArgs e) => _services.Workspace.SetProject(null);

    private void OnRecent(object sender, RoutedEventArgs e) => _services.OpenProject((string)((Button)sender).Content);

    private void OnBrowseSx(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Locate sx.exe", Filter = "sx.exe|sx.exe|Programs (*.exe)|*.exe" };
        if (dialog.ShowDialog() == true)
        {
            _services.Settings.SxPath = dialog.FileName;
            _services.Settings.Save();
            _services.Log.Info($"Audio tool set to {dialog.FileName}");
            Refresh();
        }
    }
}
