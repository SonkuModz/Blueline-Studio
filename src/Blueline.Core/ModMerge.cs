using System.Security.Cryptography;
using Blueline.Formats.Eb;

namespace Blueline.Core;

public enum MergeKind
{
    /// <summary>A path the game does not have.</summary>
    New,
    /// <summary>Changes a game file this project has not touched.</summary>
    Replaces,
    /// <summary>This project already changed the file, differently.</summary>
    Conflict,
    /// <summary>Nothing to do: this project already has exactly this.</summary>
    SameAsProject,
    /// <summary>Nothing to do: the game already has exactly this.</summary>
    SameAsGame,
    /// <summary>A loose file, which the builder refuses.</summary>
    CannotBuild,
}

/// <summary>One file a mod offers, and what taking it would do.</summary>
public sealed record MergeItem(string Path, MergeKind Kind, long Size, string Sha256, string Description, bool OverModsFolder)
{
    public bool CanTake => Kind is MergeKind.New or MergeKind.Replaces or MergeKind.Conflict;
}

/// <summary>What a mod would do to a project. Nothing has been written yet.</summary>
public sealed record MergePlan(string Source, string SourceName, IReadOnlyList<MergeItem> Items, IReadOnlyList<string> Warnings)
{
    public IEnumerable<MergeItem> Takeable => Items.Where(i => i.CanTake);
    public int Count(MergeKind kind) => Items.Count(i => i.Kind == kind);

    /// <summary>What a person would tick without thinking: everything but files this project already changed.</summary>
    public IReadOnlyList<string> Default => [.. Takeable.Where(i => i.Kind != MergeKind.Conflict).Select(i => i.Path)];
}

public sealed class MergeRefusedException(string message) : Exception(message);

/// <summary>
/// Adds another mod to a project: a Blueline project, a built .big, or a folder of .big files. Files are compared
/// with the project and the game first, so a mod that ships whole archives only adds what it really changed.
/// The game and the other mod are only read. Content is compared by file, never merged inside a file.
/// </summary>
public static class ModMerge
{
    /// <summary>File types a mod rewrites whole for a small edit, so taking one side loses the other's edits.</summary>
    private static bool HoldsManyEdits(string path) => path.EndsWith(".db", StringComparison.OrdinalIgnoreCase);

