using System.Security.Cryptography;
using System.Text.Json;
using Blueline.Formats.Eb;

namespace Blueline.Core;

public sealed record PuckPeakImportResult(IReadOnlyList<ProjectChange> Changes, long Bytes, IReadOnlyList<string> Warnings)
{
    public int Files => Changes.Count;
}

/// <summary>
/// The mod Puck Peak Overtime installs beside the game, in `CUSA11126-mods`. Its `overtime-patch.json` lists every file it
/// changed in each archive it wrote, which is what turns it into a Blueline project: only those files are taken, read from the
/// mods folder's archives, with later archives winning as they do in the game.
/// </summary>
public sealed class PuckPeakMod
{
    public const string ManifestName = "overtime-patch.json";

    private PuckPeakMod(string folder, string applied, IReadOnlyDictionary<string, IReadOnlyList<string>> entries)
    {
        Folder = folder;
        Applied = applied;
        Entries = entries;
    }

    /// <summary>The mods folder that holds the manifest and its `databigs`.</summary>
    public string Folder { get; }

    /// <summary>When Overtime applied it, as the manifest says.</summary>
    public string Applied { get; }

    /// <summary>Archive name (as in `databigs`) to the paths Overtime wrote into it.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Entries { get; }

    public int FileCount => Entries.Values.SelectMany(e => e).Distinct(StringComparer.Ordinal).Count();

    /// <summary>The Overtime mod beside this install, or null if there is none.</summary>
    public static PuckPeakMod? Find(GameInstall install)
    {
        var folder = install.ModsFolder ?? install.Root + "-mods";
        return File.Exists(Path.Combine(folder, ManifestName)) ? Open(folder) : null;
    }

    public static PuckPeakMod Open(string folder)
    {
        var manifest = Path.Combine(folder, ManifestName);
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifest));
        var root = document.RootElement;
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{ManifestName} lists no files.");
        var entries = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.EnumerateObject())
        {
            if (!file.Value.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array)
                continue;
            var name = Path.GetFileName(file.Name.Replace('\\', '/'));
            entries[name] = list.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList();
        }
        var applied = root.TryGetProperty("applied", out var when) ? when.GetString() ?? "" : "";
        return new PuckPeakMod(folder, applied, entries);
    }

    /// <summary>
    /// Copies every file Overtime changed into the project's store and returns the changes for
    /// <see cref="ModProject.AddStored"/>, which the caller applies as one undo step. Files are stored one at a time, so even
    /// the texture packs (several GB) never sit in memory together.
    /// </summary>
    public PuckPeakImportResult Copy(ModProject project, IProgress<(string Stage, int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        var databigs = Path.Combine(Folder, "databigs");
        var order = GameInstall.MountOrder(Entries.Keys);
        var winner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var archive in order)
            foreach (var path in Entries[archive])
                winner[path] = archive;

        var warnings = new List<string>();
        var stored = new List<ProjectChange>(winner.Count);
        long bytes = 0;
        var done = 0;
        foreach (var group in winner.GroupBy(w => w.Value, w => w.Key))
        {
            var file = Path.Combine(databigs, group.Key);
            if (!File.Exists(file))
                throw new InvalidDataException($"{ManifestName} lists {group.Key}, but the mods folder does not have it.");
            using var archive = EbArchive.Open(file);
            var byPath = archive.Entries.GroupBy(e => e.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            var missing = 0;
            foreach (var path in group.OrderBy(p => p, StringComparer.Ordinal))
            {
                cancel.ThrowIfCancellationRequested();
                if (!byPath.TryGetValue(path, out var entry))
                {
                    missing++;
                    continue;
                }
                var content = archive.ReadContent(entry);
                var hash = project.StoreContent(content);
                if (hash != Convert.ToHexStringLower(SHA256.HashData(content)))
                    throw new IOException($"{path} was stored differently from what was read.");
                stored.Add(new ProjectChange(path, hash, content.Length, $"Puck Peak Overtime, from {group.Key}", DateTimeOffset.Now));
                bytes += content.Length;
                progress?.Report(("Copying from Puck Peak Overtime", ++done, winner.Count));
            }
            if (missing > 0)
                warnings.Add($"{group.Key} is missing {missing} of the files the manifest lists for it; they were skipped.");
            if (VirtualFileSystem.FamilyOf(group.Key) == ArchiveFamily.Boot)
                warnings.Add($"{group.Count()} files came from {group.Key}. The game reads boot files before overlays mount, so a Blueline build of them may not take effect; Overtime changes {group.Key} itself.");
        }

        if (stored.Any(c => c.Path.EndsWith(".db", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("Before installing a build of this project, remove Overtime's mods folder or launch the plain game, so the game does not load both copies.");
        return new PuckPeakImportResult(stored, bytes, warnings);
    }
}
