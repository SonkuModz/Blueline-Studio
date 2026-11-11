using System.Buffers.Binary;
using System.IO.Compression;

namespace Blueline.Formats.Eb;

/// <summary>
/// EA's "chunkzip" wrapper. Shape, as stored by NHL 19 (all big-endian):
/// <code>
/// "chunkzip" u32 version=2  u32 fullSize  u32 chunkSize=0x20000  u32 chunkCount  u32 alignment=0x10  12 zero bytes
/// each chunk: padded so position % 16 == 8, u32 compressedSize, u32 flag (1 deflate, 4 stored), data
/// </code>
/// An older shape with no version field is also read, since the quickbms reference documents it.
/// </summary>
public static class Chunkzip
{
    private const int DefaultChunkSize = 0x20000;
    private const uint FlagDeflate = 1;
    private const uint FlagStored = 4;
    private static ReadOnlySpan<byte> Tag => "chunkzip"u8;

    public static bool IsWrapped(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(Tag);

    public static byte[] Unwrap(byte[] data)
    {
        if (!IsWrapped(data))
            return data;

        var header = ReadHeader(data);
        var output = new byte[header.FullSize];
        var written = 0;
        var position = header.DataStart;
        while (written < output.Length)
        {
            if (header.Aligned)
                position = AlignChunk(position);

            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
            position += 4;
            var flag = FlagDeflate;
            if (header.Aligned)
            {
                flag = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
                position += 4;
            }

            var wanted = Math.Min(header.ChunkSize, output.Length - written);
            if (flag == FlagStored)
            {
                data.AsSpan(position, size).CopyTo(output.AsSpan(written));
                written += size;
            }
            else
            {
                using var deflate = new DeflateStream(new MemoryStream(data, position, size), CompressionMode.Decompress);
                deflate.ReadExactly(output, written, wanted);
                written += wanted;
            }
            position += size;
        }
        return output;
    }

    /// <summary>Decodes only enough of the first chunk to return <paramref name="length"/> bytes.</summary>
    public static byte[] UnwrapHead(byte[] data, int length)
    {
        var header = ReadHeader(data);
        var position = header.Aligned ? AlignChunk(header.DataStart) : header.DataStart;
        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
        position += 4;
        var flag = FlagDeflate;
        if (header.Aligned)
        {
            flag = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
            position += 4;
        }

        var wanted = Math.Min(length, header.FullSize);
        var available = Math.Min(size, data.Length - position);
        if (flag == FlagStored)
            return data.AsSpan(position, Math.Min(wanted, available)).ToArray();

        var output = new byte[wanted];
        using var deflate = new DeflateStream(new MemoryStream(data, position, available), CompressionMode.Decompress);
        var total = 0;
        while (total < wanted)
        {
            var read = deflate.Read(output, total, wanted - total);
            if (read == 0)
                break;
            total += read;
        }
        return output[..total];
    }

    public static byte[] Wrap(ReadOnlySpan<byte> content, CompressionLevel level = CompressionLevel.Optimal)
    {
        var chunkCount = (content.Length + DefaultChunkSize - 1) / DefaultChunkSize;
        using var output = new MemoryStream(content.Length / 2 + 64);

        Span<byte> field = stackalloc byte[4];
        void WriteField(MemoryStream s, uint value, Span<byte> buffer)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            s.Write(buffer);
        }

        output.Write(Tag);
        WriteField(output, 2, field);
        WriteField(output, (uint)content.Length, field);
        WriteField(output, DefaultChunkSize, field);
        WriteField(output, (uint)chunkCount, field);
        WriteField(output, 0x10, field);
        output.Write(new byte[12]);

        for (var i = 0; i < chunkCount; i++)
        {
            var chunk = content.Slice(i * DefaultChunkSize, Math.Min(DefaultChunkSize, content.Length - i * DefaultChunkSize));
            using var packed = new MemoryStream(chunk.Length);
            using (var deflate = new DeflateStream(packed, level, leaveOpen: true))
                deflate.Write(chunk);

            output.Position = output.Length;
            while (output.Length % 16 != 8)
                output.WriteByte(0);

            var stored = packed.Length >= chunk.Length;
            WriteField(output, (uint)(stored ? chunk.Length : packed.Length), field);
            WriteField(output, stored ? FlagStored : FlagDeflate, field);
            if (stored)
                output.Write(chunk);
            else
                output.Write(packed.GetBuffer(), 0, (int)packed.Length);
        }
        return output.ToArray();
    }

    private static int AlignChunk(int position)
    {
        position = (position + 7) & ~7;
        while (position % 16 != 8)
            position += 8;
        return position;
    }

    private readonly record struct Header(int FullSize, int ChunkSize, int DataStart, bool Aligned);

    private static Header ReadHeader(byte[] data)
    {
        var probe = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
        if (probe <= 2)
        {
            var full = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(12));
            var chunk = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16));
            return new Header(full, chunk, 24 + 16, true);
        }
        return new Header((int)probe, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(12)), 16, false);
    }
}
