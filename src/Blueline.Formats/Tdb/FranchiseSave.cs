using System.Buffers.Binary;
using System.Text;

namespace Blueline.Formats.Tdb;

/// <summary>
/// An NHL 19 Franchise (Be a GM) save folder, such as BeAGM0: DATA, INDEX, NAME and sce_sys.
/// <para>
/// The outer layer matches <see cref="RosterSave"/>. DATA holds [u64 length][8-byte tag][payload] records, and the last
/// one is 0x01, an LZ4 frame and a u32 of the unpacked size. The differences:
/// <list type="bullet">
/// <item>There is no CRCValue.</item>
/// <item>DATA can have a stray byte before the payload (BeAGM0 has one at 0x11C), so this reader goes by INDEX
/// (+0x30 is the payload record's offset) and does not walk the records.</item>
/// <item>The payload is packed twice. The outer frame unpacks to inner blocks, each 0x01 and an LZ4 frame with no size
/// after it, then raw bytes. One block is the league TDB (34 league* tables). The others hold the 760-byte player
/// records, team and line data, and state not decoded yet. They are kept byte for byte.</item>
/// </list>
/// </para>
/// Writing back has not been seen in game. Test an unedited repack (<see cref="WriteTo"/> with the stored database)
/// in the sandbox first.
/// </summary>
public sealed class FranchiseSave
{
    private static readonly byte[] FrameMagic = [0x04, 0x22, 0x4D, 0x18];

    private readonly byte[] _data;
    private readonly byte[] _index;
    private readonly int _payloadAt;
    private readonly List<byte[]> _innerFrames; // each block as stored: 0x01 + its LZ4 frame
    private readonly byte[] _tail;

    private FranchiseSave(string folder, byte[] data, byte[] index, int payloadAt, List<byte[]> innerFrames, byte[] tail,
        int databaseBlock, byte[] database, IReadOnlyList<int> blockSizes)
    {
        Folder = folder;
        _data = data;
        _index = index;
        _payloadAt = payloadAt;
        _innerFrames = innerFrames;
        _tail = tail;
        DatabaseBlock = databaseBlock;
        Database = database;
        BlockSizes = blockSizes;
    }

    public string Folder { get; }

    /// <summary>The league TDB as stored. Parse a clone to edit it.</summary>
    public byte[] Database { get; }

    /// <summary>Which inner block holds the TDB.</summary>
    public int DatabaseBlock { get; }

    /// <summary>The unpacked size of every inner block, in order.</summary>
    public IReadOnlyList<int> BlockSizes { get; }

    public int TailLength => _tail.Length;

    /// <summary>The file info record as text (team, season, user name, backup flag).</summary>
    public string FileInfo
    {
        get
        {
            var text = Encoding.UTF8.GetString(_data, 0, _payloadAt);
            var at = text.IndexOf("\"TeamFullName\"", StringComparison.Ordinal);
            if (at < 0)
                return "";
            var start = text.LastIndexOf('{', at);
            var end = text.IndexOf('}', at);
            return start >= 0 && end > start ? text[start..(end + 1)] : "";
        }
    }

    public static bool IsFranchiseSave(string folder) =>
        File.Exists(Path.Combine(folder, "DATA")) && File.Exists(Path.Combine(folder, "INDEX"))
        && Path.GetFileName(Path.GetFullPath(folder).TrimEnd('\\', '/')).StartsWith("BeAGM", StringComparison.OrdinalIgnoreCase);

