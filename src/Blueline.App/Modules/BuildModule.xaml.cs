using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core;

namespace Blueline.App.Modules;

public sealed record ChangeRow(string Path, string Description, string SizeText, string Layer);

/// <summary>A file offered by another mod, with the choice of whether to take it.</summary>
public sealed class MergeRow(MergeItem item, bool take) : INotifyPropertyChanged
{
    private bool _take = take;

    public MergeItem Item { get; } = item;
    public string Path => Item.Path;
    public string SizeText => FormatSize.Of(Item.Size);
    public bool CanTake => Item.CanTake;

    public string Effect => Item.Kind switch
    {
        MergeKind.Conflict => "Conflict: this project changed it too",
        MergeKind.New => "New file",
        MergeKind.Replaces => Item.OverModsFolder ? "Replaces your mods folder's file" : "Replaces the game's file",
        MergeKind.CannotBuild => "Loose file, cannot be built",
        _ => "",
    };

    public bool Take
    {
        get => _take;
        set
        {
            _take = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Take)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Review the project's changes, build verified overlays, install and uninstall them.</summary>
public partial class BuildModule : UserControl, IEditorModule
{
    private readonly AppServices _services;
    private BuildResult? _build;
    private MergePlan? _plan;
    private List<MergeRow> _mergeRows = [];

    public BuildModule(AppServices services)
    {
        _services = services;
        InitializeComponent();
        services.Workspace.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            // Any edit makes the last build stale.
            _build = null;
            BuildResultText.Text = "";
            Warnings.ItemsSource = null;
            // What was compared no longer describes the project.
            CloseReview();
            if (IsVisible)
                Refresh();
        });
        Refresh();
    }

    public string Title => "Build";
    public FrameworkElement View => this;
    public void Activated() => Refresh();
    public void OpenPath(string path) { }

