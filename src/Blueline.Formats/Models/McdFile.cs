using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Blueline.Formats.Models;

/// <summary>
/// One field of a vertex: "t0:0C:00:0001:0000:00:2f16" is texcoord 0 at offset 0x0C of stream slot 0, two 16-bit floats.
/// A shape's vertices can be split across streams, so the slot says which of its streams holds the field.
/// </summary>
public sealed record McdElement(string Semantic, int Offset, string Encoding, int Components, int ComponentBytes, char Kind, bool Normalized, int Slot = 0)
{
    public int Size => Components * ComponentBytes;
    public char Channel => Semantic[0];
    public int ChannelIndex => Semantic.Length > 1 && char.IsDigit(Semantic[1]) ? Semantic[1] - '0' : 0;

    public void Read(ReadOnlySpan<byte> vertex, Span<float> into)
    {
        for (var i = 0; i < Math.Min(Components, into.Length); i++)
        {
            var at = vertex[(Offset + i * ComponentBytes)..];
            into[i] = (Kind, ComponentBytes) switch
            {
                ('f', 4) => BinaryPrimitives.ReadSingleLittleEndian(at),
                ('f', 2) => (float)BinaryPrimitives.ReadHalfLittleEndian(at),
                ('s', 2) => Math.Max(BinaryPrimitives.ReadInt16LittleEndian(at) / 32767f, -1f),
                ('u', 2) => BinaryPrimitives.ReadUInt16LittleEndian(at) / 65535f,
                ('s', 1) => Math.Max((sbyte)at[0] / 127f, -1f),
                ('u', 1) => at[0] / 255f,
                _ => 0f,
            };
        }
    }
}

public sealed record McdFormat(string Text, IReadOnlyList<McdElement> Elements)
{
    public int Stride => Elements.Sum(e => e.Size);
    public McdElement? Find(char channel, int index = 0) => Elements.FirstOrDefault(e => e.Channel == channel && e.ChannelIndex == index);
}

public sealed record McdStream(int DataStart, int Count, int Stride, McdFormat? Format);

public sealed record McdIndexBuffer(int DataStart, int Count, int IndexBytes);

/// <param name="Stream">The first of the shape's streams, which holds positions.</param>
/// <param name="Format">The vertex format its binding names, or -1 if unknown.</param>
/// <param name="AllStreams">Every stream of the shape, by slot. Null when only <paramref name="Stream"/> is known.</param>
public sealed record McdShape(string Name, int Stream, int IndexBuffer, int Triangles, int Format = -1, IReadOnlyList<int>? AllStreams = null);

/// <summary>
/// NHL 19's `.mcd` model container: "RSF\0" header, then byte-reversed tags: GEOM section with VFMT format
/// strings, STRM vertex streams, INDX index buffers, MESH/TLST bindings and a trailing name table. Read-only:
/// a model holds no material data. See docs/RESEARCH.md.
/// </summary>
public sealed class McdFile
{
    private const int StreamHeader = 21;
    private const int IndexHeader = 17;

    private readonly byte[] _data;

    private McdFile(byte[] data, List<McdFormat> formats, List<McdStream> streams, List<McdIndexBuffer> buffers, List<McdShape> shapes, bool paired)
    {
        _data = data;
        Formats = formats;
        Streams = streams;
        IndexBuffers = buffers;
        Shapes = shapes;
        ShapesWerePaired = paired;
    }

    public IReadOnlyList<McdFormat> Formats { get; }
    public IReadOnlyList<McdStream> Streams { get; }
    public IReadOnlyList<McdIndexBuffer> IndexBuffers { get; }
    public IReadOnlyList<McdShape> Shapes { get; }

    /// <summary>True when no binding record validated and streams were paired with buffers by position.</summary>
    public bool ShapesWerePaired { get; }

    public int VertexCount => Streams.Sum(s => s.Count);
    public int TriangleCount => Shapes.Sum(s => s.Triangles);

    public static bool IsMcd(ReadOnlySpan<byte> data) => data.Length >= 16 && data[..4].SequenceEqual("RSF\0"u8);

