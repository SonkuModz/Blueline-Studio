namespace Blueline.Formats.Tdb;

/// <summary>
/// MSB-first CRC-32 (polynomial 0x04C11DB7, init 0xFFFFFFFF, no final XOR, no reflection).
/// Not the zlib CRC. Stored little-endian. Verified on NHL 19's databases by the test suite.
/// </summary>
public static class TdbCrc
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var c = (uint)i << 24;
            for (var bit = 0; bit < 8; bit++)
                c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in bytes)
            c = (c << 8) ^ Table[((c >> 24) ^ b) & 0xFF];
        return c;
    }
}
