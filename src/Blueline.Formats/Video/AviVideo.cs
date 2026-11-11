using System.Buffers.Binary;
using System.Text;

namespace Blueline.Formats.Video;

/// <summary>The compressed frames of an AVI's first video stream, in order.</summary>
public sealed record AviVideo(string Codec, int Width, int Height, IReadOnlyList<byte[]> Frames)
{
    private const int ChunkHeader = 8;
    private const int ListHeader = 12;

    public static AviVideo Read(byte[] data)
    {
        if (data.Length < ListHeader || Tag(data, 0) != "RIFF" || Tag(data, 8) != "AVI ")
            throw new InvalidDataException("This is not an AVI file.");

        string? codec = null;
        int width = 0, height = 0, stream = -1, streams = 0;
        var frames = new List<byte[]>();

        void Walk(int start, int end)
        {
            var strh = -1;
            var position = start;
            while (position + ChunkHeader <= end)
            {
                var id = Tag(data, position);
                var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4, 4));
                var body = position + ChunkHeader;
                if (size < 0 || body + size > data.Length)
                    throw new InvalidDataException($"Bad AVI chunk at offset {position}.");

                if (id is "RIFF" or "LIST")
                {
                    if (Tag(data, body) == "strl")
                        streams++;
                    Walk(body + 4, body + size);
                }
                else if (id == "strh" && Tag(data, body) == "vids" && codec == null)
                {
                    strh = body;
                }
                else if (id == "strf" && strh >= 0)
                {
                    stream = streams - 1;
                    width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(body + 4));
                    height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(body + 8)));
                    codec = Tag(data, body + 16);
                    strh = -1;
                }
                else if (codec != null && id.Length == 4 && id[2..] is "dc" or "db" && int.TryParse(id[..2], out var number) && number == stream)
                {
                    frames.Add(data.AsSpan(body, size).ToArray());
                }
                position = body + size + (size & 1);
            }
        }

        Walk(0, data.Length);
        if (codec == null)
            throw new InvalidDataException("This AVI has no video stream.");
        return new AviVideo(codec, width, height, frames);
    }

    private static string Tag(byte[] data, int offset) => Encoding.ASCII.GetString(data, offset, 4);
}
