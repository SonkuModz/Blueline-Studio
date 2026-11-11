using System.IO.Compression;
using System.Security.Cryptography;
using Blueline.Formats.Eb;

namespace Blueline.Core;

public sealed record BuiltArchive(string Path, string FileName, int Files, long Size, string Sha256);

public sealed record BuildResult(IReadOnlyList<BuiltArchive> Archives, IReadOnlyList<string> Warnings, TimeSpan Elapsed);

public sealed class BuildFailedException(string message) : Exception(message);

/// <summary>
/// Turns a project into overlay archives. Each file goes into the family that already wins for it, because a
/// path in patch_inxp_99 would lose to the same path in any game archive, which mount later.
/// Every archive is reopened and compared with the project before the build reports success.
/// </summary>
public static class OverlayBuilder
{
    public static BuildResult Build(ModProject project, VirtualFileSystem vfs, string outputFolder,
        IProgress<(string Stage, int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        if (project.ChangeCount == 0)
            throw new BuildFailedException("The project has no changes to build.");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var warnings = new List<string>();
        var inxp = new List<ProjectChange>();
        var game = new List<ProjectChange>();

        foreach (var change in project.Changes.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            var original = vfs.Find(change.Path);
            switch (original?.Family)
            {
                case ArchiveFamily.Game:
                    game.Add(change);
                    break;
                case ArchiveFamily.Inxp:
                    inxp.Add(change);
                    break;
                case null:
                    warnings.Add($"{change.Path} is a new path. It installs in the inxp layer, and the game must request this exact name for it to load.");
                    inxp.Add(change);
                    break;
                case ArchiveFamily.Loose:
                    throw new BuildFailedException($"{change.Path} is a loose file, not an archive entry. Replacing loose files is not supported, because their priority against archives has not been verified.");
                default:
                    warnings.Add($"{change.Path} comes from {original.SourceName}, which the game may read before overlays mount, so it may not take effect.");
                    inxp.Add(change);
                    break;
            }
        }

        var results = new List<BuiltArchive>();
        Directory.CreateDirectory(outputFolder);
        foreach (var stale in Directory.GetFiles(outputFolder, "patch_*_99_*.big"))
            File.Delete(stale);
        if (inxp.Count > 0)
            results.Add(WriteArchive(project, vfs, inxp, Path.Combine(outputFolder, $"patch_inxp_99_{project.Slug}.big"), progress, cancel));
        if (game.Count > 0)
            results.Add(WriteArchive(project, vfs, game, Path.Combine(outputFolder, $"patch_game_99_{project.Slug}.big"), progress, cancel));

        return new BuildResult(results, warnings, clock.Elapsed);
    }

    private static BuiltArchive WriteArchive(ModProject project, VirtualFileSystem vfs, List<ProjectChange> changes, string destination,
        IProgress<(string Stage, int Done, int Total)>? progress, CancellationToken cancel)
    {
        var name = Path.GetFileName(destination);
        var items = changes.Select(change => new EbWriteItem(change.Path, () => Stored(project, vfs, change))).ToList();
        var writing = progress == null ? null : new InlineProgress<(int Done, int Total)>(p => progress.Report(($"Writing {name}", p.Done, p.Total)));
        EbWriter.Write(destination, items, writing, cancel);

        // Validate what is on disk, not what was intended.
        using (var archive = EbArchive.Open(destination))
        {
            var audit = EbAudit.Check(archive);
            if (!audit.Clean)
                throw new BuildFailedException($"{name} failed its structure check: {string.Join("; ", audit.Problems.Take(5))}");
            if (archive.Entries.Count != changes.Count)
                throw new BuildFailedException($"{name} holds {archive.Entries.Count} files, expected {changes.Count}.");

            var byPath = archive.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
            for (var i = 0; i < changes.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var change = changes[i];
                if (!byPath.TryGetValue(change.Path, out var entry))
                    throw new BuildFailedException($"{name} is missing {change.Path}.");
                if (!archive.ReadContent(entry).AsSpan().SequenceEqual(project.ReadBlob(change)))
                    throw new BuildFailedException($"{change.Path} read back different from the project's copy.");
                progress?.Report(($"Verifying {name}", i + 1, changes.Count));
            }
        }

        using var stream = File.OpenRead(destination);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return new BuiltArchive(destination, name, changes.Count, new FileInfo(destination).Length, hash);
    }

    /// <summary>The bytes as stored: wrapped only if the original was, since readers differ per path.</summary>
    private static byte[] Stored(ModProject project, VirtualFileSystem vfs, ProjectChange change)
    {
        var content = project.ReadBlob(change);
        var original = vfs.Find(change.Path);
        return original != null && vfs.IsWrapped(original)
            ? Chunkzip.Wrap(content, CompressionLevel.Optimal)
            : content;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
