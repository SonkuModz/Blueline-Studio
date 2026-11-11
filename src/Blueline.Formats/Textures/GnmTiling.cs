namespace Blueline.Formats.Textures;

/// <summary>
/// Gnm "Thin 1D" tiling, the only mode in NHL 19 (130,305 of 130,305 textures). An element (a BC
/// block, or a pixel for uncompressed formats) is placed in 8x8 micro-tiles; inside a tile the
/// elements are in Morton order and tiles run in raster order. Every mip is padded to a multiple of 8
/// elements. Mips run largest first, and each mip holds all of its faces.
/// </summary>
public static class GnmTiling
{
    public const int Thin1D = 13;
    private const int TileElements = 8;

    private static readonly int[] MortonTable = BuildMorton();

    private static int[] BuildMorton()
    {
        var table = new int[TileElements * TileElements];
        for (var y = 0; y < TileElements; y++)
        {
            for (var x = 0; x < TileElements; x++)
            {
                var index = 0;
                for (var bit = 0; bit < 3; bit++)
                    index |= ((x >> bit) & 1) << (2 * bit) | ((y >> bit) & 1) << (2 * bit + 1);
                table[y * TileElements + x] = index;
            }
        }
        return table;
    }

    public static int ElementBytes(TextureCodec codec) => codec switch
    {
        TextureCodec.Bc1 or TextureCodec.Bc4 => 8,
        TextureCodec.Rgba8 => 4,
        _ => 16,
    };

    public static int ElementSpan(TextureCodec codec) => codec == TextureCodec.Rgba8 ? 1 : 4;

    private static int ElementsOf(int pixels, int span) => (pixels + span - 1) / span;

    private static int PadToTile(int elements) => (elements + TileElements - 1) & ~(TileElements - 1);

    /// <summary>Bytes of one face of one mip, tile padding included.</summary>
    public static long SurfaceBytes(GnfInfo info, int mip)
    {
        var (columns, rows, bytes) = Shape(info, mip);
        return (long)PadToTile(columns) * PadToTile(rows) * bytes;
    }

    public static long SurfaceOffset(GnfInfo info, int mip, int face)
    {
        long offset = 0;
        for (var m = 0; m < mip; m++)
            offset += SurfaceBytes(info, m) * info.Faces;
        return offset + SurfaceBytes(info, mip) * face;
    }

    public static long TotalBytes(GnfInfo info) => SurfaceOffset(info, info.MipCount, 0);

    public static byte[] Detile(ReadOnlySpan<byte> tiled, GnfInfo info, int mip)
    {
        var (columns, rows, bytes) = Shape(info, mip);
        var map = BuildMap(columns, rows);
        var output = new byte[columns * rows * bytes];
        for (var element = 0; element < map.Length; element++)
            tiled.Slice(map[element] * bytes, bytes).CopyTo(output.AsSpan(element * bytes));
        return output;
    }

    public static void Tile(ReadOnlySpan<byte> raster, Span<byte> tiled, GnfInfo info, int mip)
    {
        var (columns, rows, bytes) = Shape(info, mip);
        if (raster.Length != columns * rows * bytes)
            throw new ArgumentException($"Mip {mip} should be {columns * rows * bytes} bytes, got {raster.Length}.");

        var map = BuildMap(columns, rows);
        for (var element = 0; element < map.Length; element++)
            raster.Slice(element * bytes, bytes).CopyTo(tiled[(map[element] * bytes)..]);
    }

    /// <summary>For each element in raster order, its element index in tiled order.</summary>
    private static int[] BuildMap(int columns, int rows)
    {
        var tilesPerRow = PadToTile(columns) / TileElements;
        var map = new int[columns * rows];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var tile = row / TileElements * tilesPerRow + column / TileElements;
                var inTile = MortonTable[row % TileElements * TileElements + column % TileElements];
                map[row * columns + column] = tile * TileElements * TileElements + inTile;
            }
        }
        return map;
    }

    private static (int Columns, int Rows, int Bytes) Shape(GnfInfo info, int mip)
    {
        var span = ElementSpan(info.Codec);
        return (ElementsOf(Math.Max(1, info.Width >> mip), span), ElementsOf(Math.Max(1, info.Height >> mip), span), ElementBytes(info.Codec));
    }
}
