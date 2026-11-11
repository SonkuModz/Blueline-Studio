namespace Blueline.Formats.Textures;

/// <summary>Standard reflected CRC-32, used by PNG chunks.</summary>
internal static class PngCrc
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    public static uint Compute(byte[] first, byte[] second)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in first)
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in second)
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