    public static McdFile Parse(byte[] data)
    {
        if (!IsMcd(data))
            throw new InvalidDataException("Not an RSF model file.");

        var formats = new List<McdFormat>();
        var streams = new List<McdStream>();
        var buffers = new List<McdIndexBuffer>();
        var bindings = new List<int>();
        var names = new List<string>();

        // Tags sit at arbitrary offsets, but each block declares its length, so payloads can be skipped.
        // That is both faster and immune to tag bytes appearing inside vertex data.
        for (var i = 16; i + 8 <= data.Length;)
        {
            if (Tag(data, i, "TMFV") && TryFormat(data, i, out var format, out var formatEnd))
            {
                formats.Add(format);
                i = formatEnd;
            }
            else if (Tag(data, i, "MRTS") && TryStream(data, i, formats, out var stream))
            {
                streams.Add(stream);
                i = stream.DataStart + stream.Count * stream.Stride;
            }
            else if (Tag(data, i, "XDNI") && TryIndexBuffer(data, i, out var buffer))
            {
                buffers.Add(buffer);
                i = buffer.DataStart + buffer.Count * buffer.IndexBytes;
            }
            else if (Tag(data, i, "HSEM") && Tag(data, i + 8, "TSLT"))
            {
                bindings.Add(i);
                i += 16;
            }
            else if (Tag(data, i, "MOEG") && Tag(data, i + 4, "3OEG") && TryName(data, i, out var name))
            {
                names.Add(name);
                i += 12 + name.Length;
            }
            else
            {
                i++;
            }
        }

        var shapes = ResolveShapes(data, bindings, streams, buffers, names)
            .Select(s => s with { Format = FittingFormat(s, formats, streams) }).ToList();
        var paired = false;
        if (shapes.Count == 0 && streams.Count > 0 && streams.Count == buffers.Count)
        {
            // Pairing is only accepted when every index is in range for its partner stream.
            var candidate = new List<McdShape>();
            for (var s = 0; s < streams.Count; s++)
                if (MaxIndex(data, buffers[s]) < streams[s].Count)
                    candidate.Add(new McdShape(s < names.Count ? names[s] : $"shape_{s}", s, s, buffers[s].Count / 3));
            if (candidate.Count == streams.Count)
            {
                shapes = candidate;
                paired = true;
            }
        }
        return new McdFile(data, formats, streams, buffers, shapes, paired);
    }

    private static bool Tag(byte[] data, int at, string tag)
    {
        if (at + 4 > data.Length)
            return false;
        for (var k = 0; k < 4; k++)
            if (data[at + k] != tag[k])
                return false;
        return true;
    }

