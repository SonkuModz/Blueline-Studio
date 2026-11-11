using System.Collections.ObjectModel;
using System.Windows;
using Blueline.Core;
using Microsoft.Win32;

namespace Blueline.App.Shell;

/// <summary>What the shell gives every module.</summary>
public sealed class AppServices
{
    public AppServices(Workspace workspace, AppSettings settings)
    {
        Workspace = workspace;
        Settings = settings;
        Jobs = new JobRunner(workspace.Log);
        workspace.Changed += () => Application.Current?.Dispatcher.BeginInvoke(() => Index?.SyncWith(Workspace.Project));
    }

    public Workspace Workspace { get; }
    public AppSettings Settings { get; }
    public SessionLog Log => Workspace.Log;
    public JobRunner Jobs { get; }
    public Controls.AssetIndex? Index { get; private set; }

    private FfmpegTool? _ffmpeg;
    private VideoPreviewCache? _videoPreviews;

    /// <summary>The user's FFmpeg, or null if none can be found.</summary>
    public FfmpegTool? Ffmpeg => _ffmpeg ??= FfmpegTool.Locate(Settings.FfmpegPath);

    public VideoPreviewCache? VideoPreviews => Ffmpeg is { } tool
        ? _videoPreviews ??= new VideoPreviewCache(tool, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blueline", "video-cache"))
        : null;

    /// <summary>Call after the chosen ffmpeg.exe changes.</summary>
    public void ResetVideoTool() => (_ffmpeg, _videoPreviews) = (null, null);

    public void SetGame(VirtualFileSystem vfs, Controls.AssetIndex index)
    {
        Index = index;
        index.SyncWith(Workspace.Project);
        Workspace.SetGame(vfs);
    }

    public event Action<string, string, Exception?>? ErrorReported;
    public event Action<string>? Notified;
    public event Action<string, string?>? NavigationRequested;

    /// <summary>Raised when the picture for the start screen was changed or cleared in Settings.</summary>
    public event Action? StartBackgroundChanged;

    public void NoteStartBackgroundChanged() => StartBackgroundChanged?.Invoke();

    public void ReportError(string title, string message) { Log.Warning($"{title}: {message}"); ErrorReported?.Invoke(title, message, null); }

    public void ReportError(string title, Exception exception) => ErrorReported?.Invoke(title, exception.Message, exception);

    public void Notify(string message) { Log.Info(message); Notified?.Invoke(message); }

    /// <summary>Opens a module, optionally on a specific game path.</summary>
    public void Navigate(string tab, string? path = null) => NavigationRequested?.Invoke(tab, path);

    /// <summary>The open project, or asks the user to start one. Null means they declined.</summary>
    public ModProject? RequireProject()
    {
        if (Workspace.Project != null)
            return Workspace.Project;

        var dialog = new NewProjectDialog { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
            return null;
        return CreateProject(dialog.ProjectFolder, dialog.ProjectName);
    }

    public ModProject? CreateProject(string folder, string name)
    {
        try
        {
            var project = ModProject.Create(folder, name);
            Settings.NoteRecentProject(folder);
            Workspace.SetProject(project);
            return project;
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            ReportError("The project could not be created", e);
            return null;
        }
    }

    public void OpenProject(string folder)
    {
        try
        {
            var project = ModProject.Open(folder);
            Settings.NoteRecentProject(folder);
            Workspace.SetProject(project);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            ReportError("The project could not be opened", e);
        }
    }

    public static string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
