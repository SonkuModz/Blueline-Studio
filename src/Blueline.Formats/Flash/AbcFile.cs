using System.Text;

namespace Blueline.Formats.Flash;

/// <summary>A name in the ABC multiname pool, decoded as far as finding things by name needs.</summary>
public sealed record AbcName(int Kind, int Namespace, int Name, int NamespaceSet, int[] Parameters)
{
    public bool IsQName => Kind is 0x07 or 0x0D;
}

/// <summary>A trait (field, method, getter, setter, class or function slot) of a class, instance or script.</summary>
public sealed record AbcTrait(int Name, int Kind, int Attributes, int Index);

public sealed record AbcInstance(int Name, int SuperName, int Init, IReadOnlyList<AbcTrait> Traits);

/// <summary>A method body. <see cref="Raw"/> is the body exactly as read; it is written back as is unless replaced.</summary>
public sealed class AbcMethodBody
{
    public int Method { get; init; }
    public int MaxStack { get; set; }
    public int LocalCount { get; set; }
    public int InitScopeDepth { get; set; }
    public int MaxScopeDepth { get; set; }
    public byte[] Code { get; set; } = [];
    /// <summary>The exception table and traits that follow the code, as stored.</summary>
    public byte[] Tail { get; set; } = [];
    internal byte[]? Raw { get; set; }
}

/// <summary>
/// AVM2 bytecode (ABC), read far enough to find a class's methods and to replace a method's body, and to add constants for new
/// code. The constant pools grow only at their ends, so every index the rest of the file holds stays valid, and the sections
/// between the pools and the method bodies (methods, metadata, classes, scripts) are copied byte for byte. Read back unchanged,
/// it writes the same bytes. Format: Adobe's AVM2 overview, chapter 4.
/// </summary>
public sealed class AbcFile
{
    private readonly byte[] _data;
    private readonly int _version;
    private readonly (int Start, int EntriesStart, int End)[] _pools = new (int, int, int)[7];
    private readonly int _middleStart, _middleEnd, _bodiesCountEnd;
    private readonly List<byte[]>[] _added = Enumerable.Range(0, 7).Select(_ => new List<byte[]>()).ToArray();

    public List<int> Ints { get; } = [0];
    public List<uint> Uints { get; } = [0];
    public List<double> Doubles { get; } = [double.NaN];
    public List<string> Strings { get; } = ["*"];
    /// <summary>Kind and name (a string index) of each namespace.</summary>
    public List<(int Kind, int Name)> Namespaces { get; } = [(0, 0)];
    public List<int[]> NamespaceSets { get; } = [[]];
    public List<AbcName> Names { get; } = [new AbcName(0, 0, 0, 0, [])];
    public int MethodCount { get; }
    public List<AbcInstance> Instances { get; } = [];
    public List<AbcMethodBody> Bodies { get; } = [];

    private const int PoolInt = 0, PoolUint = 1, PoolDouble = 2, PoolString = 3, PoolNamespace = 4, PoolNsSet = 5, PoolName = 6;

    private AbcFile(byte[] data)
    {
        _data = data;
        var r = new Reader(data);
        _version = r.U16() | r.U16() << 16;

        int Pool(int which, Action read)
        {
            var start = r.At;
            var count = (int)r.U30();
            var entries = r.At;
            for (var i = 1; i < count; i++)
                read();
            _pools[which] = (start, entries, r.At);
            return count;
        }
        Pool(PoolInt, () => Ints.Add((int)r.U30()));
        Pool(PoolUint, () => Uints.Add(r.U30()));
        Pool(PoolDouble, () => Doubles.Add(r.Double()));
        Pool(PoolString, () => { var n = (int)r.U30(); Strings.Add(Encoding.UTF8.GetString(data, r.At, n)); r.At += n; });
        Pool(PoolNamespace, () => { var kind = r.U8(); Namespaces.Add((kind, (int)r.U30())); });
        Pool(PoolNsSet, () => { var n = (int)r.U30(); NamespaceSets.Add(Enumerable.Range(0, n).Select(_ => (int)r.U30()).ToArray()); });
        Pool(PoolName, () => Names.Add(ReadName(r)));

        _middleStart = r.At;
        MethodCount = (int)r.U30();
        for (var i = 0; i < MethodCount; i++)
        {
            var paramCount = (int)r.U30();
            r.U30();
            for (var p = 0; p < paramCount; p++)
                r.U30();
            r.U30();
            var flags = r.U8();
            if ((flags & 0x08) != 0)
                for (int o = 0, n = (int)r.U30(); o < n; o++) { r.U30(); r.U8(); }
            if ((flags & 0x80) != 0)
                for (var p = 0; p < paramCount; p++)
                    r.U30();
        }
        for (int i = 0, n = (int)r.U30(); i < n; i++)
        {
            r.U30();
            for (int k = 0, items = (int)r.U30(); k < items; k++) { r.U30(); r.U30(); }
        }
        var classCount = (int)r.U30();
        for (var i = 0; i < classCount; i++)
        {
            int name = (int)r.U30(), super = (int)r.U30();
            var flags = r.U8();
            if ((flags & 0x08) != 0)
                r.U30();
            for (int k = 0, n = (int)r.U30(); k < n; k++)
                r.U30();
            var init = (int)r.U30();
            Instances.Add(new AbcInstance(name, super, init, ReadTraits(r)));
        }
        for (var i = 0; i < classCount; i++) { r.U30(); ReadTraits(r); }
        for (int i = 0, n = (int)r.U30(); i < n; i++) { r.U30(); ReadTraits(r); }
        _middleEnd = r.At;

        var bodyCount = (int)r.U30();
        _bodiesCountEnd = r.At;
        for (var i = 0; i < bodyCount; i++)
        {
            var start = r.At;
            var body = new AbcMethodBody
            {
                Method = (int)r.U30(), MaxStack = (int)r.U30(), LocalCount = (int)r.U30(), InitScopeDepth = (int)r.U30(), MaxScopeDepth = (int)r.U30(),
            };
            var codeLength = (int)r.U30();
            body.Code = data[r.At..(r.At + codeLength)];
            r.At += codeLength;
            var tailStart = r.At;
            for (int k = 0, n = (int)r.U30(); k < n; k++)
                for (var f = 0; f < 5; f++)
                    r.U30();
            ReadTraits(r);
            body.Tail = data[tailStart..r.At];
            body.Raw = data[start..r.At];
            Bodies.Add(body);
        }
        if (r.At != data.Length)
            throw new InvalidDataException($"The ABC block has {data.Length - r.At} bytes after its method bodies.");
    }

