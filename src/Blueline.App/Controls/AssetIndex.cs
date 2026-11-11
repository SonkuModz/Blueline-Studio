using System.ComponentModel;
using System.Runtime.CompilerServices;
using Blueline.Core;

namespace Blueline.App.Controls;

/// <summary>One line in an asset list. Rows are shared by every browser, so an edit marks all of them.</summary>
public sealed class AssetRow(string path, GameFile? file) : INotifyPropertyChanged
{
    private bool _edited;

    public string Path { get; } = path;
    public GameFile? File { get; } = file;
    public string Name { get; } = path[(path.LastIndexOf('/') + 1)..];
    public string Folder { get; } = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    public AssetCategory Category { get; } = AssetCategories.Of(path);
    public string Source => File?.SourceName ?? "New in project";

    public string SizeText => File == null ? "" : FormatSize.Of(File.StoredSize);
    public long Size => File?.StoredSize ?? 0;

    public bool IsEdited
    {
        get => _edited;
        set
        {
            if (_edited == value) return;
            _edited = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEdited)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditedMark)));
        }
    }

    public string EditedMark => _edited ? "●" : "";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class FormatSize
{
    public static string Of(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>A folder in the asset tree. Its children are created when it first opens.</summary>
public sealed class FolderNode(string name, string path, AssetIndex index)
{
    private List<FolderNode>? _children;

    public string Name { get; } = name;
    public string Path { get; } = path;
    public bool IsExpanded { get; set; } = path.Count(c => c == '/') < 1;

    public IReadOnlyList<FolderNode> Children =>
        _children ??= index.ChildFolders(Path).Select(c => new FolderNode(c, Path.Length == 0 ? c : Path + "/" + c, index)).ToList();

    public override string ToString() => Name;
}

/// <summary>Every path in the game as list rows, built once per mount.</summary>
public sealed class AssetIndex
{
    private readonly Dictionary<string, AssetRow> _byPath;
    private readonly Dictionary<string, SortedSet<string>> _folders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssetRow> _added = new(StringComparer.Ordinal);
    private HashSet<string> _marked = new(StringComparer.Ordinal);

    public AssetIndex(VirtualFileSystem vfs)
    {
        _byPath = vfs.Files.Select(f => new AssetRow(f.Path, f)).ToDictionary(r => r.Path, StringComparer.Ordinal);
        Rows = _byPath.Values.OrderBy(r => r.Path, StringComparer.Ordinal).ToArray();
        foreach (var folder in Rows.Select(r => r.Folder).Distinct(StringComparer.Ordinal))
            AddFolder(folder);
    }

    public AssetRow[] Rows { get; private set; }
    public AssetRow? Find(string path) => _byPath.GetValueOrDefault(path);

    private void AddFolder(string folder)
    {
        while (folder.Length > 0)
        {
            var cut = folder.LastIndexOf('/');
            var parent = cut < 0 ? "" : folder[..cut];
            var child = cut < 0 ? folder : folder[(cut + 1)..];
            if (!_folders.TryGetValue(parent, out var set))
                _folders[parent] = set = new SortedSet<string>(StringComparer.Ordinal);
            if (!set.Add(child))
                return;
            folder = parent;
        }
    }

    public IEnumerable<string> ChildFolders(string folder) =>
        _folders.TryGetValue(folder, out var set) ? set : Enumerable.Empty<string>();

    /// <summary>Marks edited rows, and adds rows for paths only the project has.</summary>
    public void SyncWith(ModProject? project)
    {
        var current = new HashSet<string>(StringComparer.Ordinal);
        if (project != null)
        {
            foreach (var change in project.Changes)
            {
                current.Add(change.Path);
                if (!_byPath.TryGetValue(change.Path, out var row))
                {
                    row = new AssetRow(change.Path, null);
                    _byPath[change.Path] = row;
                    _added[change.Path] = row;
                    Rows = [.. Rows, row];
                    AddFolder(row.Folder);
                }
                row.IsEdited = true;
            }
        }
        foreach (var path in _marked)
            if (!current.Contains(path) && _byPath.TryGetValue(path, out var row))
                row.IsEdited = false;
        _marked = current;
    }
}

public sealed class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Raise([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
