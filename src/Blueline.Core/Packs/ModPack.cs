using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Blueline.Core.Packs;

public sealed record PackGame(string TitleId, string AppVersion);

public sealed record PackFile(string Path, string Blob, long Size, string? Stock, string Description);

public sealed record PackArt(string? Cover, string? Icon, IReadOnlyList<string> Shots)
{
    public static readonly PackArt None = new(null, null, []);
}

/// <summary>A save the pack carries. <see cref="From"/> is the slot it was exported from, for information only.</summary>
public sealed record PackSave(string Kind, string From, string Folder, string Title, IReadOnlyDictionary<string, string> Files);

/// <summary>A mod pack's manifest.json. See docs/MOD_PACKS.md.</summary>
public sealed record PackManifest(
    int Format, string Id, string Slug, string Name, string Author, string Version, string Description, PackArt Art,
    DateTimeOffset Created, string Blueline, PackGame Game, IReadOnlyList<PackFile> Files, IReadOnlyList<PackSave> Saves,
    IReadOnlyList<string> LeftOut)
{
    public PackSave? Roster => Saves.FirstOrDefault(s => s.Kind == "roster");
    public long Bytes => Files.Sum(f => f.Size);
}

public sealed class PackFormatException(string message) : Exception(message);

/// <summary>
/// Reads and writes .blpack files: a zip holding manifest.json, art/, files/&lt;sha256&gt; and saves/. A pack comes from someone
/// else, so nothing in it is trusted: entry names are checked, sizes are capped, and every blob is hashed before use.
/// </summary>
public static partial class ModPack
{
    public const int Format = 1;
    public const string Extension = ".blpack";
    /// <summary>A sanity cap against a broken or hostile pack, well above real mods (the Overhaul project is about 6 GB). Free disk space is checked separately at install.</summary>
    public const long MaxUnpacked = 10L << 30;
    public const string RosterFolder = "saves/roster";

    public static readonly (int Width, int Height) CoverSize = (1280, 720);
    public static readonly (int Width, int Height) IconSize = (256, 256);
    public static readonly (int Width, int Height) ShotSize = (1280, 720);
    public const int MaxShots = 6;

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();

    [GeneratedRegex("^(manifest\\.json|art/(cover|icon|shot[1-6])\\.png|files/[0-9a-f]{64}|saves/roster/[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*)$")]
    private static partial Regex EntryPattern();

    /// <summary>A name for archive files: lowercase letters, digits and hyphens, at most 24 characters.</summary>
    public static string MakeSlug(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        slug = slug.Length == 0 ? "pack" : slug[..Math.Min(slug.Length, 24)].TrimEnd('-');
        return slug;
    }

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Opens a pack and checks its manifest and every entry name. Blob contents are checked by <see cref="Unpack"/>.</summary>
    public static PackManifest ReadManifest(string packPath)
    {
        using var zip = ZipFile.OpenRead(packPath);
        return ReadManifest(zip);
    }

