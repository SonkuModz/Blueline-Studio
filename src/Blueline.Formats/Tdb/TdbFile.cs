using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;

namespace Blueline.Formats.Tdb;

public sealed record TdbField(string ShortName, uint Kind, int BitOffset, int Depth)
{
    public string? Name { get; set; }
    public string Label => Name ?? ShortName;

    /// <summary>Fixed-width inline string; anything else is an integer of Depth bits.</summary>
    public bool IsInlineString => Depth > 32 && Depth % 8 == 0;

    /// <summary>Kinds whose value is a pointer into a Huffman pool; only localization tables use them.</summary>
    public bool IsPoolPointer => Kind is 13 or 14;
}

public sealed record TdbCrcCheck(string Name, int Start, int End, int StoredAt, uint Stored, uint Computed)
{
    public bool Matches => Stored == Computed;
}

public sealed class TdbTable
{
    private readonly TdbFile _file;

    internal TdbTable(TdbFile file, int slot, string shortName)
    {
        _file = file;
        Slot = slot;
        ShortName = shortName;
        var data = file.Data;
        var header = slot + 4;
        AllocationType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(header));
        Stride = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(header + 4));
        Allocated = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(header + 0x10));
        Count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(header + 0x12));
        var fieldCount = data[header + 0x18];

        var p = header + 0x24;
        var fields = new TdbField[fieldCount];
        for (var i = 0; i < fieldCount; i++)
        {
            fields[i] = new TdbField(
                Encoding.ASCII.GetString(data, p + 8, 4),
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p)),
                BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p + 4)),
                BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p + 12)));
            p += 16;
        }
        Fields = fields;
        RecordsAt = p;
    }

    public string ShortName { get; }
    public string? Name { get; set; }
    public string Label => Name ?? ShortName;
    public int Slot { get; }
    public uint AllocationType { get; }
    public int Stride { get; }
    public int Allocated { get; }
    public int Count { get; }
    public IReadOnlyList<TdbField> Fields { get; }
    public int RecordsAt { get; }

    /// <summary>A copy of every record, so a multi-cell edit can be rolled back whole.</summary>
    public byte[] RecordsSnapshot() => _file.Data.AsSpan(RecordsAt, Count * Stride).ToArray();

    public void RestoreRecords(byte[] snapshot) => snapshot.CopyTo(_file.Data, RecordsAt);

    private Span<byte> Record(int row)
    {
        if (row < 0 || row >= Count)
            throw new ArgumentOutOfRangeException(nameof(row));
        return _file.Data.AsSpan(RecordsAt + row * Stride, Stride);
    }

    public object Read(int row, TdbField field)
    {
        var record = Record(row);
        if (!field.IsInlineString)
            return ReadBits(record, field.BitOffset, field.Depth);

        var bytes = new byte[field.Depth / 8];
        for (var k = 0; k < bytes.Length; k++)
            bytes[k] = (byte)ReadBits(record, field.BitOffset + k * 8, 8);
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    /// <summary>Writes one cell. The caller must call <see cref="TdbFile.RepairCrcs"/> before saving.</summary>
    public void Write(int row, TdbField field, long value)
    {
        if (field.IsInlineString || field.IsPoolPointer)
            throw new InvalidOperationException($"{field.Label} is not an integer field.");
        if (field.Depth < 64 && (value < 0 || value >= 1L << field.Depth))
            throw new ArgumentOutOfRangeException(nameof(value), $"{field.Label} holds {field.Depth} bits: 0 to {(1L << field.Depth) - 1}.");
        WriteBits(Record(row), field.BitOffset, field.Depth, value);
    }

    public void Write(int row, TdbField field, string value)
    {
        if (!field.IsInlineString)
            throw new InvalidOperationException($"{field.Label} is not a text field.");
        var bytes = Encoding.UTF8.GetBytes(value);
        var capacity = field.Depth / 8;
        if (bytes.Length >= capacity)
            throw new ArgumentOutOfRangeException(nameof(value), $"{field.Label} holds {capacity - 1} bytes; this text is {bytes.Length}.");
        var record = Record(row);
        for (var k = 0; k < capacity; k++)
            WriteBits(record, field.BitOffset + k * 8, 8, k < bytes.Length ? bytes[k] : 0);
    }

    /// <summary>Bit fields are LSB-first within each byte.</summary>
    public static long ReadBits(ReadOnlySpan<byte> record, int bit, int depth)
    {
        long value = 0;
        for (var i = 0; i < depth; i++)
        {
            var b = bit + i;
            value |= (long)((record[b >> 3] >> (b & 7)) & 1) << i;
        }
        return value;
    }

    private static void WriteBits(Span<byte> record, int bit, int depth, long value)
    {
        for (var i = 0; i < depth; i++)
        {
            var b = bit + i;
            var mask = (byte)(1 << (b & 7));
            if (((value >> i) & 1) != 0)
                record[b >> 3] |= mask;
            else
                record[b >> 3] &= (byte)~mask;
        }
    }
}

