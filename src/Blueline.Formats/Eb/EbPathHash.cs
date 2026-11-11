using System.Text;

namespace Blueline.Formats.Eb;

/// <summary>
/// The key the file table is sorted by: 64-bit djb2 over the full stored path.
/// Verified against all 152,659 entries in NHL 19. See docs/RESEARCH.md section 3.
/// </summary>
public static class EbPathHash
{
    public static ulong Compute(string path)
    {
        var hash = 5381UL;
        foreach (var b in Encoding.Latin1.GetBytes(path))
            hash = unchecked(hash * 33 + b);
        return hash;
    }
}
