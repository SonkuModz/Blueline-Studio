using System.Buffers.Binary;
using System.Text;

namespace Blueline.Formats.Tdb;

public sealed class LocRow
{
    internal LocRow(string? key, uint hash, string? text, byte[] record)
    {
        Key = key;
        Hash = hash;
        Text = text;
        Record = record;
    }

    public string? Key { get; }
    public uint Hash { get; }

    /// <summary>Null is distinct from empty: null means the record's pointer is 0xFFFFFFFF.</summary>
    public string? Text { get; internal set; }

    internal byte[] Record { get; }
}

/// <summary>
/// A localization dictionary (`*.db` under nhl/data/kettle/loc): one Huffman-pool table with fields for
/// key (kind 13), hash (kind 3) and text (kind 14). Edits keep the original Huffman tree, so only bytes
/// in the tree's alphabet can be written. Layout follows the supplied NHL 21 handoff and was re-verified
/// on NHL 19 by round-trip tests.
/// </summary>
public sealed class LocDictionary
{
    private const int MaxKeyBytes = 100;
    private const int MaxTextBytes = 4000;
    private const uint NullPointer = 0xFFFFFFFF;

    private readonly byte[] _source;
    private readonly int _table;
    private readonly int _recordsAt;
    private readonly int _stride;
    private readonly int _treeBytes;
    private readonly byte[] _tree;
    private readonly int[,] _nodes;
    private readonly int _nodeCount;
    private readonly Dictionary<byte, (uint Bits, int Length)> _codes = new();
    private readonly (int Bit, uint Kind)[] _fields;
    private readonly List<LocRow> _rows;
    private readonly Dictionary<string, LocRow> _byKey = new(StringComparer.Ordinal);