    public static FranchiseSave Read(string folder)
    {
        var data = File.ReadAllBytes(Path.Combine(folder, "DATA"));
        var index = File.ReadAllBytes(Path.Combine(folder, "INDEX"));
        if (index.Length < 0x40)
            throw new InvalidDataException("INDEX is too short.");
        var recordLength = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(0x28)) - 16;
        var payloadAt = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(0x30));
        if (BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(0x38)) != data.Length || payloadAt < 0 || recordLength < 16
            || payloadAt + 16 + recordLength != data.Length)
            throw new InvalidDataException("INDEX does not describe DATA's payload record.");
        if (BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan((int)payloadAt)) != recordLength)
            throw new InvalidDataException("The payload record's length does not match INDEX.");

        var payload = data.AsSpan((int)payloadAt + 16, (int)recordLength);
        if (payload[0] != 1 || !payload.Slice(1, 4).SequenceEqual(FrameMagic))
            throw new InvalidDataException("The payload is not an LZ4 frame of the kind NHL 19 writes.");
        var outer = RosterSave.Unpack(payload[1..^4]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(payload[^4..]) != outer.Length)
            throw new InvalidDataException("The payload's size does not match what its frame unpacks to.");

        var frames = new List<byte[]>();
        var sizes = new List<int>();
        byte[]? database = null;
        var databaseBlock = -1;
        var at = 0;
        while (at + 5 <= outer.Length && outer[at] == 1 && outer.AsSpan(at + 1, 4).SequenceEqual(FrameMagic))
        {
            var end = FrameEnd(outer, at + 1);
            var content = RosterSave.Unpack(outer.AsSpan(at + 1, end - at - 1));
            frames.Add(outer[at..end]);
            sizes.Add(content.Length);
            if (database == null && TdbFile.IsTdb(content))
            {
                database = content;
                databaseBlock = frames.Count - 1;
            }
            at = end;
        }
        if (database == null)
            throw new InvalidDataException("No block of the Franchise payload is a TDB.");
        return new FranchiseSave(folder, data, index, (int)payloadAt, frames, outer[at..], databaseBlock, database, sizes);
    }

    /// <summary>
    /// Writes this save with another league database into a folder (a copy of this save's folder, with DATA and INDEX
    /// rebuilt). Every other block, the tail and everything before the payload are kept as they were. The database's own
    /// checksums must already be repaired.
    /// </summary>
    public void WriteTo(string folder, byte[] database)
    {
        var file = TdbFile.Parse((byte[])database.Clone());
        if (file.VerifyCrcs().FirstOrDefault(c => !c.Matches) is { } bad)
            throw new InvalidDataException($"The league database checksum {bad.Name} does not verify.");

        var outer = new MemoryStream();
        for (var i = 0; i < _innerFrames.Count; i++)
        {
            if (i == DatabaseBlock && !database.AsSpan().SequenceEqual(Database))
            {
                outer.WriteByte(1);
                outer.Write(RosterSave.Pack(database));
            }
            else
                outer.Write(_innerFrames[i]);
        }
        outer.Write(_tail);
        var unpacked = outer.ToArray();

        var frame = RosterSave.Pack(unpacked);
        var payload = new byte[1 + frame.Length + 4];
        payload[0] = 1;
        frame.CopyTo(payload, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(payload.Length - 4), (uint)unpacked.Length);

        var data = new byte[_payloadAt + 16 + payload.Length];
        _data.AsSpan(0, _payloadAt + 16).CopyTo(data);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(_payloadAt), payload.Length);
        payload.CopyTo(data, _payloadAt + 16);

        var index = (byte[])_index.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(0x28), 16 + payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(0x38), data.Length);

        if (!Path.GetFullPath(folder).Equals(Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase))
            RosterSave.CopyFolder(Folder, folder);
        File.WriteAllBytes(Path.Combine(folder, "DATA"), data);
        File.WriteAllBytes(Path.Combine(folder, "INDEX"), index);
    }

    /// <summary>The end of the LZ4 frame starting at <paramref name="at"/> (its magic), past the content checksum.</summary>
    private static int FrameEnd(byte[] buffer, int at)
    {
        var flags = buffer[at + 4];
        var position = at + 7 + ((flags & 0x08) != 0 ? 8 : 0);
        while (true)
        {
            if (position + 4 > buffer.Length)
                throw new InvalidDataException("An inner LZ4 frame runs past the end of the payload.");
            var size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(position));
            position += 4;
            if (size == 0)
                break;
            position += (int)(size & 0x7FFFFFFF) + ((flags & 0x10) != 0 ? 4 : 0);
        }
        return position + ((flags & 0x04) != 0 ? 4 : 0);
    }
}
