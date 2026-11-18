using System.Buffers.Binary;
using Blueline.Core;
using Blueline.Formats.Eb;

namespace Blueline.Tests;

/// <summary>The archive writer against the layout every stock NHL 19 archive has.</summary>
[TestClass]
public sealed class EbWriterTests
{
    private string _temp = null!;

    [TestInitialize]
    public void Setup() => _temp = Directory.CreateTempSubdirectory("blueline-ebwriter-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_temp, true);

    [TestMethod]
    public void Write_PutsAZeroBytePerFileBetweenTheRowsAndTheNames()
    {
        // Five files: before the fix, files three to five read letters of the name table as their flag byte and drew black.
        var path = Path.Combine(_temp, "patch_inxp_99_test.big");
        var items = Enumerable.Range(0, 5).Select(i => new EbWriteItem($"nhl/data/kettle/rendering/stadiums/common/board_{i}_diff.gnf", () => new byte[100 + i])).ToList();
        EbWriter.Write(path, items);

        var bytes = File.ReadAllBytes(path);
        var nameOffset = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(0x0C));
        var nameSize = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(0x10));
        var table = EbArchive.FlagTableOffset(5, 20);
        Assert.AreEqual(160, table);
        Assert.AreEqual(176, nameOffset, "Names start after the table, on a 16-byte boundary, as in stock archives.");
        Assert.AreEqual(0, nameSize % 16, "Stock archives round the name table up to 16 bytes.");
        Assert.IsTrue(bytes.AsSpan(table, nameOffset - table).IndexOfAnyExcept((byte)0) < 0);

        using var archive = EbArchive.Open(path);
        CollectionAssert.AreEqual(new byte[5], archive.ReadFileFlags());
        var audit = EbAudit.Check(archive);
        Assert.IsTrue(audit.GameReady, string.Join("; ", audit.Problems.Append(audit.GameProblem ?? "")));
        Assert.AreEqual(items.Count, archive.Entries.Count);
        foreach (var entry in archive.Entries)
            Assert.AreEqual(100 + int.Parse(entry.Path.Split('_')[^2]), archive.ReadContent(entry).Length);
    }

    [TestMethod]
    public void Audit_FlagsAnArchiveWithNoTable()
    {
        // The layout Blueline wrote before 0.10.1: names straight after the rows.
        var path = Path.Combine(_temp, "old.big");
        EbWriter.Write(path, [new EbWriteItem("nhl/a/one.txt", () => [1]), new EbWriteItem("nhl/a/two.txt", () => [2]), new EbWriteItem("nhl/a/three.txt", () => [3])]);
        var bytes = File.ReadAllBytes(path);
        var nameOffset = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(0x0C));
        var nameSize = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(0x10));
        var table = EbArchive.FlagTableOffset(3, 20);
        bytes.AsSpan(nameOffset, nameSize).CopyTo(bytes.AsSpan(table));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0x0C), table);
        File.WriteAllBytes(path, bytes);

        using var archive = EbArchive.Open(path);
        Assert.IsNull(archive.ReadFileFlags());
        var audit = EbAudit.Check(archive);
        Assert.IsTrue(audit.Clean, "It still reads correctly, so merging from it works.");
        Assert.IsFalse(audit.GameReady);
        Assert.AreEqual(3, archive.Entries.Count);
    }

    [TestMethod]
    public void BootBig_RebuildsToTheSameTablesAndContent()
    {
        // boot.big has no patch archive route (the eboot mounts databigs/boot.big by name), so a changed layout or nhluidata.swf
        // has to ship as a whole new boot.big. Rebuilt unchanged, it must match the original's tables and every file's bytes.
        string? game = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && game == null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "NHL 19", "CUSA11126", "eboot.bin")))
                game = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
        if (game == null)
            Assert.Inconclusive("The game is not here.");

        var mods = Path.Combine(game + "-mods", "databigs", "boot.big");
        var source = File.Exists(mods) ? mods : Path.Combine(game, "databigs", "boot.big");
        var rebuilt = Path.Combine(_temp, "boot.big");
        using var original = EbArchive.Open(source);
        Assert.IsTrue(original.Entries.All(e => e.CompressedSize == 0), "the writer stores sizes only; a compressed-size row would need it too");
        EbWriter.Write(rebuilt, original.Entries.Select(e => new EbWriteItem(e.Path, () => original.ReadRaw(e))).ToList());

        // The stock data is not in row order, so offsets differ; everything else in the tables must match.
        var a = File.ReadAllBytes(source);
        var b = File.ReadAllBytes(rebuilt);
        var dataStart = (int)original.Entries.Min(e => e.Offset);
        using var copy = EbArchive.Open(rebuilt);
        Assert.AreEqual(dataStart, (int)copy.Entries.Min(e => e.Offset), "data starts at the same place");
        Assert.IsTrue(a.AsSpan(0, 0x18).SequenceEqual(b.AsSpan(0, 0x18)), "header up to the size");
        Assert.IsTrue(a.AsSpan(0x20, 0x10).SequenceEqual(b.AsSpan(0x20, 0x10)), "rest of the header");
        var rowsEnd = EbArchive.HeaderSize + original.Entries.Count * 20;
        for (var i = 0; i < original.Entries.Count; i++)
            Assert.IsTrue(a.AsSpan(EbArchive.HeaderSize + i * 20 + 4, 16).SequenceEqual(b.AsSpan(EbArchive.HeaderSize + i * 20 + 4, 16)), $"row {i} past its offset");
        var at = rowsEnd + a.AsSpan(rowsEnd, dataStart - rowsEnd).CommonPrefixLength(b.AsSpan(rowsEnd, dataStart - rowsEnd));
        Assert.AreEqual(dataStart, at, $"flag table and names differ at 0x{at:X} (names at 0x{original.NameOffset:X}): "
            + $"{Convert.ToHexString(a.AsSpan(at, 24))} against {Convert.ToHexString(b.AsSpan(at, 24))}; "
            + $"{Enumerable.Range(rowsEnd, dataStart - rowsEnd).Count(i => a[i] != b[i])} bytes differ");
        Assert.AreEqual(original.Entries.Select(e => e.Offset).Distinct().Count(), copy.Entries.Select(e => e.Offset).Distinct().Count(), "same files shared");
        foreach (var (x, y) in original.Entries.Zip(copy.Entries))
            Assert.IsTrue(original.ReadRaw(x).AsSpan().SequenceEqual(copy.ReadRaw(y)), x.Path);
        Assert.IsTrue(b.Length <= a.Length, $"no bigger than the original ({b.Length:N0} against {a.Length:N0})");
    }

    [TestMethod]
    public void EveryStockArchive_HasTheTable()
    {
        string? game = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && game == null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "NHL 19", "CUSA11126", "eboot.bin")))
                game = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
        if (game == null)
            Assert.Inconclusive("The game is not here.");

        var install = GameInstall.Open(game, useModsFolder: false);
        foreach (var file in Directory.GetFiles(install.Databigs, "*.big").Where(f => !GameInstall.IsModArchive(Path.GetFileName(f))))
        {
            using var archive = EbArchive.Open(file);
            Assert.IsNull(EbAudit.Check(archive).GameProblem, archive.Name);
        }
    }
}
