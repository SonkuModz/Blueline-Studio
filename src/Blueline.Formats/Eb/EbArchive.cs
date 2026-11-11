using System.Buffers.Binary;
using System.Text;

namespace Blueline.Formats.Eb;

public sealed class EbFormatException(string message) : Exception(message);

/// <summary>One file table row joined to its name.</summary>
public sealed record EbEntry(
    int Index,
    string Path,
    long Offset,
    uint CompressedSize,
    uint Size,
    ulong Hash)
{
    /// <summary>Bytes the entry occupies in the archive.</summary>
    public long SpanOnDisk => CompressedSize != 0 ? CompressedSize : Size;
}

/// <summary>
/// Read-only view of an EB archive. Only the tables are loaded; entry data is read on demand, so a
/// 13 GB archive opens in milliseconds. Thread-safe: reads use positional I/O.
/// </summary>
public sealed class EbArchive : IDisposable
{
    public const uint FlagLargeBlocks = 0x200;
    public const uint FlagHashedRows = 0x10000;
    public const int HeaderSize = 0x30;

    private readonly FileStream _stream;

    private EbArchive(string path, FileStream stream, uint flags, long declaredSize,
        int fileRecordLength, int folderRecordLength, IReadOnlyList<string> folders, IReadOnlyList<EbEntry> entries)
    {
        FilePath = path;
        _stream = stream;
        Flags = flags;
        DeclaredSize = declaredSize;
        FileRecordLength = fileRecordLength;
        FolderRecordLength = folderRecordLength;
        Folders = folders;
        Entries = entries;
    }

    public string FilePath { get; }
    public string Name => System.IO.Path.GetFileName(FilePath);
    public uint Flags { get; }
    public long DeclaredSize { get; }
    public int FileRecordLength { get; }
    public int FolderRecordLength { get; }
    public IReadOnlyList<string> Folders { get; }
    public IReadOnlyList<EbEntry> Entries { get; }
    public long Length => _stream.Length;

    public static EbArchive Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        try
        {
            return Parse(path, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static EbArchive Parse(string path, FileStream stream)
    {
        var header = new byte[HeaderSize];
        if (stream.Length < HeaderSize || RandomAccess.Read(stream.SafeFileHandle, header, 0) != HeaderSize)
            throw new EbFormatException($"{path}: too short to be an EB archive ({stream.Length} bytes).");
        if (header[0] != 'E' || header[1] != 'B')
            throw new EbFormatException($"{path}: missing the EB signature.");

        var version = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        if (version != 3)
            throw new EbFormatException($"{path}: version {version}; only version 3 has been verified.");

        var fileCount = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
        var flags = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        var nameOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x0C));
        var nameSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x10));
        int fileRecord = header[0x14], folderRecord = header[0x15];
        var folderCount = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0x16));
        var declared = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0x18));

        if (fileCount < 0 || fileRecord < 3 || nameOffset + (long)nameSize > stream.Length)
            throw new EbFormatException($"{path}: header fields are out of range.");

        var rowSize = (flags & FlagHashedRows) != 0 ? 20 : 16;
        var blockSize = (flags & FlagLargeBlocks) != 0 ? 0x40L : 0x10L;

        var rows = new byte[fileCount * rowSize];
        RandomAccess.Read(stream.SafeFileHandle, rows, HeaderSize);
        var names = new byte[nameSize];
        RandomAccess.Read(stream.SafeFileHandle, names, nameOffset);

        var folderStart = (fileCount * fileRecord + 15) & ~15;
        var folders = new string[folderCount];
        for (var i = 0; i < folderCount; i++)
            folders[i] = ReadText(names.AsSpan(folderStart + i * folderRecord, folderRecord));

        var entries = new EbEntry[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            var row = rows.AsSpan(i * rowSize, rowSize);
            var record = names.AsSpan(i * fileRecord, fileRecord);
            var folderIndex = BinaryPrimitives.ReadUInt16BigEndian(record);
            if (folderIndex >= folderCount)
                throw new EbFormatException($"{path}: entry {i} names folder {folderIndex} of {folderCount}.");

            var name = ReadText(record[2..]);
            var hash = rowSize == 20
                ? (ulong)BinaryPrimitives.ReadUInt32BigEndian(row[12..]) << 32 | BinaryPrimitives.ReadUInt32BigEndian(row[16..])
                : BinaryPrimitives.ReadUInt32BigEndian(row[12..]);

            entries[i] = new EbEntry(i, folders[folderIndex] + "/" + name,
                BinaryPrimitives.ReadUInt32BigEndian(row) * blockSize,
                BinaryPrimitives.ReadUInt32BigEndian(row[4..]),
                BinaryPrimitives.ReadUInt32BigEndian(row[8..]),
                hash);
        }

        return new EbArchive(path, stream, flags, declared, fileRecord, folderRecord, folders, entries);
    }

    private static string ReadText(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
    }

    /// <summary>The entry's bytes as stored, still wrapped if it is chunkzip.</summary>
    public byte[] ReadRaw(EbEntry entry)
    {
        var buffer = new byte[entry.SpanOnDisk];
        ReadRaw(entry, 0, buffer);
        return buffer;
    }

    public int ReadRaw(EbEntry entry, long offset, Span<byte> destination)
    {
        var available = Math.Max(0, entry.SpanOnDisk - offset);
        var wanted = (int)Math.Min(available, destination.Length);
        var total = 0;
        while (total < wanted)
        {
            var read = RandomAccess.Read(_stream.SafeFileHandle, destination[total..wanted], entry.Offset + offset + total);
            if (read == 0)
                throw new EbFormatException($"{Name}: {entry.Path} runs past the end of the file.");
            total += read;
        }
        return total;
    }

    /// <summary>The entry's real content, with any chunkzip wrapper removed.</summary>
    public byte[] ReadContent(EbEntry entry) => Chunkzip.Unwrap(ReadRaw(entry));

    /// <summary>The first bytes of an entry's content, without decoding the whole of it.</summary>
    public byte[] ReadContentHead(EbEntry entry, int length)
    {
        var raw = new byte[Math.Min(entry.SpanOnDisk, 0x10000)];
        ReadRaw(entry, 0, raw);
        return Chunkzip.IsWrapped(raw) ? Chunkzip.UnwrapHead(raw, length) : raw[..Math.Min(length, raw.Length)];
    }

    public void Dispose() => _stream.Dispose();
}
