using System.Buffers.Binary;

namespace Blueline.Formats.Audio;

/// <summary>
/// EA Opus (codec 0xC) sample chains, converted to and from plain Ogg Opus so FFmpeg's libopus can decode and encode
/// them. A chain is a 0x48 block holding the 8-byte EA header, 0x44 blocks of [u32 BE samples][u16 BE length + Opus
/// packet]..., and a 0x45 block padded so the chain ends on 4 bytes. Checked on all 405 EA Opus horns in hornbank.sbr:
/// 20 ms packets, 11 to a block (a short lone last packet joins the block before), the first block declaring 312 samples fewer (the encoder's pre-skip), and the block
/// counts adding up to the header's total, so the last block is trimmed too.
/// </summary>
public static class EaOpus
{
    public const int Codec = 0xC;
    public const int SampleRate = 48000;
    private const int PacketsPerBlock = 11;
    private const byte HeaderBlock = 0x48, DataBlock = 0x44, EndBlock = 0x45;

    public sealed record Chain(byte[] Header, int Channels, int TotalSamples, IReadOnlyList<byte[]> Packets, int PreSkip);

    public static bool IsEaOpus(ReadOnlySpan<byte> chain) =>
        chain.Length >= 12 && chain[0] == HeaderBlock && (chain[4] & 0x0F) == Codec;