    private static PackManifest ReadManifest(ZipArchive zip)
    {
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;
            if (!EntryPattern().IsMatch(entry.FullName) || entry.FullName.Contains(".."))
                throw new PackFormatException($"The pack holds an entry it should not: {entry.FullName}");
            if (entry.CompressedLength > 0 && entry.Length / Math.Max(1, entry.CompressedLength) > 1000 && entry.Length > 1 << 20)
                throw new PackFormatException($"{entry.FullName} unpacks to over 1000 times its packed size, so the pack was refused.");
            total += entry.Length;
        }
        if (total > MaxUnpacked)
            throw new PackFormatException($"The pack unpacks to more than {MaxUnpacked >> 30} GB, so it was refused.");

        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new PackFormatException("The file has no manifest.json, so it is not a Blueline mod pack.");
        PackManifest manifest;
        try
        {
            using var stream = manifestEntry.Open();
            manifest = JsonSerializer.Deserialize<PackManifest>(stream, Json) ?? throw new PackFormatException("manifest.json is empty.");
        }
        catch (JsonException e)
        {
            throw new PackFormatException($"manifest.json could not be read: {e.Message}");
        }
        Validate(manifest, zip);
        return manifest;
    }

    private static void Validate(PackManifest m, ZipArchive zip)
    {
        if (m.Format > Format)
            throw new PackFormatException($"This pack uses format {m.Format}. Update Blueline Studio to open it.");
        if (m.Format < 1)
            throw new PackFormatException("manifest.json has no format number.");
        if (string.IsNullOrWhiteSpace(m.Name) || m.Id is null || !Guid.TryParse(m.Id, out _))
            throw new PackFormatException("manifest.json needs a name and an id.");
        if (m.Slug is null || !SlugPattern().IsMatch(m.Slug) || m.Slug.Length > 24)
            throw new PackFormatException($"The pack's slug '{m.Slug}' is not lowercase letters, digits and hyphens of at most 24 characters.");
        if (m.Game is null || m.Files is null || m.Saves is null || m.LeftOut is null || m.Art is null)
            throw new PackFormatException("manifest.json is missing a section.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in m.Files)
        {
            if (string.IsNullOrEmpty(file.Path) || file.Path.StartsWith('/') || file.Path.Contains('\\') || file.Path.Split('/').Contains(".."))
                throw new PackFormatException($"The pack names a game path it should not: {file.Path}");
            if (!seen.Add(file.Path))
                throw new PackFormatException($"The pack lists {file.Path} twice.");
            if (!HashPattern().IsMatch(file.Blob ?? "") || (file.Stock != null && !HashPattern().IsMatch(file.Stock)))
                throw new PackFormatException($"The pack's hash for {file.Path} is not a SHA-256.");
            if (zip.GetEntry("files/" + file.Blob) is not { } blob || blob.Length != file.Size)
                throw new PackFormatException($"The pack is missing the content for {file.Path}.");
        }
        foreach (var art in new[] { m.Art.Cover, m.Art.Icon }.Concat(m.Art.Shots ?? []))
            if (art != null && zip.GetEntry(art) == null)
                throw new PackFormatException($"The pack lists {art} but does not hold it.");
        foreach (var save in m.Saves)
        {
            if (save.Kind != "roster" || save.Folder != RosterFolder)
                throw new PackFormatException($"The pack holds a save of a kind this build does not know: {save.Kind}.");
            foreach (var (name, hash) in save.Files)
                if (zip.GetEntry($"{RosterFolder}/{name}") == null || !HashPattern().IsMatch(hash))
                    throw new PackFormatException($"The pack's roster save is missing {name}.");
        }
    }

    /// <summary>Reads one art picture, or null when the pack has none there.</summary>
    public static byte[]? ReadArt(string packPath, string? entryName)
    {
        if (entryName == null)
            return null;
        using var zip = ZipFile.OpenRead(packPath);
        ReadManifest(zip);
        return zip.GetEntry(entryName) is { } entry ? ReadAll(entry) : null;
    }

    /// <summary>
    /// Unpacks into a folder laid out as a Blueline project (project.json and files/), plus manifest.json, art/ and saves/.
    /// Every blob and save file is hashed against the manifest before the folder is moved into place.
    /// </summary>
    public static PackManifest Unpack(string packPath, string destination, IProgress<(string Stage, int Done, int Total)>? progress = null)
    {
        using var zip = ZipFile.OpenRead(packPath);
        var manifest = ReadManifest(zip);
        var temporary = destination + ".unpacking";
        if (Directory.Exists(temporary))
            Directory.Delete(temporary, true);

        var project = ModProject.Create(temporary, "pk " + manifest.Slug, manifest.Author, manifest.Description);
        var changes = new List<ProjectChange>();
        var done = 0;
        foreach (var file in manifest.Files)
        {
            var content = ReadAll(zip.GetEntry("files/" + file.Blob)!);
            if (Sha256(content) != file.Blob)
                throw new PackFormatException($"The content for {file.Path} does not match its hash, so the pack is damaged.");
            var blob = project.StoreContent(content);
            changes.Add(new ProjectChange(file.Path, blob, content.Length, file.Description, manifest.Created));
            progress?.Report(("Unpacking " + manifest.Name, ++done, manifest.Files.Count));
        }
        project.AddStored(changes, "Unpack " + manifest.Name);

        foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/') && (e.FullName.StartsWith("art/") || e.FullName.StartsWith("saves/"))))
        {
            var target = Path.Combine(temporary, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(temporary) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new PackFormatException($"The pack holds an entry it should not: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, ReadAll(entry));
        }
        if (manifest.Roster is { } roster)
            foreach (var (name, hash) in roster.Files)
                if (Sha256(File.ReadAllBytes(Path.Combine(temporary, RosterFolder, name))) != hash)
                    throw new PackFormatException($"The roster save's {name} does not match its hash, so the pack is damaged.");

        File.WriteAllText(Path.Combine(temporary, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
        if (Directory.Exists(destination))
            Directory.Delete(destination, true);
        Directory.Move(temporary, destination);
        return manifest;
    }

    /// <summary>Writes a pack. <paramref name="blobs"/> gives each distinct content by hash; art and save files are given as bytes.</summary>
    internal static void Write(string destination, PackManifest manifest, Func<string, byte[]> blob,
        IReadOnlyDictionary<string, byte[]> art, IReadOnlyDictionary<string, byte[]> saveFiles,
        IProgress<(string Stage, int Done, int Total)>? progress = null)
    {
        var temporary = destination + ".writing";
        if (File.Exists(temporary))
            File.Delete(temporary);
        try
        {
            WriteAndCheck(temporary, manifest, blob, art, saveFiles, progress);
        }
        catch
        {
            // A failed or refused pack can be gigabytes; don't leave it behind.
            try { File.Delete(temporary); } catch (IOException) { }
            throw;
        }
        File.Move(temporary, destination, overwrite: true);
    }

    private static void WriteAndCheck(string temporary, PackManifest manifest, Func<string, byte[]> blob,
        IReadOnlyDictionary<string, byte[]> art, IReadOnlyDictionary<string, byte[]> saveFiles,
        IProgress<(string Stage, int Done, int Total)>? progress)
    {
        using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] content, CompressionLevel level)
            {
                using var stream = zip.CreateEntry(name, level).Open();
                stream.Write(content);
            }
            Add("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, Json), CompressionLevel.Optimal);
            foreach (var (name, content) in art)
                Add(name, content, CompressionLevel.NoCompression);
            var done = 0;
            foreach (var hash in manifest.Files.Select(f => f.Blob).Distinct())
            {
                Add("files/" + hash, blob(hash), CompressionLevel.Optimal);
                progress?.Report(("Packing " + manifest.Name, ++done, manifest.Files.Count));
            }
            foreach (var (name, content) in saveFiles)
                Add($"{RosterFolder}/{name}", content, CompressionLevel.Optimal);
        }

        // Check what is on disk, not what was intended.
        using (var zip = ZipFile.OpenRead(temporary))
        {
            var check = ReadManifest(zip);
            foreach (var file in check.Files)
                if (Sha256(ReadAll(zip.GetEntry("files/" + file.Blob)!)) != file.Blob)
                    throw new PackFormatException($"{file.Path} read back different from what was packed.");
        }
    }

    internal static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var buffer = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        stream.CopyTo(buffer);
        if (buffer.Length != entry.Length)
            throw new PackFormatException($"{entry.FullName} is not the size the pack says.");
        return buffer.ToArray();
    }
}
