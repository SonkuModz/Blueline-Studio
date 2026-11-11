using System.Windows;
using System.Windows.Media;
using Blueline.App.Shell;

namespace Blueline.App.Controls;

/// <summary>
/// Ice rink line art for behind a page: centre red line, blue lines, faceoff circles and the boards, drawn faint in the theme's
/// own blue and red, with the edges faded into the page colour. It redraws when the window changes size or the theme changes.
/// </summary>
public sealed class RinkBackground : FrameworkElement
{
    public RinkBackground()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => ThemeManager.Changed += Redraw;
        Unloaded += (_, _) => ThemeManager.Changed -= Redraw;
    }

    private void Redraw() => Dispatcher.BeginInvoke(InvalidateVisual);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40)
            return;

        // Lines read a little stronger on the light theme, where faint blue on white is easy to lose.
        var strength = ThemeManager.IsDark ? 0.11 : 0.17;
        var blue = Tint("AccentBrush", strength);
        var soft = Tint("AccentBrush", strength * 0.6);
        var red = Tint("DangerBrush", strength * 0.85);

        var unit = h / 100;
        var cx = w / 2;
        var cy = h / 2;
        Pen Pen(Brush brush, double width) { var pen = new Pen(brush, Math.Max(1.5, width)); pen.Freeze(); return pen; }

        // Boards, a rounded rectangle just inside the edge.
        dc.DrawRoundedRectangle(null, Pen(soft, unit * 0.7), new Rect(w * 0.025, h * 0.06, w * 0.95, h * 0.88), unit * 16, unit * 16);

        // Blue lines and the centre red line.
        foreach (var x in new[] { cx - w * 0.17, cx + w * 0.17 })
            dc.DrawRectangle(blue, null, new Rect(x - unit * 1.2, h * 0.06, unit * 2.4, h * 0.88));
        dc.DrawRectangle(red, null, new Rect(cx - unit * 0.7, h * 0.06, unit * 1.4, h * 0.88));

        // Centre ice circle and dot.
        dc.DrawEllipse(null, Pen(blue, unit * 0.9), new Point(cx, cy), unit * 17, unit * 17);
        dc.DrawEllipse(blue, null, new Point(cx, cy), unit * 1.6, unit * 1.6);

        // End zone faceoff circles and dots.
        foreach (var sx in new[] { -1, 1 })
            foreach (var sy in new[] { -1, 1 })
            {
                var centre = new Point(cx + sx * w * 0.36, cy + sy * h * 0.22);
                dc.DrawEllipse(null, Pen(red, unit * 0.8), centre, unit * 15, unit * 15);
                dc.DrawEllipse(red, null, centre, unit * 1.4, unit * 1.4);
            }

        // Fade everything into the page colour toward the edges so it never competes with the content.
        var page = (TryFindResource("WindowBrush") as SolidColorBrush)?.Color ?? Colors.Black;
        var fade = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.75,
            RadiusY = 0.85,
        };
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, page.R, page.G, page.B), 0.0));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, page.R, page.G, page.B), 0.35));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(235, page.R, page.G, page.B), 1.0));
        fade.Freeze();
        dc.DrawRectangle(fade, null, new Rect(0, 0, w, h));
    }

    private Brush Tint(string key, double alpha)
    {
        var colour = (TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.SkyBlue;
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }
}