    public static Chain Read(ReadOnlySpan<byte> chain)
    {
        if (!IsEaOpus(chain))
            throw new InvalidDataException("This sample is not EA Opus.");
        var header = chain.Slice(4, 8).ToArray();
        var channels = (header[1] >> 2) + 1;
        var total = (int)(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)) & 0x1FFFFFFF);
        var packets = new List<byte[]>();
        int? preSkip = null;
        var position = BlockSize(chain, 0);
        while (position + 4 <= chain.Length)
        {
            var type = chain[position];
            var size = BlockSize(chain, position);
            if (size < 4 || position + size > chain.Length)
                throw new InvalidDataException($"A block at {position} runs past the sample.");
            if (type == EndBlock)
                break;
            if (type != DataBlock)
                throw new InvalidDataException($"Unexpected block type 0x{type:X2} at {position}.");
            var declared = (int)BinaryPrimitives.ReadUInt32BigEndian(chain.Slice(position + 4));
            var count = 0;
            for (var at = position + 8; at < position + size;)
            {
                var length = BinaryPrimitives.ReadUInt16BigEndian(chain.Slice(at));
                if (at + 2 + length > position + size)
                    throw new InvalidDataException($"A packet at {at} runs past its block.");
                packets.Add(chain.Slice(at + 2, length).ToArray());
                at += 2 + length;
                count++;
            }
            preSkip ??= Math.Max(0, count * SamplesPerPacket(packets[^count]) - declared);
            position += size;
        }
        return new Chain(header, channels, total, packets, preSkip ?? 0);
    }

    /// <summary>The chain as an Ogg Opus file, with its pre-skip and its trimmed end, for any Opus decoder.</summary>
    public static byte[] ToOgg(ReadOnlySpan<byte> sample)
    {
        var chain = Read(sample);
        var writer = new OggWriter(0x426C6E65);
        var head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = (byte)chain.Channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), (ushort)chain.PreSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), SampleRate);
        writer.Page([head], 0, 0x02);
        writer.Page([[.. "OpusTags"u8, 8, 0, 0, 0, .. "Blueline"u8, 0, 0, 0, 0]], 0, 0);

        long granule = 0;
        var end = (long)chain.TotalSamples + chain.PreSkip;
        for (var i = 0; i < chain.Packets.Count; i += 32)
        {
            var page = chain.Packets.Skip(i).Take(32).ToList();
            granule += page.Sum(SamplesPerPacket);
            var last = i + 32 >= chain.Packets.Count;
            writer.Page(page, last ? Math.Min(granule, end) : granule, last ? (byte)0x04 : (byte)0);
        }
        return writer.ToArray();
    }

    /// <summary>
    /// Builds a chain from an Ogg Opus file (libopus, 48 kHz). <paramref name="donorHeader"/> is the 8-byte header of
    /// the sample being replaced; its version, codec, rate and type bits are kept, and channels and length are set.
    /// </summary>
    public static byte[] FromOgg(ReadOnlySpan<byte> ogg, ReadOnlySpan<byte> donorHeader)
    {
        var (packets, granule) = OggReader.Packets(ogg);
        if (packets.Count < 3 || !packets[0].AsSpan().StartsWith("OpusHead"u8))
            throw new InvalidDataException("This is not an Ogg Opus file.");
        var head = packets[0];
        var channels = head[9];
        var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));
        if (head[18] != 0 || channels is < 1 or > 2)
            throw new InvalidDataException("Only mono or stereo Opus with mapping family 0 can go into an EA sample.");
        var audio = packets.Skip(2).ToList();
        foreach (var packet in audio)
            if (SamplesPerPacket(packet) != 960)
                throw new InvalidDataException("Every Opus packet must be 20 ms; encode with -frame_duration 20.");
        var total = (int)Math.Min(granule - preSkip, (long)audio.Count * 960 - preSkip);
        if (total <= 0)
            throw new InvalidDataException("The Opus file holds no audio.");
        if (donorHeader.Length != 8 || (donorHeader[0] & 0x0F) != Codec)
            throw new ArgumentException("The donor header must be an 8-byte EA Opus header.", nameof(donorHeader));

        using var output = new MemoryStream();
        var header = donorHeader.ToArray();
        header[1] = (byte)((header[1] & 0x03) | (channels - 1) << 2);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)(header[4] & 0xE0) << 24 | (uint)total);
        output.Write([HeaderBlock, 0, 0, 12]);
        output.Write(header);

        var remaining = total;
        for (var i = 0; i < audio.Count;)
        {
            // A single packet left over joins the block before it when it would hold fewer real samples than the
            // pre-skip, as in all 53 stock horns that end with one packet (14 merged, 39 not).
            var take = PacketsPerBlock;
            if (audio.Count - i == PacketsPerBlock + 1 && remaining - (PacketsPerBlock * 960 - (i == 0 ? preSkip : 0)) < preSkip)
                take = PacketsPerBlock + 1;
            var block = audio.Skip(i).Take(take).ToList();
            var declared = block.Count * 960 - (i == 0 ? preSkip : 0);
            declared = Math.Clamp(declared, 0, remaining);
            remaining -= declared;
            var size = 8 + block.Sum(p => 2 + p.Length);
            if (size > 0xFFFFFF)
                throw new InvalidDataException("An Opus packet is too large for an EA block.");
            Span<byte> prefix = stackalloc byte[8];
            prefix[0] = DataBlock;
            prefix[1] = (byte)(size >> 16);
            prefix[2] = (byte)(size >> 8);
            prefix[3] = (byte)size;
            BinaryPrimitives.WriteUInt32BigEndian(prefix[4..], (uint)declared);
            output.Write(prefix);
            foreach (var packet in block)
            {
                output.Write([(byte)(packet.Length >> 8), (byte)packet.Length]);
                output.Write(packet);
            }
            i += block.Count;
        }
        var endSize = 4 + (int)((4 - (output.Length + 4) % 4) % 4);
        var end = new byte[endSize];
        end[0] = EndBlock;
        end[3] = (byte)endSize;
        output.Write(end);
        return output.ToArray();
    }

    /// <summary>Samples in one Opus packet at 48 kHz, from its TOC byte (RFC 6716 section 3.1).</summary>
    public static int SamplesPerPacket(byte[] packet)
    {
        if (packet.Length == 0)
            return 0;
        var config = packet[0] >> 3;
        var frame = config switch
        {
            < 12 => new[] { 480, 960, 1920, 2880 }[config & 3],
            < 16 => (config & 1) == 0 ? 480 : 960,
            _ => new[] { 120, 240, 480, 960 }[config & 3],
        };
        var frames = (packet[0] & 3) switch
        {
            0 => 1,
            1 or 2 => 2,
            _ => packet.Length > 1 ? packet[1] & 0x3F : 0,
        };
        return frame * frames;
    }

    private static int BlockSize(ReadOnlySpan<byte> chain, int at) => chain[at + 1] << 16 | chain[at + 2] << 8 | chain[at + 3];

    private sealed class OggWriter(uint serial)
    {
        private readonly MemoryStream _stream = new();
        private uint _sequence;

        public void Page(IReadOnlyList<byte[]> packets, long granule, byte flags)
        {
            var lacing = new List<byte>();
            foreach (var packet in packets)
            {
                for (var n = packet.Length; ; n -= 255)
                {
                    lacing.Add((byte)Math.Min(n, 255));
                    if (n < 255)
                        break;
                }
            }
            if (lacing.Count > 255)
                throw new InvalidOperationException("Too many packets for one Ogg page.");
            var page = new byte[27 + lacing.Count + packets.Sum(p => p.Length)];
            "OggS"u8.CopyTo(page);
            page[5] = flags;
            BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), _sequence++);
            page[26] = (byte)lacing.Count;
            lacing.CopyTo(page, 27);
            var at = 27 + lacing.Count;
            foreach (var packet in packets)
            {
                packet.CopyTo(page, at);
                at += packet.Length;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), OggCrc(page));
            _stream.Write(page);
        }

        public byte[] ToArray() => _stream.ToArray();
    }

    private static class OggReader
    {
        public static (List<byte[]> Packets, long LastGranule) Packets(ReadOnlySpan<byte> ogg)
        {
            var packets = new List<byte[]>();
            var partial = new List<byte>();
            long granule = 0;
            var at = 0;
            while (at + 27 <= ogg.Length)
            {
                if (!ogg.Slice(at, 4).SequenceEqual("OggS"u8))
                    throw new InvalidDataException($"No Ogg page at {at}.");
                var segments = ogg[at + 26];
                var pageGranule = BinaryPrimitives.ReadInt64LittleEndian(ogg.Slice(at + 6));
                if (pageGranule >= 0)
                    granule = pageGranule;
                var data = at + 27 + segments;
                for (var s = 0; s < segments; s++)
                {
                    var length = ogg[at + 27 + s];
                    partial.AddRange(ogg.Slice(data, length));
                    data += length;
                    if (length < 255)
                    {
                        packets.Add([.. partial]);
                        partial.Clear();
                    }
                }
                at = data;
            }
            return (packets, granule);
        }
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        var c = (uint)i << 24;
        for (var k = 0; k < 8; k++)
            c = (c & 0x80000000) != 0 ? c << 1 ^ 0x04C11DB7 : c << 1;
        return c;
    }).ToArray();

    private static uint OggCrc(ReadOnlySpan<byte> page)
    {
        uint crc = 0;
        foreach (var b in page)
            crc = crc << 8 ^ CrcTable[(crc >> 24 ^ b) & 0xFF];
        return crc;
    }
}
