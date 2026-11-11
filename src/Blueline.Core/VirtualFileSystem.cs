using Blueline.Formats.Eb;

namespace Blueline.Core;

public enum ArchiveFamily { Boot, Overlays, Inxp, Game, Other, Loose }

/// <summary>A path as the game resolves it. <see cref="Entry"/> is null for a loose file on disk.</summary>
public sealed record GameFile(string Path, EbArchive? Archive, EbEntry? Entry, ArchiveFamily Family, int Layer, string? LooseFile)
{
    public bool IsFromModsFolder => (Archive?.FilePath ?? LooseFile ?? "").Contains("-mods" + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public string SourceName => (Archive?.Name ?? "Loose file") + (IsFromModsFolder ? " (mods folder)" : "");
    public bool IsLoose => Entry == null;
    public long StoredSize => Entry?.SpanOnDisk ?? new FileInfo(LooseFile!).Length;
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
    public string Folder => Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : "";
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>
/// The game's merged view of its archives: for each path, the winning version under the real mount order.
/// Paths compare case-sensitively, because the path hash does.
/// </summary>
public sealed class VirtualFileSystem : IDisposable
{
    private readonly List<EbArchive> _archives = [];
    private readonly Dictionary<string, GameFile> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<GameFile>> _layers = new(StringComparer.Ordinal);

    private VirtualFileSystem(GameInstall install) => Install = install;

    public GameInstall Install { get; }
    public IReadOnlyList<EbArchive> Archives => _archives;
    public IReadOnlyCollection<GameFile> Files => _files.Values;

    public static VirtualFileSystem Mount(GameInstall install, IProgress<(int Done, int Total)>? progress = null)
    {
        var vfs = new VirtualFileSystem(install);
        try
        {
            var order = install.StockMountOrder();
            for (var i = 0; i < order.Count; i++)
            {
                var archive = EbArchive.Open(install.ArchivePath(order[i]));
                vfs._archives.Add(archive);
                var family = FamilyOf(order[i]);
                foreach (var entry in archive.Entries)
                {
                    if (!vfs._layers.TryGetValue(entry.Path, out var stack))
                        vfs._layers[entry.Path] = stack = new List<GameFile>(1);
                    var file = new GameFile(entry.Path, archive, entry, family, stack.Count + 1, null);
                    stack.Add(file);
                    vfs._files[entry.Path] = file;
                }
                progress?.Report((i + 1, order.Count));
            }
            vfs.AddLooseFiles();
            return vfs;
        }
        catch
        {
            vfs.Dispose();
            throw;
        }
    }

    private void AddLooseFiles()
    {
        // The mods folder first, so its copy of a loose file is the one that stays.
        foreach (var root in new[] { Install.ModsFolder, Install.Root })
        {
            var folder = root == null ? null : Path.Combine(root, "nhl");
            if (folder == null || !Directory.Exists(folder))
                continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var path = Path.GetRelativePath(root!, file).Replace('\\', '/');
                if (!_files.ContainsKey(path))
                    _files[path] = new GameFile(path, null, null, ArchiveFamily.Loose, 1, file);
            }
        }
    }

    public static ArchiveFamily FamilyOf(string archiveName)
    {
        var name = archiveName.ToLowerInvariant();
        if (name.StartsWith("boot")) return ArchiveFamily.Boot;
        if (name.StartsWith("overlays")) return ArchiveFamily.Overlays;
        if (name.StartsWith("inxp") || name.StartsWith("patch_inxp")) return ArchiveFamily.Inxp;
        if (name.StartsWith("game") || name.StartsWith("patch_game")) return ArchiveFamily.Game;
        return ArchiveFamily.Other;
    }

    public GameFile? Find(string path) => _files.GetValueOrDefault(path);

    /// <summary>Every archive holding this path, in mount order; the last is the one the game uses.</summary>
    public IReadOnlyList<GameFile> Layers(string path) =>
        _layers.TryGetValue(path, out var stack) ? stack : Array.Empty<GameFile>();

    /// <summary>The file's real content, with any chunkzip wrapper removed.</summary>
    public byte[] ReadContent(GameFile file) =>
        file.Entry != null ? file.Archive!.ReadContent(file.Entry) : File.ReadAllBytes(file.LooseFile!);

    public byte[] ReadHead(GameFile file, int length)
    {
        if (file.Entry != null)
            return file.Archive!.ReadContentHead(file.Entry, length);
        using var stream = File.OpenRead(file.LooseFile!);
        var buffer = new byte[Math.Min(length, stream.Length)];
        stream.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>Whether the stored original is chunkzip-wrapped, so a replacement can match it.</summary>
    public bool IsWrapped(GameFile file)
    {
        if (file.Entry == null || file.Entry.SpanOnDisk < 8)
            return false;
        var head = new byte[8];
        file.Archive!.ReadRaw(file.Entry, 0, head);
        return Chunkzip.IsWrapped(head);
    }

    public void Dispose()
    {
        foreach (var archive in _archives)
            archive.Dispose();
    }
}