    private void Refresh()
    {
        var project = _services.Workspace.Project;
        var vfs = _services.Workspace.Vfs;
        Heading.Text = project == null ? "No project open" : $"{project.Name}: {project.ChangeCount} changed file{(project.ChangeCount == 1 ? "" : "s")}";

        Changes.ItemsSource = project?.Changes.OrderBy(c => c.Path, StringComparer.Ordinal)
            .Select(c => new ChangeRow(c.Path, c.Description, FormatSize.Of(c.Size), LayerOf(c.Path, vfs))).ToList();

        UpdateEmptyChanges();

        var ready = project != null && vfs != null && project.ChangeCount > 0 && !_services.Jobs.IsBusy;
        BuildButton.IsEnabled = ready;
        AddFolderButton.IsEnabled = AddArchiveButton.IsEnabled = vfs != null && !_services.Jobs.IsBusy;
        OpenFolder.IsEnabled = project != null && Directory.Exists(project.BuildFolder);
        InstallButton.IsEnabled = _build != null && vfs != null;
        var installed = project != null && vfs != null && ModInstaller.IsInstalled(project, vfs.Install);
        UninstallButton.IsEnabled = installed;
        InstallState.Text = project == null ? ""
            : installed ? "Installed: " + string.Join(", ", project.Installed.Select(i => i.FileName))
            : _build == null ? "Build first to install." : "Ready to install.";
        RevertSelected.IsEnabled = Changes.SelectedItems.Count > 0;

        var mods = vfs != null ? ModInstaller.InstalledMods(vfs.Install) : [];
        Installed.ItemsSource = mods;
        NoneInstalled.Visibility = mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string LayerOf(string path, VirtualFileSystem? vfs) => vfs?.Find(path)?.Family switch
    {
        ArchiveFamily.Game => "game",
        ArchiveFamily.Inxp => "inxp",
        ArchiveFamily.Loose => "not supported",
        null => "inxp (new)",
        _ => "inxp (early)",
    };

    private void OnChangeSelected(object sender, SelectionChangedEventArgs e) => RevertSelected.IsEnabled = Changes.SelectedItems.Count > 0;

    private void OnOpenChange(object sender, MouseButtonEventArgs e)
    {
        if (Changes.SelectedItem is ChangeRow row)
            _services.Navigate("Assets", row.Path);
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        var paths = Changes.SelectedItems.Cast<ChangeRow>().Select(r => r.Path).ToList();
        if (paths.Count > 0)
            _services.Workspace.Project?.Revert(paths, paths.Count == 1 ? $"Reverted {Path.GetFileName(paths[0])}" : $"Reverted {paths.Count} files");
    }

    private void OnBuild(object sender, RoutedEventArgs e) => _ = BuildAsync();

    public async Task BuildAsync()
    {
        if (_services.Workspace.Project is not { } project || _services.Workspace.Vfs is not { } vfs)
            return;
        await _services.Jobs.RunAsync($"Building {project.Name}", context =>
            OverlayBuilder.Build(project, vfs, project.BuildFolder, new Reporter(context), context.Cancel),
            result =>
            {
                _build = result;
                BuildResultText.Text = string.Join("\n", result.Archives.Select(a => $"{a.FileName}: {a.Files} file{(a.Files == 1 ? "" : "s")}, {FormatSize.Of(a.Size)}, verified"));
                Warnings.ItemsSource = result.Warnings;
                foreach (var warning in result.Warnings)
                    _services.Log.Warning(warning);
                _services.Notify($"Built {result.Archives.Count} archive{(result.Archives.Count == 1 ? "" : "s")} in {(result.Elapsed.TotalSeconds < 0.1 ? "under 0.1" : result.Elapsed.TotalSeconds.ToString("F1"))}s.");
                Refresh();
            });
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (AppServices.PickFolder("Choose a Blueline project, or a folder of .big archives") is { } folder)
            _ = CompareMod(folder);
    }

    private void OnAddArchive(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose a mod archive", Filter = "Mod archive|*.big" };
        if (dialog.ShowDialog() == true)
            _ = CompareMod(dialog.FileName);
    }

    /// <summary>Compares a mod with the project and shows what it would do. Changes nothing.</summary>
    public async Task CompareMod(string source)
    {
        if (_services.Workspace.Vfs is not { } vfs || _services.RequireProject() is not { } project)
            return;
        await _services.Jobs.RunAsync($"Comparing {Path.GetFileName(source)}", context =>
            ModMerge.Analyze(source, project, vfs, new Reporter(context), context.Cancel),
            ShowReview);
    }

    private void ShowReview(MergePlan plan)
    {
        var offers = plan.Takeable.Count() + plan.Count(MergeKind.CannotBuild);
        if (offers == 0)
        {
            _services.Notify($"{plan.SourceName} has nothing to add: all {plan.Items.Count} of its files are already the same here.");
            return;
        }

        _plan = plan;
        var chosen = plan.Default.ToHashSet(StringComparer.Ordinal);
        _mergeRows = [.. plan.Items.Where(i => i.CanTake || i.Kind == MergeKind.CannotBuild)
            .OrderBy(i => i.Kind != MergeKind.Conflict).ThenBy(i => i.Kind).ThenBy(i => i.Path, StringComparer.Ordinal)
            .Select(i => new MergeRow(i, chosen.Contains(i.Path)))];
        MergeRows.ItemsSource = _mergeRows;
        MergeTitle.Text = plan.SourceName;
        MergeTitle.ToolTip = plan.Source;

        var parts = new List<string>();
        void Part(int count, string words) { if (count > 0) parts.Add($"{count:N0} {words}"); }
        Part(plan.Count(MergeKind.Replaces), "changed from the game");
        Part(plan.Count(MergeKind.New), "new");
        Part(plan.Count(MergeKind.Conflict), plan.Count(MergeKind.Conflict) == 1 ? "conflict" : "conflicts");
        Part(plan.Count(MergeKind.SameAsProject) + plan.Count(MergeKind.SameAsGame), "already the same, left out");
        Part(plan.Count(MergeKind.CannotBuild), "cannot be built");
        MergeSummary.Text = string.Join(", ", parts) + ".";
        MergeWarnings.ItemsSource = plan.Warnings;
        foreach (var warning in plan.Warnings)
            _services.Log.Warning(warning);
        ConflictChoice.Visibility = plan.Count(MergeKind.Conflict) > 0 ? Visibility.Visible : Visibility.Collapsed;

        Changes.Visibility = Visibility.Collapsed;
        MergeRows.Visibility = Visibility.Visible;
        UpdateEmptyChanges();
        MergeStart.Visibility = Visibility.Collapsed;
        MergeReview.Visibility = Visibility.Visible;
        UpdateMergeButton();
    }

    private void CloseReview()
    {
        _plan = null;
        _mergeRows = [];
        MergeRows.ItemsSource = null;
        MergeWarnings.ItemsSource = null;
        Changes.Visibility = Visibility.Visible;
        MergeRows.Visibility = Visibility.Collapsed;
        MergeStart.Visibility = Visibility.Visible;
        MergeReview.Visibility = Visibility.Collapsed;
        UpdateEmptyChanges();
    }

    /// <summary>An empty table says what to do next instead of sitting blank.</summary>
    private void UpdateEmptyChanges()
    {
        var project = _services.Workspace.Project;
        var empty = Changes.Visibility == Visibility.Visible && (project == null || project.ChangeCount == 0);
        EmptyChanges.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty)
            return;
        EmptyTitle.Text = project == null ? "No project open" : "Nothing changed yet";
        EmptyText.Text = project == null
            ? "Open or create a project from the project menu at the top, then every file you replace shows up here."
            : "Replace a texture, edit a team or change an overlay, and each change is listed here, ready to build.";
    }