    public static AbcFile Parse(byte[] data) => new(data);

    private static AbcName ReadName(Reader r)
    {
        var kind = r.U8();
        return kind switch
        {
            0x07 or 0x0D => new AbcName(kind, (int)r.U30(), (int)r.U30(), 0, []),
            0x0F or 0x10 => new AbcName(kind, 0, (int)r.U30(), 0, []),
            0x11 or 0x12 => new AbcName(kind, 0, 0, 0, []),
            0x09 or 0x0E => new AbcName(kind, 0, (int)r.U30(), (int)r.U30(), []),
            0x1B or 0x1C => new AbcName(kind, 0, 0, (int)r.U30(), []),
            0x1D => ReadTypeName(r),
            _ => throw new InvalidDataException($"Unknown multiname kind 0x{kind:X2}."),
        };
    }

    private static AbcName ReadTypeName(Reader r)
    {
        var name = (int)r.U30();
        var count = (int)r.U30();
        return new AbcName(0x1D, 0, name, 0, Enumerable.Range(0, count).Select(_ => (int)r.U30()).ToArray());
    }

    private static List<AbcTrait> ReadTraits(Reader r)
    {
        var traits = new List<AbcTrait>();
        for (int i = 0, n = (int)r.U30(); i < n; i++)
        {
            var name = (int)r.U30();
            var kindByte = r.U8();
            int kind = kindByte & 15, attributes = kindByte >> 4, index;
            switch (kind)
            {
                case 0 or 6:
                    r.U30();
                    index = (int)r.U30();
                    if (r.U30() != 0)
                        r.U8();
                    break;
                case 1 or 2 or 3 or 4 or 5:
                    r.U30();
                    index = (int)r.U30();
                    break;
                default:
                    throw new InvalidDataException($"Unknown trait kind {kind}.");
            }
            if ((attributes & 0x4) != 0)
                for (int k = 0, m = (int)r.U30(); k < m; k++)
                    r.U30();
            traits.Add(new AbcTrait(name, kind, attributes, index));
        }
        return traits;
    }

    /// <summary>The local name of a multiname ("*" where it has none).</summary>
    public string NameOf(int multiname) => Names[multiname] is { Kind: not (0x11 or 0x12 or 0x1B or 0x1C) } n ? Strings[n.Name] : "*";

    /// <summary>A QName's namespace and name, like "nhl.overlays.scoreclock::ScoreClockOverlay".</summary>
    public string FullNameOf(int multiname)
    {
        var n = Names[multiname];
        var ns = n.IsQName ? Strings[Namespaces[n.Namespace].Name] : "";
        return ns.Length > 0 ? ns + "::" + NameOf(multiname) : NameOf(multiname);
    }

    public AbcInstance Class(string fullName) =>
        Instances.FirstOrDefault(i => FullNameOf(i.Name) == fullName) ?? throw new KeyNotFoundException($"No class {fullName}.");

    /// <summary>The method index of an instance method, getter or setter by its name.</summary>
    public int MethodOf(AbcInstance instance, string name) =>
        instance.Traits.SingleOrDefault(t => t.Kind is 1 or 2 or 3 && NameOf(t.Name) == name)?.Index
        ?? throw new KeyNotFoundException($"{FullNameOf(instance.Name)} has no method {name}.");

