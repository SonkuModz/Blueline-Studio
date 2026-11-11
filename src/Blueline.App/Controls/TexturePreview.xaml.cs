using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Blueline.Formats.Textures;

namespace Blueline.App.Controls;

/// <summary>Shows a game texture: channels, mips, faces, zoom. Decoding happens off the UI thread.</summary>
public partial class TexturePreview : UserControl
{
    private const int LargestDefaultMip = 2048;
    private GameTexture? _texture;
    private RgbaImage? _image;
    private int _generation;
    private bool _loading;
    private bool _chooseAlpha;
    private bool _startWithAlpha;
    private bool _fit = true;
    private double _zoom = 1;

    public TexturePreview()
    {
        InitializeComponent();
        ShowMessage("Select a texture to preview it.");
    }

    public bool IsDecoding { get; private set; }

    public void ShowMessage(string text)
    {
        _generation++;
        _texture = null;
        _image = null;
        IsDecoding = false;
        Picture.Source = null;
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
        MipBox.ItemsSource = null;
        FaceBox.ItemsSource = null;
        ZoomText.Text = "";
    }

    /// <param name="showAlpha">Starts with transparency on, for pictures like logos that are meant to be seen that way.</param>
    public void SetTexture(GameTexture texture, bool showAlpha = false)
    {
        _startWithAlpha = showAlpha;
        _texture = texture;
        _loading = true;
        if (texture.Blocker is { } blocker)
        {
            _loading = false;
            ShowMessage(blocker);
            return;
        }

        MipBox.ItemsSource = Enumerable.Range(0, texture.MipCount)
            .Select(m => $"{m}: {Math.Max(1, texture.Width >> m)}x{Math.Max(1, texture.Height >> m)}").ToList();
        var mip = 0;
        while (mip + 1 < texture.MipCount && Math.Max(texture.Width >> mip, texture.Height >> mip) > LargestDefaultMip)
            mip++;
        MipBox.SelectedIndex = mip;

        FaceBox.ItemsSource = Enumerable.Range(0, texture.Faces).ToList();
        FaceBox.SelectedIndex = 0;
        FaceBox.Visibility = FaceLabel.Visibility = texture.Faces > 1 ? Visibility.Visible : Visibility.Collapsed;

        _loading = false;
        _fit = true;
        _chooseAlpha = true;
        Decode();
    }

    /// <summary>Shows a picture that is not a game file, such as a generated map. It has one level, so the mip list is hidden.</summary>
    /// <param name="keepView">Keeps the zoom and scroll, for swapping between versions of one sheet.</param>
    public void SetImage(RgbaImage image, bool keepView = false)
    {
        _generation++;
        _texture = null;
        _loading = true;
        MipBox.ItemsSource = new[] { $"{image.Width}x{image.Height}" };
        MipBox.SelectedIndex = 0;
        FaceBox.ItemsSource = null;
        FaceBox.Visibility = FaceLabel.Visibility = Visibility.Collapsed;
        _loading = false;
        IsDecoding = false;
        _image = image;
        if (!keepView)
        {
            _fit = true;
            ChanA.IsChecked = false;
        }
        Message.Visibility = Visibility.Collapsed;
        Render();
    }

    /// <summary>
    /// True when a picture's colour is one flat value and its content is all in alpha, as with number fonts.
    /// Samples a grid of pixels, which is enough to tell a flat sheet from a real picture.
    /// </summary>
    public static bool ContentIsInAlpha(RgbaImage image)
    {
        var pixels = image.Pixels;
        var step = Math.Max(1, pixels.Length / 4 / 4096) * 4;
        var (first, minAlpha, maxAlpha) = (pixels.AsSpan(0, 3).ToArray(), 255, 0);
        for (var i = 0; i < pixels.Length; i += step)
        {
            for (var c = 0; c < 3; c++)
                if (Math.Abs(pixels[i + c] - first[c]) > 4)
                    return false;
            minAlpha = Math.Min(minAlpha, pixels[i + 3]);
            maxAlpha = Math.Max(maxAlpha, pixels[i + 3]);
        }
        return maxAlpha - minAlpha > 32;
    }

    private void OnMipChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
            Decode();
    }

    private void Decode()
    {
        if (_texture == null || MipBox.SelectedIndex < 0)
            return;

        var generation = ++_generation;
        var texture = _texture;
        var mip = MipBox.SelectedIndex;
        var face = Math.Max(0, FaceBox.SelectedIndex);
        IsDecoding = true;
        Message.Text = "Decoding";
        Message.Visibility = Visibility.Visible;

        Task.Run(() => texture.DecodeMip(mip, face)).ContinueWith(task =>
        {
            if (generation != _generation)
                return;
            IsDecoding = false;
            if (task.IsFaulted)
            {
                Picture.Source = null;
                Message.Text = "This texture could not be decoded: " + task.Exception!.GetBaseException().Message;
                return;
            }
            _image = task.Result;
            if (_chooseAlpha)
            {
                ChanA.IsChecked = _startWithAlpha || ContentIsInAlpha(_image);
                _chooseAlpha = false;
            }
            Message.Visibility = Visibility.Collapsed;
            Render();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnChannel(object sender, RoutedEventArgs e) => Render();

    private void Render()
    {
        if (_image == null)
            return;

        var (r, g, b, a) = (ChanR.IsChecked == true, ChanG.IsChecked == true, ChanB.IsChecked == true, ChanA.IsChecked == true);
        var source = _image.Pixels;
        var output = new byte[source.Length];
        var single = (r ? 1 : 0) + (g ? 1 : 0) + (b ? 1 : 0) == 1;
        var alphaOnly = a && !r && !g && !b;
        for (var i = 0; i < source.Length; i += 4)
        {
            if (alphaOnly)
            {
                output[i] = output[i + 1] = output[i + 2] = source[i + 3];
                output[i + 3] = 255;
                continue;
            }
            if (single)
            {
                // One channel reads best as grayscale.
                var v = r ? source[i] : g ? source[i + 1] : source[i + 2];
                output[i] = output[i + 1] = output[i + 2] = v;
            }
            else
            {
                output[i] = b ? source[i + 2] : (byte)0;
                output[i + 1] = g ? source[i + 1] : (byte)0;
                output[i + 2] = r ? source[i] : (byte)0;
            }
            output[i + 3] = a ? source[i + 3] : (byte)255;
        }

        Picture.Source = BitmapSource.Create(_image.Width, _image.Height, 96, 96, PixelFormats.Bgra32, null, output, _image.Width * 4);
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_image == null)
            return;
        if (_fit)
        {
            var available = Math.Min((Scroller.ActualWidth - 4) / _image.Width, (Scroller.ActualHeight - 4) / _image.Height);
            _zoom = available > 0 ? Math.Min(available, 4) : 1;
        }
        Picture.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        RenderOptions.SetBitmapScalingMode(Picture, _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        ZoomText.Text = $"{_zoom * 100:F0}%";
    }

    private void OnFit(object sender, RoutedEventArgs e)
    {
        _fit = true;
        ApplyZoom();
    }

    private void OnActual(object sender, RoutedEventArgs e)
    {
        _fit = false;
        _zoom = 1;
        ApplyZoom();
    }

    private void OnViewportChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fit)
            ApplyZoom();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_image == null)
            return;
        _fit = false;
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), 0.05, 32);
        ApplyZoom();
        e.Handled = true;
    }
}
