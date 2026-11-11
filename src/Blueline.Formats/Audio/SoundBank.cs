namespace Blueline.Formats.Audio;

/// <summary>One sample: a block chain from its 0x48 header through its 0x45 terminator.</summary>
public sealed record SoundSample(int Index, long Offset, int Length, byte[] Header)
{
    public int Codec => Header[0] & 0x0F;
    public int SampleRate => Header[2] << 8 | Header[3];
    public bool IsLooping => (Header[4] & 0x20) != 0;
    public int PcmSamples => Header[5] << 16 | Header[6] << 8 | Header[7];
    public double Seconds => SampleRate > 0 ? (double)PcmSamples / SampleRate : 0;

    public string CodecName => Codec switch
    {
        0x3 => "EA-XMA",
        0x5 => "EALayer3",
        0x9 => "EASpeex",
        0xC => "EAOpus",
        0xD => "EA-ATRAC9",
        0xE => "MultiStreamOpus",
        _ => $"Unknown (0x{Codec:X})",
    };
}

/// <summary>
/// Enumerates samples in an `.sbs` (or a self-contained `.sbr`) by walking its block chain. Blocks are
/// [type:u8][size:u24 BE, including the 4-byte header]. Reads through a callback in 1 MB windows,
/// so an 800 MB bank needs no 800 MB allocation.
/// </summary>
public static class SoundBank
{
    private const byte Header = 0x48, Data = 0x44, End = 0x45;
    private const int Window = 1 << 20;
    private const int SampleGrid = 0x20;
    private const int ScanLimit = 1 << 22;

    public delegate int Reader(long offset, Span<byte> buffer);

    public static IReadOnlyList<SoundSample> Scan(long length, Reader read, CancellationToken cancel = default)
    {
        var samples = new List<SoundSample>();
        var buffer = new byte[Window];
        long windowStart = -1;
        var windowLength = 0;

        bool Ensure(long offset, int count)
        {
            if (offset + count > length)
                return false;
            if (windowStart < 0 || offset < windowStart || offset + count > windowStart + windowLength)
            {
                windowStart = offset;
                windowLength = read(offset, buffer);
            }
            return offset + count <= windowStart + windowLength;
        }

        bool IsHeader(long offset)
        {
            if (!Ensure(offset, 16))
                return false;
            var p = (int)(offset - windowStart);
            return buffer[p] == Header && buffer[p + 1] == 0 && buffer[p + 2] == 0 && buffer[p + 3] == 12
                && (buffer[p + 12] == Data || buffer[p + 12] == End);
        }

        var position = FindFirst(length, IsHeader);
        while (position >= 0 && position < length)
        {
            cancel.ThrowIfCancellationRequested();
            if (!IsHeader(position))
            {
                position = NextHeader(position, length, IsHeader);
                continue;
            }

            var start = position;
            Ensure(start, 12);
            var head = buffer.AsSpan((int)(start - windowStart) + 4, 8).ToArray();

            var ended = false;
            while (position + 4 <= length)
            {
                if (!Ensure(position, 4))
                    break;
                var p = (int)(position - windowStart);
                var type = buffer[p];
                var size = buffer[p + 1] << 16 | buffer[p + 2] << 8 | buffer[p + 3];
                if (size < 4 || type is not (Header or Data or End))
                    break;
                position += size;
                if (type == End)
                {
                    ended = true;
                    break;
                }
            }
            if (!ended)
                break;

            samples.Add(new SoundSample(samples.Count, start, (int)(position - start), head));

            // Self-contained banks pack samples back to back; streamed banks pad to 0x20 with zeros, and a
            // shorter replacement leaves zeros too. Skip zeros byte by byte, since an edited packed bank's
            // next sample need not sit on the grid; fall back to a grid search if what follows isn't a header.
            while (position < length && Ensure(position, 1) && buffer[(int)(position - windowStart)] == 0)
                position++;
            if (position < length && !IsHeader(position))
                position = NextHeader(position, length, IsHeader);
        }
        return samples;
    }

    /// <summary>
    /// Replaces a sample inside a bank in place, keeping every other sample's offset. The replacement
    /// must fit the original's span; the remainder is zero-filled, which the block walk skips. Growing a
    /// sample is refused because how the game seeks into a bank has not been established.
    /// </summary>
    public static byte[] ReplaceSample(byte[] bank, SoundSample target, byte[] replacement)
    {
        if (replacement.Length == 0 || replacement[0] != Header)
            throw new InvalidDataException("The replacement is not a sample block chain.");
        if (replacement.Length > target.Length)
            throw new InvalidOperationException($"The replacement is {replacement.Length:N0} bytes; this slot holds {target.Length:N0}.");
        if (target.Offset + target.Length > bank.Length)
            throw new ArgumentOutOfRangeException(nameof(target));

        var output = (byte[])bank.Clone();
        var slot = output.AsSpan((int)target.Offset, target.Length);
        slot.Clear();
        replacement.CopyTo(slot);
        return output;
    }

    private static long FindFirst(long length, Func<long, bool> isHeader)
    {
        var limit = Math.Min(length - 16, ScanLimit);
        for (long offset = 0; offset < limit; offset++)
            if (isHeader(offset))
                return offset;
        return -1;
    }

    private static long NextHeader(long position, long length, Func<long, bool> isHeader)
    {
        var offset = (position + SampleGrid - 1) / SampleGrid * SampleGrid;
        var limit = Math.Min(length - 16, position + ScanLimit);
        for (; offset < limit; offset += SampleGrid)
            if (isHeader(offset))
                return offset;
        return -1;
    }
}
