using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using K4os.Compression.LZ4;

namespace Blueline.Formats.Tdb;

/// <summary>
/// An NHL 19 roster save folder (RosterCustom0 and the like): DATA, INDEX, NAME and sce_sys.
/// <para>
/// DATA is a run of records, each a u64 length, an 8-byte tag and the payload. The records are the backup flag (JSON), a
/// version block, the file info JSON (which holds CRCValue), and last the roster. The roster record is 0x01, then an LZ4 frame,
/// then a u32 of the unpacked size. The game needs that u32: without it, an edited save hung on "CHECKING" and an unedited
/// repack crashed after the title screen. The frame unpacks to the TDB and an 18-byte trailer whose u64 at offset 8 is the TDB's size.
/// </para>
/// <para>INDEX entry 0 holds, at 0x28, 0x30 and 0x38, the roster record's length plus 16, its offset, and the size of DATA.</para>
/// </summary>
public sealed partial class RosterSave
{
    private const int BlockSize = 64 * 1024;
    private const int TrailerLength = 18;
    private static readonly byte[] FrameHeader = [0x04, 0x22, 0x4D, 0x18, 0x64, 0x40, 0xA7];

    private readonly byte[] _data;
    private readonly byte[] _index;
    private readonly int _rosterAt;
    private readonly byte[] _trailer;

    private RosterSave(string folder, byte[] data, byte[] index, int rosterAt, byte[] database, byte[] trailer)
    {
        Folder = folder;
        _data = data;
        _index = index;
        _rosterAt = rosterAt;
        Database = database;
        _trailer = trailer;
    }

    public string Folder { get; }

    /// <summary>The roster database as stored. Parse a clone to edit it.</summary>
    public byte[] Database { get; }

    public string CrcValue => CrcPattern().Match(Encoding.UTF8.GetString(_data)).Groups[1].Value;

    public static bool IsRosterSave(string folder) =>
        File.Exists(Path.Combine(folder, "DATA")) && File.Exists(Path.Combine(folder, "INDEX"));