    private void UpdateMergeButton()
    {
        var count = _mergeRows.Count(r => r.Take);
        MergeButton.Content = count == 1 ? "Add 1 file" : $"Add {count:N0} files";
        MergeButton.IsEnabled = count > 0 && !_services.Jobs.IsBusy;
    }

    private void OnMergeRowTicked(object sender, RoutedEventArgs e) => UpdateMergeButton();

    private void SetConflicts(bool take)
    {
        foreach (var row in _mergeRows.Where(r => r.Item.Kind == MergeKind.Conflict))
            row.Take = take;
        UpdateMergeButton();
    }

    private void OnUseTheirs(object sender, RoutedEventArgs e) => SetConflicts(true);
    private void OnKeepMine(object sender, RoutedEventArgs e) => SetConflicts(false);
    private void OnCancelMerge(object sender, RoutedEventArgs e) => CloseReview();
    private void OnMerge(object sender, RoutedEventArgs e) => _ = MergeChosen();

    /// <summary>The review as it stands, for the UI tour.</summary>
    public IReadOnlyList<MergeRow> ReviewRows => _mergeRows;
    public void UseTheirsOnConflicts() => SetConflicts(true);

    public async Task MergeChosen()
    {
        if (_plan is not { } plan || _services.Workspace.Project is not { } project || _services.Workspace.Vfs is not { } vfs)
            return;
        var take = _mergeRows.Where(r => r.Take).Select(r => r.Path).ToHashSet(StringComparer.Ordinal);
        await _services.Jobs.RunAsync($"Adding from {plan.SourceName}", context =>
            ModMerge.Store(plan, take, project, vfs, new Reporter(context), context.Cancel),
            stored =>
            {
                project.AddStored(stored, $"Added {stored.Count} files from {plan.SourceName}");
                CloseReview();
                Refresh();
                _services.Notify($"Added {stored.Count:N0} file{(stored.Count == 1 ? "" : "s")} from {plan.SourceName}. Undo takes them all back out.");
            });
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_services.Workspace.Project is { } project && Directory.Exists(project.BuildFolder))
            Process.Start(new ProcessStartInfo(project.BuildFolder) { UseShellExecute = true });
    }

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_services.Workspace.Project is not { } project || _services.Workspace.Vfs is not { } vfs || _build == null)
            return;
        var names = string.Join(", ", _build.Archives.Select(a => a.FileName));
        if (!ConfirmDialog.Ask("Install this mod?", $"This copies {names} into {vfs.Install.Databigs}. The game will load it the next time it starts. Your original archives are not changed.", "Install"))
            return;

        try
        {
            ModInstaller.Install(project, vfs.Install, _build);
            _services.Notify($"Installed {names}.");
        }
        catch (Exception error) when (error is InstallBlockedException or IOException or UnauthorizedAccessException)
        {
            _services.ReportError("The mod was not installed", error);
        }
        Refresh();
    }

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_services.Workspace.Project is not { } project || _services.Workspace.Vfs is not { } vfs)
            return;
        var names = string.Join(", ", project.Installed.Select(i => i.FileName));
        if (!ConfirmDialog.Ask("Uninstall this mod?", $"This deletes {names} from {vfs.Install.Databigs}.", "Uninstall"))
            return;

        try
        {
            var problems = ModInstaller.Uninstall(project, vfs.Install);
            if (problems.Count > 0)
                _services.ReportError("Some archives were left in place", string.Join(" ", problems));
            else
                _services.Notify($"Uninstalled {names}.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _services.ReportError("The mod was not uninstalled", error);
        }
        Refresh();
    }

    private sealed class Reporter(JobContext context) : IProgress<(string Stage, int Done, int Total)>
    {
        public void Report((string Stage, int Done, int Total) value) => context.Progress(value.Stage, value.Done, value.Total);
    }
}
