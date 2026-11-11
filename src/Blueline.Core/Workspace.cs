namespace Blueline.Core;

/// <summary>What every module shares: the mounted game, the open project and the log.</summary>
public sealed class Workspace(SessionLog log) : IDisposable
{
    public SessionLog Log { get; } = log;
    public VirtualFileSystem? Vfs { get; private set; }
    public ModProject? Project { get; private set; }

    /// <summary>Raised when the game or project changes, and on any edit to the project.</summary>
    public event Action? Changed;

    public void SetGame(VirtualFileSystem vfs)
    {
        Vfs?.Dispose();
        Vfs = vfs;
        Log.Info($"Mounted {vfs.Install.Root}: {vfs.Archives.Count} archives, {vfs.Files.Count:N0} paths.");
        if (vfs.Install.ModsFolder is { } mods)
            Log.Info($"Using {mods}: added {string.Join(", ", vfs.Install.ArchivesAddedByMods)}; replaced {string.Join(", ", vfs.Install.ArchivesReplacedByMods)}.");
        Changed?.Invoke();
    }

    public void SetProject(ModProject? project)
    {
        if (Project != null)
            Project.Changed -= OnProjectChanged;
        Project = project;
        if (project != null)
        {
            project.Changed += OnProjectChanged;
            Log.Info($"Opened project '{project.Name}' with {project.ChangeCount} changes.");
        }
        Changed?.Invoke();
    }

    private void OnProjectChanged() => Changed?.Invoke();

    /// <summary>A path's content as the mod will see it: the project's version if it has one, otherwise the game's.</summary>
    public byte[]? ReadCurrent(string path)
    {
        if (Project != null && Project.TryGet(path, out var change))
            return Project.ReadBlob(change);
        var file = Vfs?.Find(path);
        return file == null ? null : Vfs!.ReadContent(file);
    }

    public bool IsEdited(string path) => Project != null && Project.TryGet(path, out _);

    public void Dispose() => Vfs?.Dispose();
}