/// <summary>
/// EA TDB database. Tables are preceded by a 4-byte CRC slot; a table's body CRC is stored in the next
/// table's slot, or the last 4 bytes of the file. The first table's slot holds the directory CRC.
/// </summary>
public sealed class TdbFile
{
    private const int DirectoryAt = 0x18;

    private TdbFile(byte[] data)
    {
        Data = data;
        var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x10));
        DirectoryEnd = DirectoryAt + count * 8;

        var tables = new TdbTable[count];
        for (var i = 0; i < count; i++)
        {
            var p = DirectoryAt + i * 8;
            var slot = DirectoryEnd + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p + 4));
            tables[i] = new TdbTable(this, slot, Encoding.ASCII.GetString(data, p, 4));
        }
        Tables = tables;
    }

    public byte[] Data { get; }
    public int DirectoryEnd { get; }
    public IReadOnlyList<TdbTable> Tables { get; }

    public static bool IsTdb(ReadOnlySpan<byte> data) =>
        data.Length > 0x20 && data[0] == 'D' && data[1] == 'B' && data[2] == 0 && data[3] == 8;

    public static TdbFile Parse(byte[] data)
    {
        if (!IsTdb(data))
            throw new InvalidDataException("Not a TDB database.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) != data.Length)
            throw new InvalidDataException("The database's size field does not match its length.");
        return new TdbFile(data);
    }

    public IReadOnlyList<TdbCrcCheck> VerifyCrcs()
    {
        var checks = new List<TdbCrcCheck>();
        foreach (var (name, start, end, at) in CrcRanges())
            checks.Add(new TdbCrcCheck(name, start, end, at, BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(at)), TdbCrc.Compute(Data.AsSpan(start, end - start))));
        return checks;
    }

    /// <summary>
    /// Recomputes checks in dependency order: table bodies and headers first (the directory CRC is held
    /// in a body-CRC-covered slot only for the first table, which starts after it), file header last.
    /// </summary>
    public void RepairCrcs()
    {
        foreach (var (_, start, end, at) in CrcRanges())
            BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(at), TdbCrc.Compute(Data.AsSpan(start, end - start)));
    }

    private IEnumerable<(string Name, int Start, int End, int At)> CrcRanges()
    {
        var slots = Tables.Select(t => t.Slot).OrderBy(s => s).ToArray();
        yield return ("directory", DirectoryAt, DirectoryEnd, slots[0]);
        foreach (var table in Tables)
        {
            yield return ($"{table.Label} header", table.Slot + 4, table.Slot + 36, table.Slot + 36);
            var next = Array.IndexOf(slots, table.Slot) + 1;
            var bodyAt = next < slots.Length ? slots[next] : Data.Length - 4;
            yield return ($"{table.Label} body", table.Slot + 40, bodyAt, bodyAt);
        }
        yield return ("file header", 0, 20, 20);
    }

    /// <summary>Applies names from a sibling `*-meta.xml`. Unknown elements are ignored.</summary>
    public void ApplyMeta(string xml)
    {
        var document = XDocument.Parse(xml.TrimStart('﻿'));
        var tables = Tables.ToDictionary(t => t.ShortName, StringComparer.Ordinal);
        foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "table"))
        {
            var shortName = (string?)element.Attribute("shortname");
            if (shortName == null || !tables.TryGetValue(shortName, out var table))
                continue;
            table.Name = (string?)element.Attribute("name");
            var fields = table.Fields.ToDictionary(f => f.ShortName, StringComparer.Ordinal);
            foreach (var field in element.Descendants().Where(e => e.Name.LocalName == "field"))
            {
                var fieldShort = (string?)field.Attribute("shortname");
                if (fieldShort != null && fields.TryGetValue(fieldShort, out var match))
                    match.Name = (string?)field.Attribute("name");
            }
        }
    }
}
