using Blueline.Formats.Textures;

namespace Blueline.Core.Overlays;

public enum OverlayBlend { Normal, Add, Multiply, Screen }

/// <summary>A picture ready to draw: red, green, blue and alpha bytes, with colour already multiplied by alpha so that scaling leaves no dark fringe.</summary>
public sealed class OverlayPicture
{
    public OverlayPicture(int width, int height, byte[] premultiplied)
    {
        Width = width;
        Height = height;
        Pixels = premultiplied;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public static OverlayPicture FromImage(RgbaImage image)
    {
        var output = new byte[image.Pixels.Length];
        for (var i = 0; i < output.Length; i += 4)
        {
            var alpha = image.Pixels[i + 3];
            output[i] = (byte)(image.Pixels[i] * alpha / 255);
            output[i + 1] = (byte)(image.Pixels[i + 1] * alpha / 255);
            output[i + 2] = (byte)(image.Pixels[i + 2] * alpha / 255);
            output[i + 3] = alpha;
        }
        return new OverlayPicture(image.Width, image.Height, output);
    }

    public static OverlayPicture Solid(uint rgb, byte alpha = 255)
    {
        var scale = alpha / 255.0;
        return new OverlayPicture(1, 1, [(byte)(((rgb >> 16) & 255) * scale), (byte)(((rgb >> 8) & 255) * scale), (byte)((rgb & 255) * scale), alpha]);
    }

    /// <summary>The picture's alpha at a place from 0 to 1 across and down.</summary>
    public byte AlphaAt(double u, double v) =>
        Pixels[(Math.Clamp((int)(v * Height), 0, Height - 1) * Width + Math.Clamp((int)(u * Width), 0, Width - 1)) * 4 + 3];
}

/// <summary>
/// One picture to draw. A, B, C, D, X and Y take the picture's corners, 0 to 1 across and down, to the screen:
/// screen x = A*u + C*v + X, screen y = B*u + D*v + Y.
/// </summary>
public sealed record OverlayLayer(OverlayPicture Picture, double A, double B, double C, double D, double X, double Y,
    double Alpha = 1, uint Tint = 0xFFFFFF, OverlayBlend Blend = OverlayBlend.Normal, OverlayClip? Clip = null)
{
    public static OverlayLayer Axis(OverlayPicture picture, double x, double y, double width, double height, double rotationDegrees = 0,
        double alpha = 1, uint tint = 0xFFFFFF, OverlayBlend blend = OverlayBlend.Normal, OverlayClip? clip = null)
    {
        var angle = rotationDegrees * Math.PI / 180;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        return new OverlayLayer(picture, width * cos, width * sin, -height * sin, height * cos, x, y, alpha, tint, blend, clip);
    }

    public (double Left, double Top, double Right, double Bottom) Bounds()
    {
        double[] xs = [X, X + A, X + C, X + A + C];
        double[] ys = [Y, Y + B, Y + D, Y + B + D];
        return (xs.Min(), ys.Min(), xs.Max(), ys.Max());
    }

    /// <summary>The place on the picture under a screen point, or null if the point is off it.</summary>
    public (double U, double V)? Inverse(double screenX, double screenY)
    {
        var determinant = A * D - B * C;
        if (Math.Abs(determinant) < 1e-9)
            return null;
        var dx = screenX - X;
        var dy = screenY - Y;
        var u = (D * dx - C * dy) / determinant;
        var v = (A * dy - B * dx) / determinant;
        if (u < 0 || u >= 1 || v < 0 || v >= 1)
            return null;
        if (Clip is { } clip && (screenX < clip.Left || screenX >= clip.Right || screenY < clip.Top || screenY >= clip.Bottom))
            return null;
        return (u, v);
    }
}

/// <summary>Draws layers in order over a solid backdrop, using the same blend modes the game's interface uses.</summary>
public static class OverlayCompositor
{
    public static uint Multiply(uint a, uint b) =>
        ((((a >> 16) & 255) * ((b >> 16) & 255) / 255) << 16) | (((a >> 8) & 255) * ((b >> 8) & 255) / 255) << 8 | ((a & 255) * (b & 255) / 255);

    /// <param name="originX">The screen position that becomes the image's left edge.</param>
    public static RgbaImage Compose(int width, int height, uint backdrop, IEnumerable<OverlayLayer> layers, double originX = 0, double originY = 0)
    {
        var target = new byte[width * height * 4];
        for (var i = 0; i < target.Length; i += 4)
        {
            target[i] = (byte)(backdrop >> 16);
            target[i + 1] = (byte)(backdrop >> 8);
            target[i + 2] = (byte)backdrop;
            target[i + 3] = 255;
        }

        foreach (var layer in layers)
            Draw(target, width, height, layer, originX, originY);
        return new RgbaImage(width, height, target);
    }

