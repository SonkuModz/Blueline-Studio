using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Blueline.Core.Overlays;
using Blueline.Formats.Scaleform;
using Blueline.Formats.Textures;

namespace Blueline.App.Controls;

/// <summary>
/// Draws a layout or a movie frame the way the game puts it together: the game's own textures, fonts, tints, blend
/// modes and clipping on a 1920 by 1080 screen. Click selects a widget and dragging moves it.
/// </summary>
public sealed class OverlayCanvas : Canvas
{
    public const double ScreenWidth = 1920;
    public const double ScreenHeight = 1080;
    private const double FramePadding = 24;
    private const double DragStart = 2;
    private const uint Backdrop = 0x2C3C4C;
    private static readonly OverlayPicture WhiteBlock = OverlayPicture.Solid(0xFFFFFF);
    private static readonly OverlayPicture UnknownBlock = OverlayPicture.Solid(0x78828F, 90);

    private readonly List<(OverlayLayer Layer, PlacedWidget Placed)> _drawn = [];
    private readonly Dictionary<string, OverlayPicture?> _pictures = [];
    private readonly Dictionary<string, string> _fontFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Typeface> _typefaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FontLine?> _fontLines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OverlayPicture> _textPictures = [];
    private readonly Image _image = new() { IsHitTestVisible = false };
    private IReadOnlyList<PlacedWidget> _placed = [];
    private LayoutDocument? _document;
    private OverlayCatalog? _catalog;
    private OverlaySample? _sample;
    private Func<string, LayoutDocument?> _expand = _ => null;
    private LayoutWidget? _selected;
    private Point _pressedAt;
    private PlacedWidget? _dragging;
    private double _movedX, _movedY;
    private bool _moved;
    private double _originX, _originY;
    private double _zoom = 1;
    private bool _gameScreen = true;
    private bool _showHidden;
    private OverlayPose? _pose;

    public OverlayCanvas()
    {
        Background = Brushes.Transparent;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        MouseLeftButtonDown += OnPress;
        MouseMove += OnDrag;
        MouseLeftButtonUp += OnRelease;
    }

    public event Action<LayoutWidget?>? WidgetChosen;

    /// <summary>A drag has finished and changed the layout.</summary>
    public event Action? WidgetMoved;

    public int DrawnCount => _drawn.Count;

    /// <summary>The picture on screen, for tests that need to see whether a change reached it.</summary>
    public RgbaImage? Composite { get; private set; }

    public bool GameScreen
    {
        get => _gameScreen;
        set { _gameScreen = value; Redraw(); }
    }

    public bool ShowHidden
    {
        get => _showHidden;
        set { _showHidden = value; Redraw(); }
    }

    /// <summary>What a running animation has done to the widgets, or null to show the file as it is.</summary>
    public OverlayPose? Pose
    {
        get => _pose;
        set { _pose = value; Redraw(); }
    }

