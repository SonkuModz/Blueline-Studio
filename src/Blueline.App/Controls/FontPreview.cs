using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Blueline.App.Controls;

/// <summary>Shows a font file as sample text. Windows needs the font on disk, so it is kept by content in a cache folder.</summary>
public sealed class FontPreview : ScrollViewer
{
    private static readonly string[] Samples =
    [
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
        "abcdefghijklmnopqrstuvwxyz",
        "0123456789  .,:;!?&#%()-+",
        "Toronto 3, Montreal 2 (OT)",
    ];
    private static readonly double[] Sizes = [28, 28, 28, 42];

    private readonly StackPanel _lines = new() { Margin = new Thickness(14) };

    public FontPreview()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        Content = _lines;
    }

    /// <summary>The font's name, once one is showing.</summary>
    public string Description { get; private set; } = "";

    /// <summary>False when Windows cannot read this as a font.</summary>
    public bool Show(byte[] content)
    {
        _lines.Children.Clear();
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blueline", "font-cache");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, Convert.ToHexString(SHA1.HashData(content)) + ".ttf");
            if (!File.Exists(file))
                File.WriteAllBytes(file, content);

            var glyphs = new GlyphTypeface(new Uri(file));
            var brush = (Brush)Application.Current.FindResource("TextBrush");
            var pixelsPerDip = (float)VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (var i = 0; i < Samples.Length; i++)
                _lines.Children.Add(new Image { Source = Draw(glyphs, Samples[i], Sizes[i], brush, pixelsPerDip), Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14) });
            Description = $"{glyphs.FamilyNames.Values.First()}, {glyphs.GlyphCount:N0} glyphs";
            return _lines.Children.Count > 0;
        }
        catch (Exception e) when (e is FileFormatException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Draws straight from the file's glyphs. Going through a font family lets WPF silently substitute a system font.</summary>
    private static DrawingImage? Draw(GlyphTypeface font, string text, double size, Brush brush, float pixelsPerDip)
    {
        var indices = new List<ushort>();
        var advances = new List<double>();
        foreach (var character in text)
        {
            if (!font.CharacterToGlyphMap.TryGetValue(character, out var glyph))
                continue;
            indices.Add(glyph);
            advances.Add(font.AdvanceWidths[glyph] * size);
        }
        if (indices.Count == 0)
            return null;

        var run = new GlyphRun(font, 0, false, size, pixelsPerDip, indices, new Point(0, font.Baseline * size), advances, null, null, null, null, null, null);
        var image = new DrawingImage(new GlyphRunDrawing(brush, run));
        image.Freeze();
        return image;
    }
}
