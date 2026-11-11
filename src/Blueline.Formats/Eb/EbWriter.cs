using System.Buffers.Binary;
using System.Text;

namespace Blueline.Formats.Eb;

/// <summary>A file to put in a new archive. <see cref="Content"/> supplies the bytes as they are stored.</summary>
public sealed record EbWriteItem(string Path, Func<byte[]> Content);

/// <summary>
/// Writes a complete new EB archive in the shape the NHL 19 databigs use: flags 0x00110400, 20-byte
/// hashed rows sorted by path hash, 16-byte aligned data, file length a multiple of 16.
/// Writes to a temporary file and moves it into place, so a failure never leaves a partial archive.
/// </summary>
public static class EbWriter
{
    public const uint StandardFlags = 0x00110400;
    private const int RowSize = 20;
    private const int Block = 16;
    private const int MaxRecordLength = 255;

    public static void Write(string destination, IReadOnlyCollection<EbWriteItem> items,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        if (items.Count == 0)
            throw new ArgumentException("An archive needs at least one file.", nameof(items));

        var seen = new HashSet<ulong>();
        var sorted = new List<(EbWriteItem Item, ulong Hash, string Folder, string Name)>(items.Count);
        foreach (var item in items)
        {
            var path = ValidatePath(item.Path);
            var hash = EbPathHash.Compute(path);
            if (!seen.Add(hash))
                throw new InvalidDataException($"Duplicate or colliding path: {path}");
            var cut = path.LastIndexOf('/');
            sorted.Add((item, hash, path[..cut], path[(cut + 1)..]));
        }
        sorted.Sort((a, b) => a.Hash.CompareTo(b.Hash));

        var folders = sorted.Select(s => s.Folder).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var folderIndex = folders.Select((f, i) => (f, i)).ToDictionary(x => x.f, x => x.i, StringComparer.Ordinal);
        if (folders.Count > ushort.MaxValue)
            throw new InvalidDataException("Too many folders for one archive.");

        var fileRecord = sorted.Max(s => s.Name.Length) + 3;
        var folderRecord = folders.Max(f => f.Length) + 1;
        if (fileRecord > MaxRecordLength || folderRecord > MaxRecordLength)
            throw new InvalidDataException("A path is too long to fit in the archive's name records.");

        var nameOffset = AlignUp(EbArchive.HeaderSize + sorted.Count * RowSize);
        var folderStart = AlignUp(sorted.Count * fileRecord);
        var nameSize = folderStart + folders.Count * folderRecord;
        var dataStart = AlignUp(nameOffset + nameSize);

        var names = new byte[nameSize];
        for (var i = 0; i < sorted.Count; i++)
        {
            var record = names.AsSpan(i * fileRecord, fileRecord);
            BinaryPrimitives.WriteUInt16BigEndian(record, (ushort)folderIndex[sorted[i].Folder]);
            Encoding.Latin1.GetBytes(sorted[i].Name, record[2..]);
        }
        for (var i = 0; i < folders.Count; i++)
            Encoding.Latin1.GetBytes(folders[i], names.AsSpan(folderStart + i * folderRecord, folderRecord));

        var temporary = destination + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        try
        {
            var rows = new byte[sorted.Count * RowSize];
            long length;
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                output.Position = dataStart;
                for (var i = 0; i < sorted.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var content = sorted[i].Item.Content();
                    var offset = output.Position;
                    output.Write(content);
                    Pad(output);

                    var blocks = offset / Block;
                    if (blocks > uint.MaxValue)
                        throw new InvalidDataException("Archive exceeds the format's offset range.");

                    var row = rows.AsSpan(i * RowSize, RowSize);
                    BinaryPrimitives.WriteUInt32BigEndian(row, (uint)blocks);
                    BinaryPrimitives.WriteUInt32BigEndian(row[8..], (uint)content.Length);
                    BinaryPrimitives.WriteUInt32BigEndian(row[12..], (uint)(sorted[i].Hash >> 32));
                    BinaryPrimitives.WriteUInt32BigEndian(row[16..], (uint)sorted[i].Hash);
                    progress?.Report((i + 1, sorted.Count));
                }
                length = output.Length;

                var header = new byte[EbArchive.HeaderSize];
                header[0] = (byte)'E';
                header[1] = (byte)'B';
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 3);
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), sorted.Count);
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), StandardFlags);
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x0C), nameOffset);
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x10), nameSize);
                header[0x14] = (byte)fileRecord;
                header[0x15] = (byte)folderRecord;
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0x16), (ushort)folders.Count);
                BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x18), (ulong)length);

                output.Position = 0;
                output.Write(header);
                output.Write(rows);
                output.Position = nameOffset;
                output.Write(names);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/') || path.EndsWith('/') || !path.Contains('/'))
            throw new InvalidDataException($"Not a valid archive path: '{path}'");
        if (path.Any(c => c > 0x7E || c < 0x20))
            throw new InvalidDataException($"Path contains characters the archive tables cannot hold: '{path}'");
        return path;
    }

    private static int AlignUp(int value) => (value + Block - 1) & ~(Block - 1);

    private static void Pad(Stream output)
    {
        var over = (int)(output.Position % Block);
        if (over != 0)
            output.Write(new byte[Block - over]);
    }
}