    public AbcMethodBody BodyOf(int method) => Bodies.Single(b => b.Method == method);

    /// <summary>Marks a body as changed, so it is written from its fields instead of the bytes it was read from.</summary>
    public void Changed(AbcMethodBody body) => body.Raw = null;

    public int AddString(string value)
    {
        var at = Strings.IndexOf(value, 1);
        if (at > 0)
            return at;
        var bytes = Encoding.UTF8.GetBytes(value);
        var w = new Writer();
        w.U30((uint)bytes.Length);
        w.Bytes(bytes);
        _added[PoolString].Add(w.ToArray());
        Strings.Add(value);
        return Strings.Count - 1;
    }

    public int AddDouble(double value)
    {
        var at = Doubles.FindIndex(1, d => d.Equals(value));
        if (at > 0)
            return at;
        _added[PoolDouble].Add(BitConverter.GetBytes(value));
        Doubles.Add(value);
        return Doubles.Count - 1;
    }

    public int AddInt(int value)
    {
        var at = Ints.IndexOf(value, 1);
        if (at > 0)
            return at;
        var w = new Writer();
        w.U30(unchecked((uint)value));
        _added[PoolInt].Add(w.ToArray());
        Ints.Add(value);
        return Ints.Count - 1;
    }

    /// <summary>A QName in the public namespace whose name is the given string, added if there is none.</summary>
    public int PublicName(string name)
    {
        var nameIndex = AddString(name);
        var ns = Namespaces.FindIndex(1, n => n.Kind == 0x16 && Strings[n.Name] == "");
        if (ns < 0)
            throw new InvalidDataException("The ABC block has no public namespace.");
        var at = Names.FindIndex(1, n => n.Kind == 0x07 && n.Namespace == ns && n.Name == nameIndex);
        if (at > 0)
            return at;
        var w = new Writer();
        w.U8(0x07);
        w.U30((uint)ns);
        w.U30((uint)nameIndex);
        _added[PoolName].Add(w.ToArray());
        Names.Add(new AbcName(0x07, ns, nameIndex, 0, []));
        return Names.Count - 1;
    }

    public byte[] ToBytes()
    {
        var w = new Writer();
        w.Bytes(_data.AsSpan(0, 4));
        for (var p = 0; p < 7; p++)
        {
            var (start, entries, end) = _pools[p];
            if (_added[p].Count == 0)
            {
                w.Bytes(_data.AsSpan(start, end - start));
                continue;
            }
            var oldCount = (int)new Reader(_data) { At = start }.U30();
            w.U30((uint)(Math.Max(oldCount, 1) + _added[p].Count));
            w.Bytes(_data.AsSpan(entries, end - entries));
            foreach (var entry in _added[p])
                w.Bytes(entry);
        }
        w.Bytes(_data.AsSpan(_middleStart, _middleEnd - _middleStart));
        w.Bytes(_data.AsSpan(_middleEnd, _bodiesCountEnd - _middleEnd));
        foreach (var body in Bodies)
        {
            if (body.Raw != null)
            {
                w.Bytes(body.Raw);
                continue;
            }
            w.U30((uint)body.Method);
            w.U30((uint)body.MaxStack);
            w.U30((uint)body.LocalCount);
            w.U30((uint)body.InitScopeDepth);
            w.U30((uint)body.MaxScopeDepth);
            w.U30((uint)body.Code.Length);
            w.Bytes(body.Code);
            w.Bytes(body.Tail);
        }
        return w.ToArray();
    }

    internal sealed class Reader(byte[] data)
    {
        public int At;
        public int U8() => data[At++];
        public int U16() { var v = data[At] | data[At + 1] << 8; At += 2; return v; }
        public double Double() { var v = BitConverter.ToDouble(data, At); At += 8; return v; }
        public int S24() { var v = data[At] | data[At + 1] << 8 | data[At + 2] << 16; At += 3; return v << 8 >> 8; }

        public uint U30()
        {
            uint value = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var b = data[At++];
                value |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    break;
            }
            return value;
        }
    }

    internal sealed class Writer
    {
        private readonly MemoryStream _out = new();
        public void U8(int value) => _out.WriteByte((byte)value);
        public void Bytes(ReadOnlySpan<byte> bytes) => _out.Write(bytes);
        public void S24(int value) { U8(value & 0xFF); U8(value >> 8 & 0xFF); U8(value >> 16 & 0xFF); }

        public void U30(uint value)
        {
            do
            {
                var b = (byte)(value & 0x7F);
                value >>= 7;
                _out.WriteByte(value != 0 ? (byte)(b | 0x80) : b);
            }
            while (value != 0);
        }

        public byte[] ToArray() => _out.ToArray();
        public int Length => (int)_out.Length;
    }
}
