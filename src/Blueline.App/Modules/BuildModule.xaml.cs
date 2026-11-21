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
        ExportPackButton.IsEnabled = project != null && vfs != null && project.ChangeCount > 0 && !_services.Jobs.IsBusy;
        var hasDatabase = project != null && project.TryGet(Core.Teams.TeamCatalog.DatabasePath, out _);
        RosterSyncButton.IsEnabled = hasDatabase && vfs != null && !_services.Jobs.IsBusy;
        if (!hasDatabase)
            RosterState.Text = project == null ? "" : "This project has no database edits to copy.";

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

    private UIElement? _buildView;

    /// <summary>Shows the Export mod pack page in place of the Build page, with a way back.</summary>
    public void OnExportPack(object? sender, RoutedEventArgs? e)
    {
        if (_services.Workspace.Project is not { } project || _services.Workspace.Vfs == null)
            return;
        _buildView ??= (UIElement)Content;
        Content = new PackExportPage(_services, project, () =>
        {
            Content = _buildView;
            Refresh();
        });
    }

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
        var message = $"This copies {names} into {vfs.Install.Databigs}. The game will load it the next time it starts. Your original archives are not changed.";
        IReadOnlyList<InstallOverlap> overlaps;
        try
        {
            overlaps = ModInstaller.Overlaps(project, vfs.Install, _build);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Blueline.Formats.Eb.EbFormatException or InvalidDataException)
        {
            _services.ReportError("The built archives could not be read", error);
            return;
        }

        var removeOthers = false;
        if (overlaps.Count == 0)
        {
            if (!ConfirmDialog.Ask("Install this mod?", message, "Install"))
                return;
        }
        else
        {
            var lines = string.Join("\n", overlaps.Select(o => $"\u2022 {o.FileName}: {o.Shared:N0} file{(o.Shared == 1 ? "" : "s")}, like {Path.GetFileName(o.Examples[0])}"));
            var choice = ConfirmDialog.Choose("Other mods hold the same files",
                $"These installed mods also hold files this mod changes:\n{lines}\n\nWhen two installed mods hold the same file the game can draw it wrong, for example as a black board. "
                + "Removing them keeps only this mod's copy. Their projects can install them again.\n\n" + message,
                overlaps.Count == 1 ? "Remove it and install" : $"Remove {overlaps.Count} and install", "Install anyway");
            if (choice < 0)
                return;
            removeOthers = choice == 0;
        }

        try
        {
            if (removeOthers)
                foreach (var overlap in overlaps)
                {
                    ModInstaller.RemoveOther(vfs.Install, overlap.FileName);
                    _services.Log.Info($"Removed {overlap.FileName}: it held {overlap.Shared} of the same files.");
                }
            ModInstaller.Install(project, vfs.Install, _build);
            _services.Notify($"Installed {names}." + (removeOthers ? $" Removed {string.Join(", ", overlaps.Select(o => o.FileName))}." : ""));
        }
        catch (Exception error) when (error is InstallBlockedException or IOException or UnauthorizedAccessException)
        {
            _services.ReportError("The mod was not installed", error);
        }
        Refresh();
    }

    private void OnRosterSync(object sender, RoutedEventArgs e)
    {
        if (_services.Workspace.Project is not { } project || _services.Workspace.Vfs is not { } vfs
            || !project.TryGet(Core.Teams.TeamCatalog.DatabasePath, out var stored))
            return;
        if (_services.Settings.EmulatorPath is not { Length: > 0 } emulatorPath || !File.Exists(emulatorPath))
        {
            _services.ReportError("No emulator is set", "Choose shadPS4 in Settings first, so Blueline knows where the game's saves are.");
            return;
        }
        var emulator = new Core.Emulator.ShadInstall(emulatorPath);
        var folder = Path.Combine(Core.Teams.RosterSync.SaveDataFolder(emulator.UserFolder), Core.Teams.RosterSync.CustomRoster);
        if (!Blueline.Formats.Tdb.RosterSave.IsRosterSave(folder))
        {
            _services.ReportError("There is no custom roster save", $"Nothing was found at {folder}. Load or save a custom roster in the game first.");
            return;
        }

        Blueline.Formats.Tdb.RosterSave save;
        Core.Teams.RosterSyncPlan plan;
        string meta;
        try
        {
            meta = System.Text.Encoding.UTF8.GetString(vfs.ReadContent(vfs.Find(Core.Teams.TeamCatalog.MetaPath)
                ?? throw new InvalidDataException("The game has no database meta file.")));
            var game = vfs.ReadContent(vfs.Find(Core.Teams.TeamCatalog.DatabasePath) ?? throw new InvalidDataException("The game has no team database."));
            save = Blueline.Formats.Tdb.RosterSave.Read(folder);
            plan = Core.Teams.RosterSync.Plan(game, project.ReadBlob(stored), save, meta);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            _services.ReportError("The roster save could not be read", error);
            return;
        }

        var pending = plan.Pending;
        if (plan.ToWrite == 0)
        {
            RosterState.Text = plan.Total == 0
                ? "None of this project's database edits are in tables the roster save holds." + (plan.Skipped.Count > 0 ? " " + string.Join(" ", plan.Skipped) : "")
                : $"The roster save already has all {plan.Total} of this project's edits.";
            _services.Notify(RosterState.Text);
            return;
        }
        if (Core.Emulator.ShadInstall.AnyRunning())
        {
            _services.ReportError("The game is running", "Close shadPS4 first. The game could write the roster save while Blueline does.");
            return;
        }

        var lines = Core.Teams.RosterSync.Describe(plan).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var shown = string.Join("\n", lines.Take(20)) + (lines.Length > 20 ? $"\n...and {lines.Length - 20} more" : "");
        var overwrites = pending.Count(c => c.Overwrites);
        var what = plan.WhatIsLeft;
        var message = $"This writes {what} into {folder}:\n\n{shown}\n\n"
            + (overwrites > 0 ? $"{overwrites} of the cells replace a value the roster had changed itself.\n\n" : "")
            + "A copy of the save goes to " + Core.Teams.RosterSync.BackupFolder(emulator.UserFolder) + " first. Anything else in the roster stays as it is.";
        if (!ConfirmDialog.Ask("Copy edits to the roster save?", message, "Write to roster save"))
            return;

        try
        {
            var backup = Core.Teams.RosterSync.Write(save, plan, meta, Core.Teams.RosterSync.BackupFolder(emulator.UserFolder));
            RosterState.Text = $"Wrote {what} to {Core.Teams.RosterSync.CustomRoster}. The old save is in {backup}.";
            _services.Log.Info(RosterState.Text);
            _services.Notify(RosterState.Text);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            _services.ReportError("The roster save was not changed", error);
        }
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