    public LocDictionary(byte[] data)
    {
        _source = data;
        if (!TdbFile.IsTdb(data))
            throw new InvalidDataException("Not a TDB database.");
        if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16)) != 1)
            throw new InvalidDataException("Localization dictionaries have exactly one table.");

        _table = 32 + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(28));
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_table + 4)) != 66)
            throw new InvalidDataException("Table is not a Huffman pool table.");

        _stride = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(_table + 8));
        var allocated = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_table + 20));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_table + 22));
        var fieldCount = data[_table + 28];
        _recordsAt = _table + 40 + fieldCount * 16;

        _fields = new (int, uint)[fieldCount];
        for (var i = 0; i < fieldCount; i++)
        {
            var p = _table + 40 + i * 16;
            _fields[i] = (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p + 4)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p)));
        }
        if (_fields.Count(f => f.Kind == 13) != 1 || _fields.Count(f => f.Kind == 14) != 1 || _fields.Count(f => f.Kind == 3) != 1)
            throw new InvalidDataException("Unexpected localization table fields.");

        var pool = data.AsSpan(_recordsAt + allocated * _stride);
        (_nodes, _nodeCount, _treeBytes) = ReadTree(pool);
        _tree = pool[.._treeBytes].ToArray();
        BuildCodes(0, 0, 0);

        _rows = new List<LocRow>(count);
        for (var i = 0; i < count; i++)
        {
            var record = data.AsSpan(_recordsAt + i * _stride, _stride).ToArray();
            string? key = null, text = null;
            uint hash = 0;
            foreach (var (bit, kind) in _fields)
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(bit / 8));
                if (kind == 3)
                    hash = value;
                else if (kind == 13)
                    key = DecodeString(pool, value, 1);
                else
                    text = DecodeString(pool, value, 2);
            }
            var row = new LocRow(key, hash, text, record);
            _rows.Add(row);
            if (key != null)
                _byKey.TryAdd(key, row);
        }
    }

    public IReadOnlyList<LocRow> Rows => _rows;

    public bool TryGet(string key, out LocRow row) => _byKey.TryGetValue(key, out row!);

    /// <summary>The first character this tree cannot encode, or null if all can be written.</summary>
    public char? FindUnencodable(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var buffer = new byte[4];
            var written = rune.EncodeToUtf8(buffer);
            for (var i = 0; i < written; i++)
                if (!_codes.ContainsKey(buffer[i]))
                    return rune.IsBmp ? (char)rune.Value : '?';
        }
        return null;
    }

    public void SetText(string key, string? text)
    {
        if (!_byKey.TryGetValue(key, out var row))
            throw new KeyNotFoundException($"No string has the key '{key}'.");
        Validate(text, MaxTextBytes, key);
        row.Text = text;
    }

    public void AddKey(string key, string? text)
    {
        if (_byKey.ContainsKey(key))
            throw new InvalidOperationException($"The key '{key}' already exists.");
        Validate(key, MaxKeyBytes, key);
        Validate(text, MaxTextBytes, key);
        var hash = KeyHash(key);
        if (_rows.Any(r => r.Hash == hash))
            throw new InvalidOperationException($"The key '{key}' collides with an existing key's hash.");
        if (_rows.Count >= ushort.MaxValue)
            throw new InvalidOperationException("The table is at its maximum row count.");

        var record = new byte[_stride];
        foreach (var (bit, kind) in _fields)
            if (kind == 3)
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(bit / 8), hash);

        var row = new LocRow(key, hash, text, record);
        var at = _rows.FindIndex(r => r.Hash > hash);
        _rows.Insert(at < 0 ? _rows.Count : at, row);
        _byKey[key] = row;
    }

    private void Validate(string? text, int limit, string key)
    {
        if (text == null)
            return;
        var length = Encoding.UTF8.GetByteCount(text);
        if (length > limit)
            throw new InvalidOperationException($"'{key}': text is {length} bytes; the limit is {limit}.");
        if (FindUnencodable(text) is { } bad)
            throw new InvalidOperationException($"'{key}': the character '{bad}' is not in this language's encoding table.");
    }

    /// <summary>The key hash: CRC over masked UTF-16 code units. Different from the file's integrity CRC.</summary>
    public static uint KeyHash(string key)
    {
        var c = 0u;
        foreach (var unit in key)
        {
            c ^= unit & 0xDFu;
            for (var i = 0; i < 8; i++)
                c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1;
        }
        return c;
    }

    public byte[] Save()
    {
        using var pool = new MemoryStream();
        pool.Write(_tree);
        var written = new Dictionary<(int, string), uint>();

        uint Put(string? text, int prefix)
        {
            if (text == null)
                return NullPointer;
            if (written.TryGetValue((prefix, text), out var existing))
                return existing;

            var offset = (uint)pool.Position;
            var bytes = Encoding.UTF8.GetBytes(text);
            if (prefix == 1)
                pool.WriteByte((byte)bytes.Length);
            else
            {
                pool.WriteByte((byte)(bytes.Length >> 8));
                pool.WriteByte((byte)bytes.Length);
            }
            byte current = 0;
            var used = 0;
            foreach (var b in bytes)
            {
                var (bits, length) = _codes[b];
                for (var i = length - 1; i >= 0; i--)
                {
                    current = (byte)((current << 1) | (int)((bits >> i) & 1));
                    if (++used == 8)
                    {
                        pool.WriteByte(current);
                        current = 0;
                        used = 0;
                    }
                }
            }
            if (used > 0)
                pool.WriteByte((byte)(current << (8 - used)));
            written[(prefix, text)] = offset;
            return offset;
        }

        var records = new byte[_rows.Count * _stride];
        for (var i = 0; i < _rows.Count; i++)
        {
            var record = records.AsSpan(i * _stride, _stride);
            _rows[i].Record.CopyTo(record);
            foreach (var (bit, kind) in _fields)
            {
                if (kind == 13)
                    BinaryPrimitives.WriteUInt32LittleEndian(record[(bit / 8)..], Put(_rows[i].Key, 1));
                else if (kind == 14)
                    BinaryPrimitives.WriteUInt32LittleEndian(record[(bit / 8)..], Put(_rows[i].Text, 2));
            }
        }

        var poolSize = (int)pool.Length;
        var padding = (4 - (_recordsAt + records.Length + poolSize) % 4) % 4;
        var output = new byte[_recordsAt + records.Length + poolSize + padding + 4];
        _source.AsSpan(0, _recordsAt).CopyTo(output);
        records.CopyTo(output, _recordsAt);
        pool.GetBuffer().AsSpan(0, poolSize).CopyTo(output.AsSpan(_recordsAt + records.Length));

        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), output.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(_table + 16), poolSize);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(_table + 20), (ushort)_rows.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(_table + 22), (ushort)_rows.Count);

        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[_table..], TdbCrc.Compute(span[24..32]));
        BinaryPrimitives.WriteUInt32LittleEndian(span[(_table + 36)..], TdbCrc.Compute(span[(_table + 4)..(_table + 36)]));
        BinaryPrimitives.WriteUInt32LittleEndian(span[^4..], TdbCrc.Compute(span[(_table + 40)..^4]));
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], TdbCrc.Compute(span[..20]));
        return output;
    }

    /// <summary>Breadth-first tree: each node is left marker, left value, right marker, right value; zero marker = leaf.</summary>
    private static (int[,] Nodes, int Count, int Bytes) ReadTree(ReadOnlySpan<byte> pool)
    {
        const int Limit = 1024;
        var nodes = new int[Limit, 3]; // left, right, leaf value (or -1)
        for (var i = 0; i < Limit; i++)
            nodes[i, 2] = -1;

        var count = 1;
        var queue = new Queue<int>();
        queue.Enqueue(0);
        var position = 0;
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            var children = new int[2];
            for (var side = 0; side < 2; side++)
            {
                var marker = pool[position++];
                var value = pool[position++];
                var node = count++;
                if (node >= Limit)
                    throw new InvalidDataException("Huffman tree is too large.");
                if (marker == 0)
                    nodes[node, 2] = value;
                children[side] = node;
            }
            nodes[parent, 0] = children[0];
            nodes[parent, 1] = children[1];
            if (nodes[children[1], 2] < 0)
                queue.Enqueue(children[1]);
            if (nodes[children[0], 2] < 0)
                queue.Enqueue(children[0]);
        }
        return (nodes, count, position);
    }

    private void BuildCodes(int node, uint bits, int length)
    {
        if (_nodes[node, 2] >= 0)
        {
            _codes[(byte)_nodes[node, 2]] = (bits, length);
            return;
        }
        BuildCodes(_nodes[node, 0], bits << 1, length + 1);
        BuildCodes(_nodes[node, 1], bits << 1 | 1, length + 1);
    }

    private string? DecodeString(ReadOnlySpan<byte> pool, uint offset, int prefix)
    {
        if (offset == NullPointer)
            return null;
        var position = (int)offset;
        int length = prefix == 1 ? pool[position] : pool[position] << 8 | pool[position + 1];
        var bit = (position + prefix) * 8;
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            var node = 0;
            while (_nodes[node, 2] < 0)
            {
                var v = (pool[bit >> 3] >> (7 - (bit & 7))) & 1;
                bit++;
                node = _nodes[node, v];
            }
            bytes[i] = (byte)_nodes[node, 2];
        }
        return Encoding.UTF8.GetString(bytes);
    }
}
