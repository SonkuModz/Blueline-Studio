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
            ["Window"] = "#090D16",
            ["Panel"] = "#0F1523",
            ["Raised"] = "#151D2E",
            ["Hover"] = "#1E293B",
            ["Line"] = "#243146",
            ["Text"] = "#E2E8F0",
            ["Dim"] = "#94A3B8",
            ["Accent"] = "#0284C7",
            ["AccentHover"] = "#0EA5E9",
            ["AccentPress"] = "#0369A1",
            ["AccentText"] = "#38BDF8",
            ["AccentSoft"] = "#1F38BDF8",
            ["Select"] = "#3338BDF8",
            ["Danger"] = "#F0655C",
            ["Success"] = "#46BE8A",
            ["Warning"] = "#E8B04A",
            ["ScrollThumb"] = "#334155",
            ["ScrollThumbHover"] = "#475569",
            ["Zebra"] = "#0AFFFFFF",
            ["Banner"] = "#2A1A1D",
        },
    };

    public static UiPalette Light { get; } = new()
    {
        Name = LightName,
        Colours = new Dictionary<string, string>
        {
            ["Window"] = "#EAF0F7",
            ["Panel"] = "#FFFFFF",
            ["Raised"] = "#F3F6FA",
            ["Hover"] = "#E2E8F0",
            ["Line"] = "#CBD5E1",
            ["Text"] = "#0F172A",
            ["Dim"] = "#566579",
            ["Accent"] = "#0284C7",
            ["AccentHover"] = "#0369A1",
            ["AccentPress"] = "#075985",
            ["AccentText"] = "#0369A1",
            ["AccentSoft"] = "#1F0284C7",
            ["Select"] = "#330284C7",
            ["Danger"] = "#C93C3C",
            ["Success"] = "#177A55",
            ["Warning"] = "#9A6410",
            ["ScrollThumb"] = "#CBD5E1",
            ["ScrollThumbHover"] = "#94A3B8",
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
