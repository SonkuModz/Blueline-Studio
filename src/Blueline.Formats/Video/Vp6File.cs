using System.Buffers.Binary;
using System.Text;

namespace Blueline.Formats.Video;

/// <summary>
/// EA's `.vp6` container: chunks of a 4-byte tag and a little-endian size that counts the 8-byte header.
/// Picture chunks are `MVhd`, `MV0K` (key frame) and `MV0F`. Files with transparency hold a second stream of the
/// same shape in `ALhd`, `AL0K` and `AL0F`. See RESEARCH section 6.8.
/// </summary>
public static class Vp6File
{
    private const int ChunkHeader = 8;
    private const int HeaderChunksToSearch = 4;
    private const int ChunkAlignment = 4;

    public static bool IsVp6(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual("MVhd"u8);

    /// <summary>True when the file has a matte stream. Its header sits with the picture's, at the start of the file.</summary>
    public static bool HasMatte(ReadOnlySpan<byte> data) => HeaderHas(data, "ALhd"u8);

    /// <summary>True when the file carries EA sound chunks, which FFmpeg does not read from these files.</summary>
    public static bool HasSound(ReadOnlySpan<byte> data) => HeaderHas(data, "SCHl"u8);

    private static bool HeaderHas(ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag)
    {
        if (!IsVp6(data))
            return false;
        var position = 0;
        for (var i = 0; i < HeaderChunksToSearch && position + ChunkHeader <= data.Length; i++)
        {
            if (data.Slice(position, 4).SequenceEqual(tag))
                return true;
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position + 4, 4));
            if (size < ChunkHeader)
                return false;
            position += size;
        }
        return false;
    }

    /// <summary>
    /// A copy of the game's file with new frames, everything else left in place: headers, sound and, unless a new one
    /// is given, the matte. EA pads chunks to 4 bytes. The frames must match the original's count and size.
    /// </summary>
    public static byte[] ReplaceFrames(byte[] original, AviVideo picture, AviVideo? matte = null)
    {
        if (!IsVp6(original))
            throw new InvalidDataException("This is not a VP6 file.");
        if (matte != null && !HasMatte(original))
            throw new InvalidDataException("This video has no matte to replace.");

        var (width, height) = (BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(12)), BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(14)));
        var chunks = Chunks(original).ToList();
        Check(picture, "picture", chunks.Count(c => c.Tag is "MV0K" or "MV0F"), width, height);
        if (matte != null)
            Check(matte, "matte", chunks.Count(c => c.Tag is "AL0K" or "AL0F"), width, height);

        using var output = new MemoryStream(original.Length);
        var (pictureIndex, matteIndex) = (0, 0);
        foreach (var chunk in chunks)
        {
            if (chunk.Tag is "MV0K" or "MV0F")
                WriteFrame(output, "MV", picture.Frames[pictureIndex++]);
            else if (matte != null && chunk.Tag is "AL0K" or "AL0F")
                WriteFrame(output, "AL", matte.Frames[matteIndex++]);
            else
                output.Write(original, chunk.Offset, chunk.Size);
        }
        return output.ToArray();
    }

    private static void Check(AviVideo video, string role, int frames, int width, int height)
    {
        if (!video.Codec.Equals("VP60", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The {role} AVI is {video.Codec}. Encode it as VP60 (Simple Profile), which is what the game's files use.");
        if (video.Width != width || video.Height != height)
            throw new InvalidDataException($"The {role} AVI is {video.Width}x{video.Height}. This video is {width}x{height}.");
        if (video.Frames.Count != frames)
            throw new InvalidDataException($"The {role} AVI has {video.Frames.Count} frames. This video has {frames}.");
        if (video.Frames.Any(f => f.Length == 0))
            throw new InvalidDataException($"The {role} AVI has empty frames. Turn off frame dropping when encoding it.");
        if (!IsKeyFrame(video.Frames[0]))
            throw new InvalidDataException($"The {role} AVI does not start on a key frame.");
    }

    /// <summary>In a VP6 frame the top bit of the first byte is clear on key frames.</summary>
    private static bool IsKeyFrame(byte[] frame) => (frame[0] & 0x80) == 0;

    private static void WriteFrame(Stream output, string prefix, byte[] frame)
    {
        var padded = (frame.Length + ChunkHeader + ChunkAlignment - 1) / ChunkAlignment * ChunkAlignment;
        Span<byte> header = stackalloc byte[ChunkHeader];
        Encoding.ASCII.GetBytes(prefix + (IsKeyFrame(frame) ? "0K" : "0F"), header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)padded);
        output.Write(header);
        output.Write(frame);
        output.Write(new byte[padded - ChunkHeader - frame.Length]);
    }

    private static IEnumerable<(string Tag, int Offset, int Size)> Chunks(byte[] data)
    {
        var position = 0;
        while (position + ChunkHeader <= data.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4, 4));
            if (size < ChunkHeader || position + size > data.Length)
                throw new InvalidDataException($"Bad VP6 chunk at offset {position}.");
            yield return (Encoding.ASCII.GetString(data, position, 4), position, size);
            position += size;
        }
    }

    /// <summary>
    /// A copy of the file holding only the matte, relabelled as the picture stream, so a VP6 decoder that reads just
    /// the picture stream shows the matte.
    /// </summary>
    public static byte[] MatteAsVideo(byte[] data)
    {
        if (!HasMatte(data))
            throw new InvalidDataException("This VP6 file has no matte.");

        using var output = new MemoryStream(data.Length / 2);
        var position = 0;
        while (position + ChunkHeader <= data.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4, 4));
            if (size < ChunkHeader || position + size > data.Length)
                throw new InvalidDataException($"Bad VP6 chunk at offset {position}.");

            var tag = Encoding.ASCII.GetString(data, position, 4);
            if (tag.StartsWith("AL", StringComparison.Ordinal))
            {
                output.Write("MV"u8);
                output.Write(data, position + 2, size - 2);
            }
            position += size;
        }
        return output.ToArray();
    }
}