    public static RosterSave Read(string folder)
    {
        var data = File.ReadAllBytes(Path.Combine(folder, "DATA"));
        var index = File.ReadAllBytes(Path.Combine(folder, "INDEX"));

        var records = new List<int>();
        var at = 0;
        while (at < data.Length)
        {
            if (at + 16 > data.Length)
                throw new InvalidDataException("DATA ends inside a record header.");
            var length = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(at));
            if (length < 0 || at + 16 + length > data.Length)
                throw new InvalidDataException("A DATA record runs past the end of the file.");
            records.Add(at);
            at += 16 + (int)length;
        }
        if (records.Count < 2)
            throw new InvalidDataException("DATA has too few records to be a roster save.");
        var rosterAt = records[^1];
        var payload = data.AsSpan(rosterAt + 16, (int)BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(rosterAt)));
        if (payload.Length < 1 + FrameHeader.Length + 8 || payload[0] != 1 || !payload.Slice(1, FrameHeader.Length).SequenceEqual(FrameHeader))
            throw new InvalidDataException("The last DATA record is not an LZ4 roster frame of the kind NHL 19 writes.");

        if (index.Length < 0x40 || BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(0x28)) != 16 + payload.Length
            || BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(0x30)) != rosterAt
            || BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(0x38)) != data.Length)
            throw new InvalidDataException("INDEX does not describe DATA's roster record.");

        var unpacked = Unpack(payload[1..^4]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(payload[^4..]) != unpacked.Length)
            throw new InvalidDataException("The roster record's size does not match what its frame unpacks to.");
        if (unpacked.Length < 0x20 + TrailerLength)
            throw new InvalidDataException("The roster is too small.");
        var size = BinaryPrimitives.ReadInt32LittleEndian(unpacked.AsSpan(8));
        if (size + TrailerLength != unpacked.Length || BinaryPrimitives.ReadInt64LittleEndian(unpacked.AsSpan(size + 8)) != size)
            throw new InvalidDataException("The roster's trailer does not match its database.");
        return new RosterSave(folder, data, index, rosterAt, unpacked[..size], unpacked[size..]);
    }

    /// <summary>
    /// Writes this save with another roster database into a folder (a copy of this save's folder, with DATA and INDEX rebuilt).
    /// The database's own checksums must already be repaired; CRCValue is recomputed here from the meta.
    /// </summary>
    public void WriteTo(string folder, byte[] database, string metaXml)
    {
        var file = TdbFile.Parse(database);
        if (file.VerifyCrcs().FirstOrDefault(c => !c.Matches) is { } bad)
            throw new InvalidDataException($"The roster database checksum {bad.Name} does not verify.");

        var trailer = (byte[])_trailer.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(trailer.AsSpan(8), database.Length);
        var unpacked = new byte[database.Length + trailer.Length];
        database.CopyTo(unpacked, 0);
        trailer.CopyTo(unpacked, database.Length);

        var frame = Pack(unpacked);
        var payload = new byte[1 + frame.Length + 4];
        payload[0] = 1;
        frame.CopyTo(payload, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(payload.Length - 4), (uint)unpacked.Length);

        var head = (byte[])_data[.._rosterAt].Clone();
        var oldCrc = CrcPattern().Match(Encoding.ASCII.GetString(head));
        if (!oldCrc.Success || oldCrc.Groups[1].Length != RosterCrc.Length)
            throw new InvalidDataException("The save's file info has no CRCValue of the expected length.");
        Encoding.ASCII.GetBytes(RosterCrc.Compute(file, metaXml)).CopyTo(head, oldCrc.Groups[1].Index);

        var data = new byte[head.Length + 16 + payload.Length];
        head.CopyTo(data, 0);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(head.Length), payload.Length);
        _data.AsSpan(_rosterAt + 8, 8).CopyTo(data.AsSpan(head.Length + 8));
        payload.CopyTo(data, head.Length + 16);

        var index = (byte[])_index.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(0x28), 16 + payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(0x38), data.Length);

        if (!Path.GetFullPath(folder).Equals(Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase))
            CopyFolder(Folder, folder);
        File.WriteAllBytes(Path.Combine(folder, "DATA"), data);
        File.WriteAllBytes(Path.Combine(folder, "INDEX"), index);
    }

    public static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(from))
            CopyFolder(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    [GeneratedRegex("\"CRCValue\":\"([^\"]*)\"")]
    private static partial Regex CrcPattern();

    // ---- LZ4 frame: independent 64 KB blocks, content checksum, no content size, as the game writes it

    private static byte[] Unpack(ReadOnlySpan<byte> frame)
    {
        var output = new MemoryStream();
        var block = new byte[BlockSize];
        var at = FrameHeader.Length;
        while (true)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(frame[at..]);
            at += 4;
            if (size == 0)
                break;
            var raw = (size & 0x80000000) != 0;
            var length = (int)(size & 0x7FFFFFFF);
            if (length > BlockSize || at + length > frame.Length)
                throw new InvalidDataException("An LZ4 block is larger than the frame allows.");
            if (raw)
                output.Write(frame.Slice(at, length));
            else
            {
                var decoded = LZ4Codec.Decode(frame.Slice(at, length), block);
                if (decoded < 0)
                    throw new InvalidDataException("An LZ4 block of the roster is damaged.");
                output.Write(block, 0, decoded);
            }
            at += length;
        }
        var content = output.ToArray();
        if (at + 4 != frame.Length || BinaryPrimitives.ReadUInt32LittleEndian(frame[at..]) != XxHash32(content))
            throw new InvalidDataException("The roster's LZ4 content checksum does not match.");
        return content;
    }

    private static byte[] Pack(byte[] content)
    {
        var output = new MemoryStream();
        output.Write(FrameHeader);
        var buffer = new byte[LZ4Codec.MaximumOutputSize(BlockSize)];
        Span<byte> word = stackalloc byte[4];
        for (var at = 0; at < content.Length; at += BlockSize)
        {
            var chunk = content.AsSpan(at, Math.Min(BlockSize, content.Length - at));
            var encoded = LZ4Codec.Encode(chunk, buffer, LZ4Level.L03_HC);
            if (encoded <= 0 || encoded >= chunk.Length)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)chunk.Length | 0x80000000);
                output.Write(word);
                output.Write(chunk);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)encoded);
                output.Write(word);
                output.Write(buffer, 0, encoded);
            }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(word, 0);
        output.Write(word);
        BinaryPrimitives.WriteUInt32LittleEndian(word, XxHash32(content));
        output.Write(word);
        return output.ToArray();
    }

    private const uint P1 = 2654435761, P2 = 2246822519, P3 = 3266489917, P4 = 668265263, P5 = 374761393;

    /// <summary>XXH32 with seed 0, the LZ4 frame content checksum.</summary>
    public static uint XxHash32(ReadOnlySpan<byte> input)
    {
        var at = 0;
        uint h;
        if (input.Length >= 16)
        {
            uint v1 = unchecked(P1 + P2), v2 = P2, v3 = 0, v4 = unchecked(0 - P1);
            for (; at + 16 <= input.Length; at += 16)
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt32LittleEndian(input[at..]));
                v2 = Round(v2, BinaryPrimitives.ReadUInt32LittleEndian(input[(at + 4)..]));
                v3 = Round(v3, BinaryPrimitives.ReadUInt32LittleEndian(input[(at + 8)..]));
                v4 = Round(v4, BinaryPrimitives.ReadUInt32LittleEndian(input[(at + 12)..]));
            }
            h = uint.RotateLeft(v1, 1) + uint.RotateLeft(v2, 7) + uint.RotateLeft(v3, 12) + uint.RotateLeft(v4, 18);
        }
        else
            h = P5;
        h = unchecked(h + (uint)input.Length);
        for (; at + 4 <= input.Length; at += 4)
            h = unchecked(uint.RotateLeft(h + BinaryPrimitives.ReadUInt32LittleEndian(input[at..]) * P3, 17) * P4);
        for (; at < input.Length; at++)
            h = unchecked(uint.RotateLeft(h + input[at] * P5, 11) * P1);
        h ^= h >> 15;
        h = unchecked(h * P2);
        h ^= h >> 13;
        h = unchecked(h * P3);
        h ^= h >> 16;
        return h;

        static uint Round(uint acc, uint lane) => unchecked(uint.RotateLeft(acc + lane * P2, 13) * P1);
    }
}
