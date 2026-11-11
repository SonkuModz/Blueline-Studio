using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Blueline.Core;
using Blueline.Formats.Textures;

namespace Blueline.App.Controls;

/// <summary>A small picture of a game texture that fills in when it has been decoded.</summary>
public sealed class ThumbItem(string? path, int size, bool isPicture = false) : INotifyPropertyChanged
{
    private ImageSource? _image;
    private string _status = "";

    public string? Path { get; } = path;
    public int Size { get; } = size;

    /// <summary>Always draw with transparency, for logos, where one flat colour plus alpha is the picture itself.</summary>
    public bool IsPicture { get; } = isPicture;

    /// <summary>False once it has scrolled out of view, so waiting work for it is dropped.</summary>
    public volatile bool Wanted;

    public ImageSource? Image
    {
        get => _image;
        set { _image = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image))); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Decodes thumbnails off the UI thread, newest request first, so what is on screen fills in before what was
/// scrolled past. It decodes the smallest mip that is big enough, and reads through the workspace so edits show.
/// </summary>
public sealed class ThumbnailLoader(Workspace workspace, Dispatcher dispatcher)
{
    private const int Workers = 3;
    private const int MostCached = 3000;
    private readonly Stack<ThumbItem> _pending = new();
    private readonly Dictionary<(string, int), ImageSource?> _cache = [];
    private readonly object _gate = new();
    private int _running;

    /// <summary>True while thumbnails are waiting or decoding.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
                return _running > 0 || _pending.Count > 0;
        }
    }

    public void Request(ThumbItem item)
    {
        item.Wanted = true;
        if (item.Path == null)
            return;
        lock (_gate)
        {
            if (_cache.TryGetValue((item.Path, item.Size), out var cached))
            {
                Show(item, cached);
                return;
            }
            _pending.Push(item);
            if (_running >= Workers)
                return;
            _running++;
        }
        Task.Run(Work);
    }

    /// <summary>Forgets every picture, because the project changed what some of them show.</summary>
    public void Invalidate()
    {
        lock (_gate)
            _cache.Clear();
    }

    private void Work()
    {
        while (true)
        {
            ThumbItem item;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _running--;
                    return;
                }
                item = _pending.Pop();
            }
            if (!item.Wanted)
                continue;

            ImageSource? image = null;
            try
            {
                image = Decode(item.Path!, item.Size, item.IsPicture);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A file with no preview still gets a tile. Opening it says why.
            }
            lock (_gate)
            {
                if (_cache.Count >= MostCached)
                    _cache.Clear();
                _cache[(item.Path!, item.Size)] = image;
            }
            dispatcher.BeginInvoke(() => Show(item, image));
        }
    }

    private static void Show(ThumbItem item, ImageSource? image)
    {
        item.Image = image;
        item.Status = image == null ? "No preview" : "";
    }

    private ImageSource? Decode(string path, int size, bool isPicture)
    {
        var texture = GameTexture.TryOpen(workspace.ReadCurrent(path) ?? []);
        if (texture == null || texture.Blocker != null)
            return null;

        var mip = 0;
        while (mip + 1 < texture.MipCount && Math.Max(texture.Width >> (mip + 1), texture.Height >> (mip + 1)) >= size)
            mip++;
        var image = texture.DecodeMip(mip);
        var largest = Math.Max(image.Width, image.Height);
        if (largest > size * 2)
            image = TexturePipeline.Resize(image, Math.Max(1, image.Width * size / largest), Math.Max(1, image.Height * size / largest));
        return ToBitmap(image, isPicture);
    }

    private static BitmapSource ToBitmap(RgbaImage image, bool isPicture)
    {
        var source = image.Pixels;
        var output = new byte[source.Length];
        var alphaIsContent = !isPicture && TexturePreview.ContentIsInAlpha(image);
        var alphaIsUnused = !alphaIsContent && AlphaIsEmpty(source);
        for (var i = 0; i < source.Length; i += 4)
        {
            if (alphaIsContent)
            {
                output[i] = output[i + 1] = output[i + 2] = source[i + 3];
                output[i + 3] = 255;
                continue;
            }
            output[i] = source[i + 2];
            output[i + 1] = source[i + 1];
            output[i + 2] = source[i];
            output[i + 3] = alphaIsUnused ? (byte)255 : source[i + 3];
        }
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, output, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Colour with nothing in alpha would draw as nothing, so such textures are shown opaque.</summary>
    private static bool AlphaIsEmpty(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] > 8)
                return false;
        return true;
    }
}