    private static int I32(byte[] data, int at) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));

    private static bool TryFormat(byte[] data, int at, out McdFormat format, out int end)
    {
        format = null!;
        end = 0;
        var length = I32(data, at + 4);
        if (length is < 8 or > 4096 || at + 8 + length > data.Length)
            return false;
        var text = Encoding.ASCII.GetString(data, at + 8, length).TrimEnd('\0');
        if (!text.StartsWith("p0:", StringComparison.Ordinal))
            return false;
        var elements = new List<McdElement>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split(':');
            if (parts.Length < 3 || !int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var offset) || !TryEncoding(parts[^1], out var count, out var bytes, out var kind))
                return false;
            var slot = int.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedSlot) ? parsedSlot : 0;
            elements.Add(new McdElement(parts[0], offset, parts[^1], count, bytes, kind, parts[^1].EndsWith('n'), slot));
        }
        format = new McdFormat(text, elements);
        end = at + 8 + length;
        return true;
    }

    /// <summary>"3f32", "2f16", "4s16n", "4u8n".</summary>
    private static bool TryEncoding(string encoding, out int count, out int bytes, out char kind)
    {
        count = bytes = 0;
        kind = ' ';
        var body = encoding.TrimEnd('n');
        if (body.Length < 3 || !char.IsDigit(body[0]))
            return false;
        count = body[0] - '0';
        kind = body[1];
        if (count is < 1 or > 4 || kind is not ('f' or 's' or 'u') || !int.TryParse(body[2..], out var bits))
            return false;
        bytes = bits / 8;
        return bytes is 1 or 2 or 4;
    }

    private static bool TryStream(byte[] data, int at, List<McdFormat> formats, out McdStream stream)
    {
        stream = null!;
        if (at + StreamHeader > data.Length || I32(data, at + 5) != 2)
            return false;
        var count = I32(data, at + 9);
        var stride = I32(data, at + 13);
        var formatIndex = I32(data, at + 17);
        if (count is <= 0 or > 20_000_000 || stride is < 4 or > 4096 || at + StreamHeader + (long)count * stride > data.Length)
            return false;
        var format = formatIndex >= 0 && formatIndex < formats.Count && formats[formatIndex].Stride == stride ? formats[formatIndex] : null;
        stream = new McdStream(at + StreamHeader, count, stride, format);
        return true;
    }

    private static bool TryIndexBuffer(byte[] data, int at, out McdIndexBuffer buffer)
    {
        buffer = null!;
        if (at + IndexHeader > data.Length || I32(data, at + 5) != 0)
            return false;
        var count = I32(data, at + 9);
        var format = Encoding.ASCII.GetString(data, at + 13, 4);
        var bytes = format switch { "61DI" => 2, "23DI" => 4, _ => 0 };
        if (bytes == 0 || count <= 0 || count % 3 != 0 || at + IndexHeader + (long)count * bytes > data.Length)
            return false;
        buffer = new McdIndexBuffer(at + IndexHeader, count, bytes);
        return true;
    }

    private static bool TryName(byte[] data, int at, out string name)
    {
        name = "";
        if (at + 12 > data.Length)
            return false;
        var length = I32(data, at + 8);
        if (length is <= 0 or > 512 || at + 12 + length > data.Length)
            return false;
        name = Encoding.Latin1.GetString(data, at + 12, length).TrimEnd('\0');
        return name.Length > 0;
    }

    /// <summary>
    /// The format that lays out a shape's streams: the one its binding names when that fits slot by slot, otherwise the
    /// only one that does. Helmet visors name a format that does not fit, while another in the file fits exactly.
    /// </summary>
    private static int FittingFormat(McdShape shape, List<McdFormat> formats, List<McdStream> streams)
    {
        var ids = shape.AllStreams ?? [shape.Stream];
        bool Fits(McdFormat format) =>
            format.Elements.All(e => e.Slot < ids.Count)
            && Enumerable.Range(0, ids.Count).All(slot => format.Elements.Where(e => e.Slot == slot).Sum(e => e.Size) == streams[ids[slot]].Stride);
        if (shape.Format >= 0 && shape.Format < formats.Count && Fits(formats[shape.Format]))
            return shape.Format;
        var fitting = Enumerable.Range(0, formats.Count).Where(i => Fits(formats[i])).ToList();
        return fitting.Count == 1 ? fitting[0] : shape.Format;
    }

    private static int MaxIndex(byte[] data, McdIndexBuffer buffer)
    {
        var max = 0;
        for (var i = 0; i < buffer.Count; i++)
        {
            var at = buffer.DataStart + i * buffer.IndexBytes;
            var value = buffer.IndexBytes == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) : I32(data, at);
            max = Math.Max(max, value);
        }
        return max;
    }

    /// <summary>
    /// A binding record is "MESH" u32 "TLST" u32 then fields: 0, triangles, format, 1, stream count, streams,
    /// index buffer. A record is kept only if its triangle count and indices check out against the buffers.
    /// </summary>
    private static List<McdShape> ResolveShapes(byte[] data, List<int> bindings, List<McdStream> streams, List<McdIndexBuffer> buffers, List<string> names)
    {
        var shapes = new List<McdShape>();
        foreach (var record in bindings)
        {
            var start = record + 16;
            if (start + 28 > data.Length)
                continue;
            var triangles = I32(data, start + 4);
            var streamCount = I32(data, start + 16);
            if (streamCount is < 1 or > 4 || start + 24 + streamCount * 4 > data.Length)
                continue;
            var stream = I32(data, start + 20);
            var buffer = I32(data, start + 20 + streamCount * 4);
            if (stream < 0 || stream >= streams.Count || buffer < 0 || buffer >= buffers.Count)
                continue;
            if (buffers[buffer].Count / 3 != triangles || MaxIndex(data, buffers[buffer]) >= streams[stream].Count)
                continue;
            var all = new List<int>(streamCount);
            for (var s = 0; s < streamCount; s++)
            {
                var id = I32(data, start + 20 + s * 4);
                if (id >= 0 && id < streams.Count && streams[id].Count == streams[stream].Count)
                    all.Add(id);
            }
            var index = shapes.Count;
            shapes.Add(new McdShape(index < names.Count ? names[index] : $"shape_{index}", stream, buffer, triangles, I32(data, start + 8), all.Count == streamCount ? all : null));
        }
        return shapes;
    }

    public Vector3[] ReadPositions(McdStream stream)
    {
        var output = new Vector3[stream.Count];
        var element = stream.Format?.Find('p');
        Span<float> v = stackalloc float[4];
        for (var i = 0; i < stream.Count; i++)
        {
            var vertex = _data.AsSpan(stream.DataStart + i * stream.Stride, stream.Stride);
            if (element != null)
            {
                element.Read(vertex, v);
                output[i] = new Vector3(v[0], v[1], v[2]);
            }
            else
            {
                output[i] = new Vector3(
                    BinaryPrimitives.ReadSingleLittleEndian(vertex),
                    BinaryPrimitives.ReadSingleLittleEndian(vertex[4..]),
                    BinaryPrimitives.ReadSingleLittleEndian(vertex[8..]));
            }
        }
        return output;
    }

    public Vector2[]? ReadUvs(McdStream stream, int set = 0)
    {
        if (stream.Format?.Find('t', set) is not { } element)
            return null;
        var output = new Vector2[stream.Count];
        Span<float> v = stackalloc float[4];
        for (var i = 0; i < stream.Count; i++)
        {
            element.Read(_data.AsSpan(stream.DataStart + i * stream.Stride, stream.Stride), v);
            output[i] = new Vector2(v[0], v[1]);
        }
        return output;
    }

    public Vector3[]? ReadNormals(McdStream stream)
    {
        if (stream.Format?.Find('n') is not { } element)
            return null;
        var output = new Vector3[stream.Count];
        Span<float> v = stackalloc float[4];
        for (var i = 0; i < stream.Count; i++)
        {
            element.Read(_data.AsSpan(stream.DataStart + i * stream.Stride, stream.Stride), v);
            output[i] = new Vector3(v[0], v[1], v[2]);
        }
        return output;
    }

    /// <summary>Where a shape keeps a field, using the format its binding names. Null if that format does not describe it.</summary>
    private (McdElement Element, McdStream Stream)? Locate(McdShape shape, char channel, int index)
    {
        if (shape.Format < 0 || shape.Format >= Formats.Count)
            return null;
        var element = Formats[shape.Format].Find(channel, index);
        var streams = shape.AllStreams ?? [shape.Stream];
        if (element == null || element.Slot >= streams.Count)
            return null;
        var stream = Streams[streams[element.Slot]];
        return element.Offset + element.Size <= stream.Stride ? (element, stream) : null;
    }

    private Vector4[]? ReadField(McdShape shape, char channel, int index)
    {
        if (Locate(shape, channel, index) is not { } found)
            return null;
        var output = new Vector4[found.Stream.Count];
        Span<float> v = stackalloc float[4];
        for (var i = 0; i < output.Length; i++)
        {
            found.Element.Read(_data.AsSpan(found.Stream.DataStart + i * found.Stream.Stride, found.Stream.Stride), v);
            output[i] = new Vector4(v[0], v[1], v[2], v[3]);
        }
        return output;
    }

    /// <summary>A shape's UVs, from whichever of its streams holds them.</summary>
    public Vector2[]? ReadUvs(McdShape shape, int set = 0) =>
        ReadField(shape, 't', set)?.Select(v => new Vector2(v.X, v.Y)).ToArray() ?? ReadUvs(Streams[shape.Stream], set);

    /// <summary>A shape's normals, from whichever of its streams holds them.</summary>
    public Vector3[]? ReadNormals(McdShape shape) =>
        ReadField(shape, 'n', 0)?.Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray() ?? ReadNormals(Streams[shape.Stream]);

    public int[] ReadIndices(McdIndexBuffer buffer)
    {
        var output = new int[buffer.Count];
        for (var i = 0; i < buffer.Count; i++)
        {
            var at = buffer.DataStart + i * buffer.IndexBytes;
            output[i] = buffer.IndexBytes == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(at)) : I32(_data, at);
        }
        return output;
    }

    /// <summary>Writes the shapes as Wavefront OBJ, with UVs and normals where the format has them.</summary>
    public void WriteObj(TextWriter output, string header)
    {
        var c = CultureInfo.InvariantCulture;
        output.WriteLine($"# {header}");
        var baseVertex = 1;
        var baseUv = 1;
        var baseNormal = 1;
        foreach (var shape in Shapes)
        {
            var stream = Streams[shape.Stream];
            var positions = ReadPositions(stream);
            var uvs = ReadUvs(shape);
            var normals = ReadNormals(shape);
            var indices = ReadIndices(IndexBuffers[shape.IndexBuffer]);

            output.WriteLine($"o {shape.Name.Replace(' ', '_')}");
            foreach (var p in positions)
                output.WriteLine(string.Create(c, $"v {p.X:R} {p.Y:R} {p.Z:R}"));
            if (uvs != null)
                foreach (var t in uvs)
                    output.WriteLine(string.Create(c, $"vt {t.X:R} {1 - t.Y:R}"));
            if (normals != null)
                foreach (var n in normals)
                    output.WriteLine(string.Create(c, $"vn {n.X:R} {n.Y:R} {n.Z:R}"));

            string Corner(int i)
            {
                var v = baseVertex + i;
                if (uvs != null && normals != null) return $"{v}/{baseUv + i}/{baseNormal + i}";
                if (uvs != null) return $"{v}/{baseUv + i}";
                return normals != null ? $"{v}//{baseNormal + i}" : $"{v}";
            }

            for (var i = 0; i + 2 < indices.Length; i += 3)
                output.WriteLine($"f {Corner(indices[i])} {Corner(indices[i + 1])} {Corner(indices[i + 2])}");

            baseVertex += positions.Length;
            baseUv += uvs?.Length ?? 0;
            baseNormal += normals?.Length ?? 0;
        }
    }
}