    private static void Draw(byte[] target, int width, int height, OverlayLayer layer, double originX, double originY)
    {
        if (layer.Alpha <= 0 || layer.Picture.Width == 0 || layer.Picture.Height == 0)
            return;
        var determinant = layer.A * layer.D - layer.B * layer.C;
        if (Math.Abs(determinant) < 1e-9)
            return;

        var (left, top, right, bottom) = layer.Bounds();
        if (layer.Clip is { } clip)
        {
            left = Math.Max(left, clip.Left);
            top = Math.Max(top, clip.Top);
            right = Math.Min(right, clip.Right);
            bottom = Math.Min(bottom, clip.Bottom);
        }

        var firstX = Math.Max(0, (int)Math.Floor(left - originX));
        var lastX = Math.Min(width, (int)Math.Ceiling(right - originX));
        var firstY = Math.Max(0, (int)Math.Floor(top - originY));
        var lastY = Math.Min(height, (int)Math.Ceiling(bottom - originY));
        if (firstX >= lastX || firstY >= lastY)
            return;

        var picture = layer.Picture;
        var tintR = ((layer.Tint >> 16) & 255) / 255.0;
        var tintG = ((layer.Tint >> 8) & 255) / 255.0;
        var tintB = (layer.Tint & 255) / 255.0;
        var alpha = Math.Clamp(layer.Alpha, 0, 1);

        Parallel.For(firstY, lastY, y =>
        {
            Span<double> sample = stackalloc double[4];
            for (var x = firstX; x < lastX; x++)
            {
                var screenX = x + originX + 0.5;
                var screenY = y + originY + 0.5;
                if (layer.Clip is { } c && (screenX < c.Left || screenX >= c.Right || screenY < c.Top || screenY >= c.Bottom))
                    continue;
                var dx = screenX - layer.X;
                var dy = screenY - layer.Y;
                var u = (layer.D * dx - layer.C * dy) / determinant;
                var v = (layer.A * dy - layer.B * dx) / determinant;
                if (u < 0 || u >= 1 || v < 0 || v >= 1)
                    continue;

                Sample(picture, u, v, sample);
                var a = sample[3] * alpha;
                if (a <= 0)
                    continue;
                Blend(target, (y * width + x) * 4, sample[0] * tintR * alpha, sample[1] * tintG * alpha, sample[2] * tintB * alpha, a, layer.Blend);
            }
        });
    }

    private static void Sample(OverlayPicture picture, double u, double v, Span<double> output)
    {
        var fx = u * picture.Width - 0.5;
        var fy = v * picture.Height - 0.5;
        var x0 = (int)Math.Floor(fx);
        var y0 = (int)Math.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;
        var x1 = Math.Clamp(x0 + 1, 0, picture.Width - 1);
        var y1 = Math.Clamp(y0 + 1, 0, picture.Height - 1);
        x0 = Math.Clamp(x0, 0, picture.Width - 1);
        y0 = Math.Clamp(y0, 0, picture.Height - 1);

        var pixels = picture.Pixels;
        var p00 = (y0 * picture.Width + x0) * 4;
        var p10 = (y0 * picture.Width + x1) * 4;
        var p01 = (y1 * picture.Width + x0) * 4;
        var p11 = (y1 * picture.Width + x1) * 4;
        for (var i = 0; i < 4; i++)
        {
            var top = pixels[p00 + i] + (pixels[p10 + i] - pixels[p00 + i]) * tx;
            var bottom = pixels[p01 + i] + (pixels[p11 + i] - pixels[p01 + i]) * tx;
            output[i] = (top + (bottom - top) * ty) / 255;
        }
    }

    private static void Blend(byte[] target, int at, double r, double g, double b, double a, OverlayBlend mode)
    {
        for (var channel = 0; channel < 3; channel++)
        {
            var source = channel == 0 ? r : channel == 1 ? g : b;
            var under = target[at + channel] / 255.0;
            var result = mode switch
            {
                OverlayBlend.Add => under + source,
                OverlayBlend.Multiply => under * (1 - a) + under * source,
                OverlayBlend.Screen => under + source - under * source,
                _ => source + under * (1 - a),
            };
            target[at + channel] = (byte)Math.Clamp(result * 255 + 0.5, 0, 255);
        }
    }
}
