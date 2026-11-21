using System.Globalization;

namespace Blueline.Core;

/// <summary>
/// The colours of one app theme, as hex text so they can be checked without a window. Keys are the names of the brushes in
/// Theme.xaml with "Brush" left off. The brand colours come from the Blueline Studio logo files: sky blue, deep navy and slate.
/// </summary>
public sealed class UiPalette
{
    public const string DarkName = "dark";
    public const string LightName = "light";

    public required string Name { get; init; }
    public required IReadOnlyDictionary<string, string> Colours { get; init; }

    public static UiPalette Dark { get; } = new()
    {
        Name = DarkName,
        Colours = new Dictionary<string, string>
        {
            ["Window"] = "#060A12",
            ["Panel"] = "#0C1422",
            ["Raised"] = "#121C2E",
            ["Hover"] = "#1A2740",
            ["Line"] = "#1E2C44",
            ["Rail"] = "#08101C",
            ["Text"] = "#E6F1FA",
            ["Dim"] = "#8FA5BE",
            ["Disabled"] = "#53657D",
            ["Accent"] = "#0277B6",
            ["AccentHover"] = "#0A8FD4",
            ["AccentPress"] = "#035F92",
            ["AccentText"] = "#5CCBF5",
            ["AccentSoft"] = "#245CCBF5",
            ["Select"] = "#385CCBF5",
            ["Danger"] = "#F0655C",
            ["Success"] = "#46BE8A",
            ["Warning"] = "#E8B04A",
            ["ScrollThumb"] = "#2A3A54",
            ["ScrollThumbHover"] = "#3D5170",
            ["Zebra"] = "#0AFFFFFF",
            ["Banner"] = "#2A1A1D",
        },
    };

    public static UiPalette Light { get; } = new()
    {
        Name = LightName,
        Colours = new Dictionary<string, string>
        {
            ["Window"] = "#E9F1F8",
            ["Panel"] = "#FFFFFF",
            ["Raised"] = "#F2F7FB",
            ["Hover"] = "#E0EAF3",
            ["Line"] = "#C9D8E6",
            ["Rail"] = "#F7FAFD",
            ["Text"] = "#0B1A2C",
            ["Dim"] = "#4F6680",
            ["Disabled"] = "#93A3B5",
            ["Accent"] = "#0277B6",
            ["AccentHover"] = "#035F92",
            ["AccentPress"] = "#054B73",
            ["AccentText"] = "#03679E",
            ["AccentSoft"] = "#1F0277B6",
            ["Select"] = "#330277B6",
            ["Danger"] = "#C93C3C",
            ["Success"] = "#177A55",
            ["Warning"] = "#9A6410",
            ["ScrollThumb"] = "#C9D8E6",
            ["ScrollThumbHover"] = "#93A3B5",
            ["Zebra"] = "#08000000",
            ["Banner"] = "#FDECEC",
        },
    };

    public static UiPalette Named(string? name) => string.Equals(name, LightName, StringComparison.OrdinalIgnoreCase) ? Light : Dark;

    /// <summary>The colour as red, green, blue and alpha bytes.</summary>
    public static (byte R, byte G, byte B, byte A) Parse(string hex)
    {
        var text = hex.TrimStart('#');
        var value = uint.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return text.Length == 8
            ? ((byte)(value >> 16), (byte)(value >> 8), (byte)value, (byte)(value >> 24))
            : ((byte)(value >> 16), (byte)(value >> 8), (byte)value, (byte)255);
    }

    /// <summary>WCAG contrast ratio between two opaque colours, from 1 to 21.</summary>
    public static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var (r, g, b, _) = Parse(hex);
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }
}
