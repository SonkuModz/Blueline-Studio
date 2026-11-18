using Blueline.Formats.Eb;

namespace Blueline.Core;

/// <summary>
/// A whole new boot.big with a project's edits in it. The eboot mounts databigs/boot.big by name, with no patch archive over
/// it (it globs inxp*.big and game*.big with patch_inxp and patch_game, but not boot), so layouts, Lua, definitions and
/// nhluidata.swf can only change this way. Seen in game 2026-11-09: a patch_inxp_99 copy of the score clock layout was ignored.
/// The source is the boot.big the game would read: the mods folder's when there is one (Puck Peak ships its own), else the
/// install's. Every other file is copied as stored.
/// </summary>
public static class BootArchive
{
    public const string FileName = "boot.big";

    public sealed record Result(string Source, string Destination, IReadOnlyList<string> Replaced, int Files);

    /// <summary>The project's changes to files boot.big holds.</summary>
    public static IReadOnlyList<ProjectChange> ChangesIn(ModProject project, GameInstall game)
    {
        using var archive = EbArchive.Open(game.ArchivePath(FileName));
        var paths = archive.Entries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        return project.Changes.Where(c => paths.Contains(c.Path)).OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
    }

    public static Result Build(ModProject project, GameInstall game, string destination)
    {
        var source = game.ArchivePath(FileName);
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Write the new boot.big somewhere else than the one it is built from.", nameof(destination));
        using var archive = EbArchive.Open(source);
        var changes = ChangesIn(project, game).ToDictionary(c => c.Path, StringComparer.Ordinal);
        if (changes.Count == 0)
            throw new InvalidOperationException("The project changes nothing boot.big holds.");

        // Edited files are stored plain: a third of stock boot.big is (2,285 of 6,886 files), so the game reads both, while
        // Blueline's own chunkzip wrapping has not been seen in game. Stock files are copied as stored.
        EbWriter.Write(destination, archive.Entries.Select(e => new EbWriteItem(e.Path,
            changes.TryGetValue(e.Path, out var change) ? () => project.ReadBlob(change) : () => archive.ReadRaw(e))).ToList());

        using var written = EbArchive.Open(destination);
        var audit = EbAudit.Check(written);
        if (!audit.GameReady)
            throw new InvalidDataException("The new boot.big failed its check: " + string.Join("; ", audit.Problems.Append(audit.GameProblem ?? "")));
        foreach (var entry in written.Entries.Where(e => changes.ContainsKey(e.Path)))
            if (!written.ReadContent(entry).AsSpan().SequenceEqual(project.ReadBlob(changes[entry.Path])))
                throw new InvalidDataException($"{entry.Path} did not read back as written.");
        return new Result(source, destination, changes.Keys.ToList(), written.Entries.Count);
    }
}
