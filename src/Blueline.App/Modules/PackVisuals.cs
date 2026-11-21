using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Blueline.Core.Packs;

namespace Blueline.App.Modules;

/// <summary>Shared pieces of the Mods and Export pages: section labels, check lines, chips, the cover banner and the pack tile.</summary>
internal static class PackVisuals
{
    public static T Brush<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    public static TextBlock Label(string text, double top = 16) =>
        new TextBlock { Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 8) }
            .Brush(TextBlock.ForegroundProperty, "DimBrush");

    public static TextBlock Dim(string text, double size = 12) =>
        new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap }.Brush(TextBlock.ForegroundProperty, "DimBrush");

    public static Border Card(UIElement child, double padding = 10) =>
        new Border { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) }
            .Brush(Border.BackgroundProperty, "RaisedBrush").Brush(Border.BorderBrushProperty, "LineBrush");

    public static BitmapSource? Bitmap(byte[]? png)
    {
        if (png == null)
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A green, amber or red dot and a line of text, as on the Mods page's checks.</summary>
    public static UIElement CheckLine(CheckLevel level, string title, string detail)
    {
        var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 5, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, level switch { CheckLevel.Ok => "SuccessBrush", CheckLevel.Warning => "WarningBrush", _ => "DangerBrush" });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (detail.Length > 0)
            text.Children.Add(Dim(detail));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        row.Children.Add(text);
        return row;
    }

    public static WrapPanel Chips(IEnumerable<string> paths, params string[] extra)
    {
        var panel = new WrapPanel();
        foreach (var label in PackSummary.Groups(paths).Select(g => $"{g.Group} {g.Count:N0}").Concat(extra))
            panel.Children.Add(new Border
            {
                Child = new TextBlock { Text = label, FontSize = 12 },
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
            }.Brush(Border.BackgroundProperty, "RaisedBrush").Brush(Border.BorderBrushProperty, "LineBrush"));
        return panel;
    }

    /// <summary>The pack's banner: its cover, or one drawn from its name in the Ice colours when it has none.</summary>
    public static FrameworkElement Cover(byte[]? cover, string name, double height)
    {
        var grid = new Grid { Height = height, ClipToBounds = true };
        var border = new Border { CornerRadius = new CornerRadius(10), Child = grid, ClipToBounds = true };
        if (Bitmap(cover) is { } image)
        {
            border.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            return border;
        }
        border.Background = new LinearGradientBrush(
            [new GradientStop(Color.FromRgb(0x06, 0x2A, 0x47), 0), new GradientStop(Color.FromRgb(0x02, 0x77, 0xB6), 0.55), new GradientStop(Color.FromRgb(0x5C, 0xCB, 0xF5), 1)],
            new Point(0, 0), new Point(1, 1));
        grid.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = Brushes.White,
            FontSize = Math.Clamp(height / 7, 14, 26),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(18, 0, 18, 12),
            VerticalAlignment = VerticalAlignment.Bottom,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return border;
    }

    /// <summary>The pack's square tile for lists: its icon, or the first letters of its name.</summary>
    public static FrameworkElement Icon(byte[]? icon, string name, double size)
    {
        var border = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(6), ClipToBounds = true };
        if (Bitmap(icon) is { } image)
        {
            border.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            return border;
        }
        border.Background = new LinearGradientBrush(Color.FromRgb(0x0B, 0x3D, 0x63), Color.FromRgb(0x5C, 0xCB, 0xF5), 45);
        var letters = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
        border.Child = new TextBlock { Text = letters, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = size / 2.6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return border;
    }

    public static FrameworkElement Shot(byte[] png, double height) =>
        new Border
        {
            Width = height * 16 / 9,
            Height = height,
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 0, 6, 0),
            Background = Bitmap(png) is { } image ? new ImageBrush(image) { Stretch = Stretch.UniformToFill } : null,
        };

    public static string S(int count) => count == 1 ? "" : "s";
}

internal sealed class JobProgress(Shell.JobContext context) : IProgress<(string Stage, int Done, int Total)>
{
    public void Report((string Stage, int Done, int Total) value) => context.Progress(value.Stage, value.Done, value.Total);
}