    private Func<LayoutWidget, string, System.Text.Json.Nodes.JsonNode?> Read => (widget, key) =>
        _pose != null && _pose.Has(widget, key) ? _pose.Get(widget, key) : _catalog!.Styled(widget, key);

    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = value;
            LayoutTransform = new ScaleTransform(value, value);
            if (_document != null)
                DrawSelection();
        }
    }

    public static BitmapSource ToBitmap(RgbaImage image)
    {
        var output = new byte[image.Pixels.Length];
        for (var i = 0; i < output.Length; i += 4)
        {
            output[i] = image.Pixels[i + 2];
            output[i + 1] = image.Pixels[i + 1];
            output[i + 2] = image.Pixels[i];
            output[i + 3] = image.Pixels[i + 3];
        }
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, output, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The name a widget's picture is stored under, because team logos differ by side.</summary>
    public static string PictureKey(LayoutWidget widget, OverlaySample sample) => $"{widget.AssetType}#{sample.AssetNumber(widget)}";

    /// <summary>Shows a layout. Pictures were decoded beforehand, and fonts were saved to disk beforehand, because both are slow.</summary>
    public void Show(LayoutDocument? document, OverlayCatalog catalog, IReadOnlyDictionary<string, OverlayPicture?> pictures,
        IReadOnlyDictionary<string, string> fontFiles, OverlaySample sample, Func<string, LayoutDocument?> expand)
    {
        _document = document;
        _catalog = catalog;
        _sample = sample;
        _expand = expand;
        _selected = null;
        _pictures.Clear();
        foreach (var (name, picture) in pictures)
            _pictures[name] = picture;
        _fontFiles.Clear();
        foreach (var (face, file) in fontFiles)
            _fontFiles[face] = file;
        _typefaces.Clear();
        _fontLines.Clear();
        _textPictures.Clear();
        Redraw();
    }

    /// <summary>Shows one frame of a movie on its own stage. Movies have no widgets, so nothing can be picked.</summary>
    public void ShowMovieFrame(GfxTimeline timeline, IReadOnlyList<GfxPiece> pieces, IReadOnlyDictionary<int, OverlayPicture?> pictures)
    {
        _document = null;
        _selected = null;
        _placed = [];
        _drawn.Clear();
        var layers = new List<OverlayLayer>();
        foreach (var piece in pieces)
        {
            OverlayPicture? picture;
            uint tint = piece.Tint;
            if (piece.ImageId >= 0)
            {
                picture = pictures.GetValueOrDefault(piece.ImageId);
            }
            else
            {
                picture = WhiteBlock;
                tint = OverlayCompositor.Multiply(tint, piece.Rgb);
            }
            if (picture != null)
                layers.Add(new OverlayLayer(picture, piece.A, piece.B, piece.C, piece.D, piece.X, piece.Y, piece.Alpha, tint, (OverlayBlend)piece.Blend,
                    piece.Clip is { } c ? new OverlayClip(c.Left, c.Top, c.Right, c.Bottom) : null));
        }
        MovieLayerCount = layers.Count;
        Paint((int)Math.Ceiling(timeline.StageWidth), (int)Math.Ceiling(timeline.StageHeight), layers, 0, 0);
    }

    public int MovieLayerCount { get; private set; }

    public void Select(LayoutWidget? widget)
    {
        _selected = widget;
        Redraw();
    }

    public (double X, double Y)? PositionOf(string name) =>
        _placed.FirstOrDefault(p => p.Widget.Name == name) is { } item ? (item.X, item.Y) : null;

    private (double, double)? PictureSize(string asset) =>
        _pictures.GetValueOrDefault(asset) is { } picture ? (picture.Width, picture.Height) : null;

    private uint? ColourValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        if (value.StartsWith('$'))
            return _catalog!.Colours.TryGetValue(value[1..], out var named) ? named & 0xFFFFFF : null;
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(value[2..], NumberStyles.HexNumber, null, out var rgb) ? rgb & 0xFFFFFF : null;
    }

    public void Redraw()
    {
        Children.Clear();
        _drawn.Clear();
        _placed = [];
        Composite = null;
        if (_document == null || _catalog == null || _sample == null)
        {
            Width = Height = 0;
            _image.Source = null;
            return;
        }

        var options = new PlacerOptions
        {
            ScreenWidth = ScreenWidth,
            ScreenHeight = ScreenHeight,
            ShowHidden = _showHidden || _pose != null,
            Reveal = _selected,
            Colour = ColourValue,
            GameColour = widget => _pose != null && (_pose.Has(widget, "color") || _pose.Has(widget, "fillColor")) ? null : _sample.Colour(widget),
            GameBox = _sample.GameBox,
            Text = _sample.Text,
            Styled = Read,
            Posed = _pose != null,
        };
        _placed = new LayoutPlacer(a => PictureSize(a), _expand, options).Place(_document);
        foreach (var item in _placed)
            if (Build(item) is { } layer)
                _drawn.Add((layer, item));

        var layers = _drawn.Select(d => d.Layer).ToList();
        if (_gameScreen || layers.Count == 0)
        {
            Paint((int)ScreenWidth, (int)ScreenHeight, layers, 0, 0);
        }
        else
        {
            var (left, top, right, bottom) = VisibleArea(layers);
            Paint((int)Math.Ceiling(right - left + FramePadding * 2), (int)Math.Ceiling(bottom - top + FramePadding * 2), layers, left - FramePadding, top - FramePadding);
        }
        DrawSelection();
    }

    private static (double Left, double Top, double Right, double Bottom) VisibleArea(List<OverlayLayer> layers)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var layer in layers.Where(l => l.Alpha > 0))
        {
            var (l, t, r, b) = layer.Bounds();
            if (layer.Clip is { } clip)
                (l, t, r, b) = (Math.Max(l, clip.Left), Math.Max(t, clip.Top), Math.Min(r, clip.Right), Math.Min(b, clip.Bottom));
            if (r <= l || b <= t)
                continue;
            (left, top, right, bottom) = (Math.Min(left, l), Math.Min(top, t), Math.Max(right, r), Math.Max(bottom, b));
        }
        return left == double.MaxValue ? (0, 0, ScreenWidth, ScreenHeight) : (left, top, right, bottom);
    }

    private void Paint(int width, int height, IEnumerable<OverlayLayer> layers, double originX, double originY)
    {
        _originX = originX;
        _originY = originY;
        Composite = OverlayCompositor.Compose(Math.Max(1, width), Math.Max(1, height), Backdrop, layers, originX, originY);
        _image.Source = ToBitmap(Composite);
        _image.Width = Width = Composite.Width;
        _image.Height = Height = Composite.Height;
        Children.Clear();
        Children.Add(_image);
    }

    private void DrawSelection()
    {
        foreach (var old in Children.OfType<Rectangle>().ToList())
            Children.Remove(old);
        if (_selected == null || _placed.FirstOrDefault(p => ReferenceEquals(p.Widget, _selected)) is not { } item)
            return;
        var outline = new Rectangle
        {
            Width = Math.Max(4, Math.Abs(item.Width * item.ScaleX)),
            Height = Math.Max(4, Math.Abs(item.Height * item.ScaleY)),
            Stroke = (Brush)Application.Current.FindResource("AccentBrush"),
            StrokeThickness = 2 / Math.Max(0.05, _zoom),
            StrokeDashArray = [4, 2],
            IsHitTestVisible = false,
        };
        SetLeft(outline, Math.Min(item.X, item.X + item.Width * item.ScaleX) - _originX);
        SetTop(outline, Math.Min(item.Y, item.Y + item.Height * item.ScaleY) - _originY);
        Children.Add(outline);
    }

    private OverlayLayer? Build(PlacedWidget item)
    {
        var node = item.Widget.Node;
        OverlayLayer Block(OverlayPicture picture, uint tint) =>
            OverlayLayer.Axis(picture, item.X, item.Y, item.Width * item.ScaleX, item.Height * item.ScaleY, item.Rotation, item.Alpha, tint, item.Blend, item.Clip);

        switch (item.Widget.Kind)
        {
            case "Asset":
                var key = JsonEdit.Number(_pose?.Get(item.Widget, "id")) is { } posedId ? $"{item.Widget.AssetType}#{(int)posedId}" : PictureKey(item.Widget, _sample!);
                return _pictures.GetValueOrDefault(key) is { } picture ? Block(picture, item.Tint) : Block(UnknownBlock, 0xFFFFFF);
            case "Rectangle":
            case "SlicedRectangleAS":
                if (item.Width <= 0 || item.Height <= 0)
                    return null;
                return Block(OverlayPicture.Solid(0xFFFFFF, (byte)(255 * Math.Clamp(JsonEdit.Number(node["fillAlpha"]) ?? 1, 0, 1))), item.Tint);
            case "Label":
                if (TextPicture(item) is not { } text)
                    return null;
                var ink = ColourValue(_catalog!.Look(item.Widget, Read).Colour) ?? 0xFFFFFF;
                return OverlayLayer.Axis(text, item.X, item.Y, text.Width * item.ScaleX, text.Height * item.ScaleY, item.Rotation, item.Alpha, OverlayCompositor.Multiply(item.Tint, ink), item.Blend, item.Clip);
            case "MoviePlayer":
                return Block(UnknownBlock, 0xFFFFFF);
            default:
                return null;
        }
    }

    private Typeface TypefaceFor(string face)
    {
        if (_typefaces.TryGetValue(face, out var known))
            return known;
        var typeface = _fontFiles.TryGetValue(face, out var file) ? Fonts.GetTypefaces(new Uri(file)).FirstOrDefault() : null;
        return _typefaces[face] = typeface ?? new Typeface("Segoe UI");
    }

    private FontLine? FontLineFor(string face)
    {
        if (_fontLines.TryGetValue(face, out var known))
            return known;
        return _fontLines[face] = _fontFiles.TryGetValue(face, out var file) ? FontLine.Read(File.ReadAllBytes(file)) : null;
    }

    /// <summary>Text drawn in white with the game's font. The widget's colour is applied as a tint, like everything else.</summary>
    private OverlayPicture? TextPicture(PlacedWidget item)
    {
        var posed = _pose != null && _pose.Has(item.Widget, "text");
        var written = JsonEdit.String(posed ? _pose!.Get(item.Widget, "text") : item.Widget.Node["text"]);
        var text = posed || !string.IsNullOrEmpty(written) ? written : _sample!.Text(item.Widget);
        if (string.IsNullOrEmpty(text))
            return null;

        var look = _catalog!.Look(item.Widget, Read);
        if (look.ForceCaps)
            text = text.ToUpperInvariant();
        var face = look.Face;
        var size = look.Size;
        var alignH = look.AlignH;
        var alignV = look.AlignV;
        var compress = look.Resize == "COMPRESS";
        var tracking = Math.Round(look.Tracking * 2) / 2;
        var key = string.Join('|', text, face, size, alignH, alignV, compress, look.Leading, tracking, Math.Round(item.Width), Math.Round(item.Height));
        if (_textPictures.TryGetValue(key, out var cached))
            return cached;

        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TypefaceFor(face), size, Brushes.White, 1.0);
        var line = FontLineFor(face) ?? new FontLine(formatted.Baseline / size, (formatted.Height - formatted.Baseline) / size);
        var lines = text.Count(c => c == '\n') + 1;
        if (lines > 1)
            formatted.LineHeight = LabelPlacement.BlockHeight(line, size) + look.Leading;

        var pieces = tracking != 0
            ? text.Select(c => new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TypefaceFor(face), size, Brushes.White, 1.0)).ToList()
            : null;
        var textWidth = pieces != null ? pieces.Sum(p => p.WidthIncludingTrailingWhitespace + tracking) : formatted.WidthIncludingTrailingWhitespace;
        var boxWidth = item.Width > 0 ? item.Width : textWidth + LabelPlacement.Gutter * 2;
        var room = Math.Max(1, boxWidth - LabelPlacement.Gutter * 2);
        var squeeze = compress && textWidth > room ? room / textWidth : 1;
        var left = alignH switch
        {
            "RIGHT" => boxWidth - LabelPlacement.Gutter - textWidth * squeeze,
            "CENTER" => (boxWidth - textWidth * squeeze) / 2,
            _ => LabelPlacement.Gutter,
        };

        // WPF's own line sits lower than the game's. The part above the box that this cuts off is empty space in the font.
        var baseline = LabelPlacement.Baseline(line, size, item.Height, alignV, lines, look.Leading);
        var top = baseline - formatted.Baseline;
        var boxHeight = Math.Max(item.Height, baseline + LabelPlacement.BlockHeight(line, size, lines, look.Leading) - line.Ascent * size);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(left, top));
            context.PushTransform(new ScaleTransform(squeeze, 1));
            if (pieces == null)
            {
                context.DrawText(formatted, new Point(0, 0));
            }
            else
            {
                double along = 0;
                foreach (var piece in pieces)
                {
                    context.DrawText(piece, new Point(along, 0));
                    along += piece.WidthIncludingTrailingWhitespace + tracking;
                }
            }
        }

        var width = Math.Max(1, (int)Math.Ceiling(boxWidth));
        var height = Math.Max(1, (int)Math.Ceiling(boxHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        return _textPictures[key] = new OverlayPicture(width, height, pixels);
    }

    private PlacedWidget? WidgetAt(Point point)
    {
        var screenX = point.X + _originX;
        var screenY = point.Y + _originY;
        for (var i = _drawn.Count - 1; i >= 0; i--)
        {
            var (layer, placed) = _drawn[i];
            if (layer.Alpha > 0.02 && layer.Inverse(screenX, screenY) is { } spot && layer.Picture.AlphaAt(spot.U, spot.V) > 12)
                return placed;
        }
        return null;
    }

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (_document == null)
            return;
        _pressedAt = e.GetPosition(this);
        var placed = WidgetAt(_pressedAt);
        _dragging = _pose == null && placed != null && placed.Target.Parent != null ? placed : null;
        _movedX = _movedY = 0;
        _moved = false;
        _selected = placed?.Target;
        WidgetChosen?.Invoke(placed?.Target);
        Redraw();
        if (_dragging != null)
            CaptureMouse();
    }

    private void OnDrag(object sender, MouseEventArgs e)
    {
        if (_dragging == null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var now = e.GetPosition(this);
        var totalX = now.X - _pressedAt.X;
        var totalY = now.Y - _pressedAt.Y;
        if (!_moved && Math.Abs(totalX) < DragStart && Math.Abs(totalY) < DragStart)
            return;

        _moved = true;
        var scaleX = _dragging.ParentScaleX == 0 ? 1 : _dragging.ParentScaleX;
        var scaleY = _dragging.ParentScaleY == 0 ? 1 : _dragging.ParentScaleY;
        LayoutDocument.Move(_dragging.Target, Math.Round((totalX - _movedX) / scaleX), Math.Round((totalY - _movedY) / scaleY));
        _movedX += Math.Round((totalX - _movedX) / scaleX) * scaleX;
        _movedY += Math.Round((totalY - _movedY) / scaleY) * scaleY;
        Redraw();
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        var moved = _dragging != null && _moved;
        _dragging = null;
        if (moved)
            WidgetMoved?.Invoke();
    }
}
