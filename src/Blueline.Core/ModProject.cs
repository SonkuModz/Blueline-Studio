using System.Security.Cryptography;
using System.Text.Json;

namespace Blueline.Core;

/// <summary>One file the project replaces or adds. Content lives in the project's blob store.</summary>
public sealed record ProjectChange(string Path, string Blob, long Size, string Description, DateTimeOffset When);

public sealed record InstalledArchive(string FileName, long Size, string Sha256);

/// <summary>
/// A mod: a folder with project.json and a content-addressed files/ store. Nothing outside this folder is
/// touched until Install. Every edit is a new state of the change list, so undo and redo are exact.
/// Blobs stay until <see cref="RemoveUnusedBlobs"/>, which keeps undo safe.
/// </summary>
public sealed class ModProject
{
    private const string FileName = "project.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record Stored(int Format, string Name, string Author, string Notes, List<ProjectChange> Changes, List<InstalledArchive> Installed);

    private readonly Stack<(Dictionary<string, ProjectChange> State, string Label)> _undo = new();
    private readonly Stack<(Dictionary<string, ProjectChange> State, string Label)> _redo = new();
    private Dictionary<string, ProjectChange> _changes;

    private ModProject(string folder, string name, string author, string notes, Dictionary<string, ProjectChange> changes, List<InstalledArchive> installed)
    {
        Folder = folder;
        Name = name;
        Author = author;
        Notes = notes;
        _changes = changes;
        Installed = installed;
    }

    public string Folder { get; }
    public string Name { get; private set; }
    public string Author { get; private set; }
    public string Notes { get; private set; }
    public List<InstalledArchive> Installed { get; }
    public IReadOnlyCollection<ProjectChange> Changes => _changes.Values;
    public int ChangeCount => _changes.Count;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Count > 0 ? _undo.Peek().Label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo.Peek().Label : null;
    public string BuildFolder => Path.Combine(Folder, "build");

    public event Action? Changed;

    /// <summary>The name used in archive file names: lowercase letters, digits and hyphens.</summary>
    public string Slug
    {
        get
        {
            var chars = Name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
            var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
            return slug.Length == 0 ? "mod" : slug[..Math.Min(slug.Length, 40)];
        }
    }

    public static ModProject Create(string folder, string name, string author = "", string notes = "")
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A project needs a name.");
        if (File.Exists(Path.Combine(folder, FileName)))
            throw new InvalidOperationException($"{folder} already holds a project.");
        Directory.CreateDirectory(Path.Combine(folder, "files"));
        var project = new ModProject(folder, name.Trim(), author, notes, new(StringComparer.Ordinal), []);
        project.Save();
        return project;
    }

    public static ModProject Open(string folder)
    {
        var file = Path.Combine(folder, FileName);
        if (!File.Exists(file))
            throw new FileNotFoundException($"{folder} does not hold a Blueline project.");
        var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(file), Json)
            ?? throw new InvalidDataException($"{file} is empty.");
        if (stored.Format != 1)
            throw new InvalidDataException($"{file} has format {stored.Format}; this build reads format 1.");
        var project = new ModProject(folder, stored.Name, stored.Author, stored.Notes,
            stored.Changes.ToDictionary(c => c.Path, StringComparer.Ordinal), stored.Installed);
        foreach (var change in project.Changes)
            if (!File.Exists(project.BlobPath(change.Blob)))
                throw new InvalidDataException($"The project's store is missing the content for {change.Path}.");
        return project;
    }

    public void Rename(string name, string author, string notes)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A project needs a name.");
        Name = name.Trim();
        Author = author;
        Notes = notes;
        Save();
        Changed?.Invoke();
    }

    public bool TryGet(string path, out ProjectChange change) => _changes.TryGetValue(path, out change!);

    public byte[] ReadBlob(ProjectChange change) => File.ReadAllBytes(BlobPath(change.Blob));

    public void Set(string path, byte[] content, string description) =>
        SetMany([(path, content, description)], description);

    /// <summary>Applies several files as one undo step.</summary>
    public void SetMany(IReadOnlyCollection<(string Path, byte[] Content, string Description)> items, string label)
    {
        if (items.Count == 0)
            return;
        var next = new Dictionary<string, ProjectChange>(_changes, StringComparer.Ordinal);
        foreach (var (path, content, description) in items)
            next[path] = new ProjectChange(path, Store(content), content.Length, description, DateTimeOffset.Now);
        Commit(next, label);
    }

    /// <summary>Puts content in the store without changing the mod, so a large merge need not sit in memory.</summary>
    public string StoreContent(byte[] content) => Store(content);

    /// <summary>Applies changes whose content is already stored, as one undo step.</summary>
    public void AddStored(IReadOnlyCollection<ProjectChange> changes, string label)
    {
        if (changes.Count == 0)
            return;
        var next = new Dictionary<string, ProjectChange>(_changes, StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (!File.Exists(BlobPath(change.Blob)))
                throw new InvalidDataException($"The project's store is missing the content for {change.Path}.");
            next[change.Path] = change;
        }
        Commit(next, label);
    }

    public void Revert(IReadOnlyCollection<string> paths, string label)
    {
        var next = new Dictionary<string, ProjectChange>(_changes, StringComparer.Ordinal);
        if (paths.Count(next.Remove) > 0)
            Commit(next, label);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var (state, label) = _undo.Pop();
        _redo.Push((_changes, label));
        _changes = state;
        Save();
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var (state, label) = _redo.Pop();
        _undo.Push((_changes, label));
        _changes = state;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Deletes stored content no change refers to, and clears the undo history that needed it.</summary>
    public int RemoveUnusedBlobs()
    {
        _undo.Clear();
        _redo.Clear();
        var used = _changes.Values.Select(c => c.Blob).ToHashSet();
        var removed = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(Folder, "files")))
            if (!used.Contains(Path.GetFileName(file)))
            {
                File.Delete(file);
                removed++;
            }
        Changed?.Invoke();
        return removed;
    }

    public void Save()
    {
        var stored = new Stored(1, Name, Author, Notes, _changes.Values.OrderBy(c => c.Path, StringComparer.Ordinal).ToList(), Installed);
        var file = Path.Combine(Folder, FileName);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(stored, Json));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    private void Commit(Dictionary<string, ProjectChange> next, string label)
    {
        _undo.Push((_changes, label));
        _redo.Clear();
        _changes = next;
        Save();
        Changed?.Invoke();
    }

    private string BlobPath(string blob) => Path.Combine(Folder, "files", blob);

    private string Store(byte[] content)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var path = BlobPath(hash);
        if (!File.Exists(path))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        return hash;
    }
}
