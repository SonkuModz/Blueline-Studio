namespace Blueline.Formats.Eb;

public sealed record EbAuditResult(string Archive, int Entries, int UniqueBlocks, long GapBytes, IReadOnlyList<string> Problems)
{
    public bool Clean => Problems.Count == 0;
}

/// <summary>
/// Structural checks. Sharing a block is normal in base archives (identical pairs); only partial
/// overlap means damage.
/// </summary>
public static class EbAudit
{
    private const int MaxReported = 50;

    public static EbAuditResult Check(EbArchive archive)
    {
        var problems = new List<string>();
        void Problem(string message)
        {
            if (problems.Count < MaxReported)
                problems.Add(message);
        }

        if (archive.DeclaredSize != archive.Length)
            Problem($"Header says {archive.DeclaredSize:N0} bytes, file is {archive.Length:N0}.");
        if (archive.Length % 16 != 0)
            Problem($"File length {archive.Length:N0} is not a multiple of 16.");

        var hashed = (archive.Flags & EbArchive.FlagHashedRows) != 0;
        ulong previous = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Offset + entry.SpanOnDisk > archive.Length)
                Problem($"{entry.Path} ends past the end of the file.");
            if (!hashed)
                continue;
            if (entry.Hash != EbPathHash.Compute(entry.Path))
                Problem($"{entry.Path} has the wrong path hash; the game could not find it.");
            if (entry.Index > 0 && entry.Hash < previous)
                Problem($"{entry.Path} is out of hash order; lookups may fail.");
            previous = entry.Hash;
        }

        // Empty entries share the offset of whatever follows them; they occupy nothing, so they cannot overlap.
        var spans = archive.Entries.Where(e => e.SpanOnDisk > 0).Select(e => (Start: e.Offset, End: e.Offset + e.SpanOnDisk)).Distinct().OrderBy(s => s.Start).ToList();
        long gaps = 0;
        for (var i = 1; i < spans.Count; i++)
        {
            if (spans[i].Start < spans[i - 1].End)
                Problem($"Data ranges overlap at offset 0x{spans[i].Start:X}.");
            else
                gaps += spans[i].Start - spans[i - 1].End;
        }

        return new EbAuditResult(archive.Name, archive.Entries.Count, spans.Count, gaps, problems);
    }
}