    public static MergePlan Analyze(string source, ModProject project, VirtualFileSystem vfs,
        IProgress<(string Stage, int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        using var mod = OpenSource(source, project, vfs);
        var offered = mod.Files();
        var items = new List<MergeItem>(offered.Count);
        for (var i = 0; i < offered.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var file = offered[i];
            var content = file.Read();
            var hash = Convert.ToHexStringLower(SHA256.HashData(content));
            items.Add(new MergeItem(file.Path, Classify(file.Path, content, hash, project, vfs, out var overMods), content.Length, hash, file.Description, overMods));
            progress?.Report(($"Comparing {mod.Name}", i + 1, offered.Count));
        }

        var warnings = new List<string>();
        var databases = items.Where(i => i.Kind == MergeKind.Conflict && HoldsManyEdits(i.Path)).Select(i => FileName(i.Path)).ToList();
        if (databases.Count > 0)
            warnings.Add($"Both mods changed {string.Join(", ", databases)}. One whole file has to win, so the other mod's edits to it are lost.");
        var overFolder = items.Where(i => i.Kind != MergeKind.Conflict && i.CanTake && i.OverModsFolder).ToList();
        if (overFolder.Count > 0)
        {
            var names = overFolder.Where(i => HoldsManyEdits(i.Path)).Select(i => FileName(i.Path)).ToList();
            warnings.Add((overFolder.Count == 1 ? "1 file would replace your mods folder's version." : $"{overFolder.Count} files would replace your mods folder's versions.") + (names.Count > 0 ? $" Its edits in {string.Join(", ", names)} would be lost." : ""));
        }
        warnings.AddRange(mod.Warnings(vfs.Install));
        return new MergePlan(source, mod.Name, items.OrderBy(i => i.Kind).ThenBy(i => i.Path, StringComparer.Ordinal).ToList(), warnings);
    }

    /// <summary>
    /// Copies the chosen files into the project's store and returns them for <see cref="ModProject.AddStored"/>.
    /// The mod is read again, so it must still match what was compared.
    /// </summary>
    public static IReadOnlyList<ProjectChange> Store(MergePlan plan, IReadOnlyCollection<string> take, ModProject project, VirtualFileSystem vfs,
        IProgress<(string Stage, int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        using var mod = OpenSource(plan.Source, project, vfs);
        var offered = mod.Files().ToDictionary(f => f.Path, StringComparer.Ordinal);
        var chosen = plan.Takeable.Where(i => take.Contains(i.Path)).ToList();
        var stored = new List<ProjectChange>(chosen.Count);
        foreach (var item in chosen)
        {
            cancel.ThrowIfCancellationRequested();
            if (!offered.TryGetValue(item.Path, out var file))
                throw new MergeRefusedException($"{plan.SourceName} no longer holds {item.Path}. Nothing was added.");
            var content = file.Read();
            var hash = Convert.ToHexStringLower(SHA256.HashData(content));
            if (hash != item.Sha256)
                throw new MergeRefusedException($"{item.Path} in {plan.SourceName} changed after it was compared. Nothing was added.");
            if (project.StoreContent(content) != hash)
                throw new MergeRefusedException($"{item.Path} was stored differently from what was read. Nothing was added.");
            stored.Add(new ProjectChange(item.Path, hash, content.Length, item.Description, DateTimeOffset.Now));
            progress?.Report(($"Adding from {plan.SourceName}", stored.Count, chosen.Count));
        }
        return stored;
    }

    private static MergeKind Classify(string path, byte[] content, string hash, ModProject project, VirtualFileSystem vfs, out bool overMods)
    {
        var game = vfs.Find(path);
        overMods = game?.IsFromModsFolder == true;
        if (game?.IsLoose == true)
            return MergeKind.CannotBuild;
        var inProject = project.TryGet(path, out var change);
        if (inProject && change.Blob == hash)
            return MergeKind.SameAsProject;
        if (game != null && vfs.ReadContent(game).AsSpan().SequenceEqual(content))
            return MergeKind.SameAsGame;
        return inProject ? MergeKind.Conflict : game != null ? MergeKind.Replaces : MergeKind.New;
    }

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static bool SamePath(string a, string b) =>
        Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static IMergeSource OpenSource(string source, ModProject project, VirtualFileSystem vfs)
    {
        if (vfs.Install.ModsFolder is { } mods && (SamePath(source, mods) || Path.GetFullPath(source).StartsWith(Path.GetFullPath(mods) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new MergeRefusedException("Studio already reads the mods folder under every project, so its changes are already included.");

        if (File.Exists(source))
            return new ArchiveSource(Path.GetFileName(source), [source]);
        if (!Directory.Exists(source))
            throw new MergeRefusedException($"{source} does not exist.");

        if (File.Exists(Path.Combine(source, "project.json")))
        {
            if (SamePath(source, project.Folder))
                throw new MergeRefusedException("That is the project already open.");
            return new ProjectSource(ModProject.Open(source));
        }

        var databigs = Path.Combine(source, "databigs");
        var folder = Directory.Exists(databigs) ? databigs : source;
        var archives = GameInstall.MountOrder(Directory.GetFiles(folder, "*.big").Select(p => Path.GetFileName(p)!)).Select(n => Path.Combine(folder, n)).ToList();
        if (archives.Count == 0)
            throw new MergeRefusedException($"{source} holds no Blueline project and no .big archives.");
        return new ArchiveSource(Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)), archives);
    }

    private sealed record OfferedFile(string Path, string Description, Func<byte[]> Read);

    private interface IMergeSource : IDisposable
    {
        string Name { get; }
        IReadOnlyList<OfferedFile> Files();
        IEnumerable<string> Warnings(GameInstall install);
    }

    private sealed class ProjectSource(ModProject mod) : IMergeSource
    {
        public string Name => mod.Name;

        public IReadOnlyList<OfferedFile> Files() =>
            [.. mod.Changes.OrderBy(c => c.Path, StringComparer.Ordinal).Select(c => new OfferedFile(c.Path, $"{mod.Name}: {c.Description}", () => mod.ReadBlob(c)))];

        public IEnumerable<string> Warnings(GameInstall install)
        {
            if (ModInstaller.IsInstalled(mod, install))
                yield return $"{mod.Name} is still installed. After installing the combined mod, uninstall it so the game does not load both.";
        }

        public void Dispose() { }
    }

    private sealed class ArchiveSource : IMergeSource
    {
        private readonly List<EbArchive> _archives = [];
        private readonly IReadOnlyList<string> _paths;

        public ArchiveSource(string name, IReadOnlyList<string> paths)
        {
            Name = name;
            _paths = paths;
            try
            {
                foreach (var path in paths)
                {
                    var archive = EbArchive.Open(path);
                    _archives.Add(archive);
                    var audit = EbAudit.Check(archive);
                    if (!audit.Clean)
                        throw new MergeRefusedException($"{archive.Name} failed its structure check: {string.Join("; ", audit.Problems.Take(3))}");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public string Name { get; }

        public IReadOnlyList<OfferedFile> Files()
        {
            // Later archives win, as they do in the game.
            var files = new Dictionary<string, OfferedFile>(StringComparer.Ordinal);
            foreach (var archive in _archives)
                foreach (var entry in archive.Entries)
                    files[entry.Path] = new OfferedFile(entry.Path, $"From {archive.Name}", () => archive.ReadContent(entry));
            return [.. files.Values];
        }

        public IEnumerable<string> Warnings(GameInstall install)
        {
            foreach (var path in _paths)
                if (GameInstall.IsModArchive(Path.GetFileName(path)) && SamePath(Path.GetDirectoryName(path)!, install.Databigs))
                    yield return $"{Path.GetFileName(path)} is still installed. After installing the combined mod, remove it so the game does not load both.";
        }

        public void Dispose()
        {
            foreach (var archive in _archives)
                archive.Dispose();
        }
    }
}
