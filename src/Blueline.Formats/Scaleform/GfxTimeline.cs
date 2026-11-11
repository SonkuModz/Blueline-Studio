using System.Buffers.Binary;
using System.Text;

namespace Blueline.Formats.Scaleform;

public enum GfxBlend { Normal, Add, Multiply, Screen }

/// <summary>One picture or colour block of a frame. A to D, X and Y take the block's corners (0 to 1) to stage pixels.</summary>
public sealed record GfxPiece(int ImageId, uint Rgb, double A, double B, double C, double D, double X, double Y, double Alpha, uint Tint, GfxBlend Blend,
    (double Left, double Top, double Right, double Bottom)? Clip);

/// <summary>
/// Plays a movie's timeline far enough to draw any frame from its shapes and external images. ActionScript does not
/// run here, so anything a movie's code moves, shows or stops is not reflected, and text is left out. See RESEARCH section 6.9.
/// </summary>
public sealed class GfxTimeline
{
    private const int TagEnd = 0, TagShowFrame = 1, TagShape1 = 2, TagRemove1 = 5, TagShape2 = 22, TagPlace2 = 26, TagRemove2 = 28, TagShape3 = 32,
        TagSprite = 39, TagPlace3 = 70, TagShape4 = 83, TagExternalImage = 1009;
    private const double Twips = 20;
    private const int NoBitmap = 0xFFFF;

    private abstract record Character;
    private sealed record Shape(double Left, double Top, double Right, double Bottom, int ImageId, uint Rgb, double Alpha) : Character;
    private sealed record Sprite(Clip Clip) : Character;

    private sealed class Clip
    {
        public List<List<Control>> Frames { get; } = [];
    }

    private abstract record Control(int Depth);
    private sealed record Remove(int Depth) : Control(Depth);
    private sealed record Place(int Depth, bool Move, int? Character, Matrix? Matrix, Colour? Colour, int? ClipDepth, GfxBlend? Blend) : Control(Depth);

    private readonly record struct Matrix(double A, double B, double C, double D, double X, double Y)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

        public Matrix Then(Matrix outer) => new(
            A * outer.A + B * outer.C,
            A * outer.B + B * outer.D,
            C * outer.A + D * outer.C,
            C * outer.B + D * outer.D,
            X * outer.A + Y * outer.C + outer.X,
            X * outer.B + Y * outer.D + outer.Y);
    }

    private readonly record struct Colour(double R, double G, double B, double A)
    {
        public static readonly Colour None = new(1, 1, 1, 1);
        public Colour Then(Colour outer) => new(R * outer.R, G * outer.G, B * outer.B, A * outer.A);
    }

    private sealed class Item
    {
        public int Character;
        public Matrix Matrix = Matrix.Identity;
        public Colour Colour = Colour.None;
        public int ClipDepth;
        public GfxBlend Blend;
        public int PlacedAt;
    }

    private readonly Dictionary<int, Character> _characters = [];
    private readonly Clip _root = new();
    private readonly Dictionary<int, string> _images = [];

    public double StageWidth { get; private set; }
    public double StageHeight { get; private set; }
    public double FrameRate { get; private set; }
    public int FrameCount => _root.Frames.Count;
    public IReadOnlyDictionary<int, string> Images => _images;

    public static GfxTimeline Parse(byte[] data)
    {
        var body = GfxMovie.Body(data);
        var timeline = new GfxTimeline();
        var bits = new BitReader(body, 0);
        var size = bits.Unsigned(5);
        var xMin = bits.Signed(size);
        var xMax = bits.Signed(size);
        var yMin = bits.Signed(size);
        var yMax = bits.Signed(size);
        timeline.StageWidth = (xMax - xMin) / Twips;
        timeline.StageHeight = (yMax - yMin) / Twips;
        var at = bits.ByteEnd;
        timeline.FrameRate = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at)) / 256.0;
        timeline.ReadTags(body, at + 4, body.Length, timeline._root);
        return timeline;
    }

    private void ReadTags(byte[] body, int position, int end, Clip clip)
    {
        var current = new List<Control>();
        while (position + 2 <= end)
        {
            var header = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(position));
            position += 2;
            var code = header >> 6;
            var length = header & 63;
            if (length == 63)
            {
                length = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(position));
                position += 4;
            }
            if (length < 0 || position + length > end)
                throw new InvalidDataException("The movie's tag list runs past its end.");

            switch (code)
            {
                case TagEnd:
                    if (current.Count > 0)
                        clip.Frames.Add(current);
                    return;
                case TagShowFrame:
                    clip.Frames.Add(current);
                    current = [];
                    break;
                case TagShape1 or TagShape2 or TagShape3 or TagShape4:
                    ReadShape(body, position, code);
                    break;
                case TagExternalImage:
                    _images[BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(position))] = GfxMovie.ReadExternalImage(body.AsSpan(position, length)).FileName;
                    break;
                case TagSprite:
                    var sprite = new Clip();
                    _characters[BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(position))] = new Sprite(sprite);
                    ReadTags(body, position + 4, position + length, sprite);
                    break;
                case TagPlace2:
                    current.Add(ReadPlace2(body, position));
                    break;
                case TagPlace3:
                    current.Add(ReadPlace3(body, position));
                    break;
                case TagRemove1:
                    current.Add(new Remove(BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(position + 2))));
                    break;
                case TagRemove2:
                    current.Add(new Remove(BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(position))));
                    break;
            }
            position += length;
        }
        if (current.Count > 0)
            clip.Frames.Add(current);
    }

    /// <summary>Only the bounds and first fill are needed: every shape in the game's movies is one picture or one colour on a rectangle.</summary>
    private void ReadShape(byte[] body, int position, int code)
    {
        var id = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(position));
        var bits = new BitReader(body, position + 2);
        var size = bits.Unsigned(5);
        var left = bits.Signed(size) / Twips;
        var right = bits.Signed(size) / Twips;
        var top = bits.Signed(size) / Twips;
        var bottom = bits.Signed(size) / Twips;
        var at = bits.ByteEnd;
        if (code == TagShape4)
        {
            var edges = new BitReader(body, at);
            var edgeSize = edges.Unsigned(5);
            for (var i = 0; i < 4; i++)
                edges.Signed(edgeSize);
            at = edges.ByteEnd + 1;
        }

        int fills = body[at++];
        if (fills == 255 && code != TagShape1)
        {
            fills = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at));
            at += 2;
        }

        var hasAlpha = code is TagShape3 or TagShape4;
        var (image, rgb, alpha) = (-1, 0xFFFFFFu, 0.0);
        for (var i = 0; i < fills && alpha == 0 && image < 0; i++)
        {
            int type = body[at++];
            if (type == 0)
            {
                rgb = (uint)((body[at] << 16) | (body[at + 1] << 8) | body[at + 2]);
                alpha = hasAlpha ? body[at + 3] / 255.0 : 1;
                at += hasAlpha ? 4 : 3;
            }
            else if (type is >= 0x40 and <= 0x43)
            {
                var bitmap = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at));
                var matrix = new BitReader(body, at + 2);
                ReadMatrix(ref matrix);
                at = matrix.ByteEnd;
                if (bitmap != NoBitmap)
                {
                    image = bitmap;
                    alpha = 1;
                }
            }
            else if (type is 0x10 or 0x12 or 0x13)
            {
                (rgb, alpha, at) = MiddleOfGradient(body, at, hasAlpha, type == 0x13);
            }
            else
            {
                break;
            }
        }
        _characters[id] = new Shape(left, top, right, bottom, image, rgb, alpha);
    }

    private static (uint Rgb, double Alpha, int End) MiddleOfGradient(byte[] body, int at, bool hasAlpha, bool focal)
    {
        var bits = new BitReader(body, at);
        ReadMatrix(ref bits);
        at = bits.ByteEnd;
        var stops = body[at++] & 15;
        var size = hasAlpha ? 5 : 4;
        var end = at + stops * size + (focal ? 2 : 0);
        if (stops == 0)
            return (0xFFFFFF, 0, end);
        var entry = at + (stops / 2) * size + 1;
        return ((uint)((body[entry] << 16) | (body[entry + 1] << 8) | body[entry + 2]), hasAlpha ? body[entry + 3] / 255.0 : 1, end);
    }

    private static Place ReadPlace2(byte[] body, int position)
    {
        var flags = body[position];
        var at = position + 1;
        var (depth, character, matrix, colour, clipDepth) = ReadPlaceFields(body, ref at, flags);
        return new Place(depth, (flags & 1) != 0, character, matrix, colour, clipDepth, null);
    }

    private static Place ReadPlace3(byte[] body, int position)
    {
        var flags = body[position];
        var extra = body[position + 1];
        var at = position + 2;
        var hasClassName = (extra & 8) != 0 || ((extra & 16) != 0 && (flags & 2) != 0);

        var depth = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at));
        var fieldsAt = at + 2;
        if (hasClassName)
            SkipString(body, ref fieldsAt);
        var (_, character, matrix, colour, clipDepth) = ReadPlaceBody(body, ref fieldsAt, flags);

        GfxBlend? blend = null;
        var hasFilters = (extra & 1) != 0;
        if ((extra & 2) != 0 && !hasFilters)
            blend = body[fieldsAt] switch { 8 => GfxBlend.Add, 3 => GfxBlend.Multiply, 4 => GfxBlend.Screen, _ => GfxBlend.Normal };
        return new Place(depth, (flags & 1) != 0, character, matrix, colour, clipDepth, blend);
    }

    private static (int Depth, int? Character, Matrix? Matrix, Colour? Colour, int? ClipDepth) ReadPlaceFields(byte[] body, ref int at, byte flags)
    {
        var depth = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at));
        at += 2;
        var (_, character, matrix, colour, clip) = ReadPlaceBody(body, ref at, flags);
        return (depth, character, matrix, colour, clip);
    }

    private static (int Unused, int? Character, Matrix? Matrix, Colour? Colour, int? ClipDepth) ReadPlaceBody(byte[] body, ref int at, byte flags)
    {
        int? character = null;
        if ((flags & 2) != 0)
        {
            character = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at));
            at += 2;
        }

        Matrix? matrix = null;
        if ((flags & 4) != 0)
        {
            var bits = new BitReader(body, at);
            matrix = ReadMatrix(ref bits);
            at = bits.ByteEnd;
        }

        Colour? colour = null;
        if ((flags & 8) != 0)
        {
            var bits = new BitReader(body, at);
            var hasAdd = bits.Unsigned(1) == 1;
            var hasMultiply = bits.Unsigned(1) == 1;
            var size = bits.Unsigned(4);
            var value = Colour.None;
            if (hasMultiply)
                value = new Colour(bits.Signed(size) / 256.0, bits.Signed(size) / 256.0, bits.Signed(size) / 256.0, bits.Signed(size) / 256.0);
            if (hasAdd)
                for (var i = 0; i < 4; i++)
                    bits.Signed(size);
            colour = value;
            at = bits.ByteEnd;
        }

        if ((flags & 16) != 0)
            at += 2;
        if ((flags & 32) != 0)
            SkipString(body, ref at);

        int? clipDepth = null;
        if ((flags & 64) != 0)
        {
            clipDepth = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at));
            at += 2;
        }
        return (0, character, matrix, colour, clipDepth);
    }

    private static void SkipString(byte[] body, ref int at)
    {
        while (body[at] != 0)
            at++;
        at++;
    }

    private static Matrix ReadMatrix(ref BitReader bits)
    {
        double a = 1, d = 1, b = 0, c = 0;
        if (bits.Unsigned(1) == 1)
        {
            var size = bits.Unsigned(5);
            a = bits.Signed(size) / 65536.0;
            d = bits.Signed(size) / 65536.0;
        }
        if (bits.Unsigned(1) == 1)
        {
            var size = bits.Unsigned(5);
            b = bits.Signed(size) / 65536.0;
            c = bits.Signed(size) / 65536.0;
        }
        var moveSize = bits.Unsigned(5);
        var x = bits.Signed(moveSize) / Twips;
        var y = bits.Signed(moveSize) / Twips;
        return new Matrix(a, b, c, d, x, y);
    }

    /// <summary>What is on stage at a frame, bottom first.</summary>
    public IReadOnlyList<GfxPiece> Frame(int frame)
    {
        var pieces = new List<GfxPiece>();
        Draw(_root, frame, Matrix.Identity, Colour.None, GfxBlend.Normal, null, pieces, 0);
        return pieces;
    }

    private void Draw(Clip clip, int frame, Matrix outer, Colour outerColour, GfxBlend outerBlend, (double, double, double, double)? outerClip, List<GfxPiece> pieces, int depth)
    {
        if (depth > 16 || clip.Frames.Count == 0)
            return;

        var list = Evaluate(clip, frame);
        (double Left, double Top, double Right, double Bottom)? mask = null;
        var maskUntil = -1;
        foreach (var (at, item) in list.OrderBy(p => p.Key))
        {
            if (mask != null && at > maskUntil)
                mask = null;
            var matrix = item.Matrix.Then(outer);
            if (item.ClipDepth > 0)
            {
                if (_characters.GetValueOrDefault(item.Character) is Shape s)
                {
                    mask = Bounds(s, matrix);
                    maskUntil = item.ClipDepth;
                }
                continue;
            }

            var colour = item.Colour.Then(outerColour);
            var blend = item.Blend != GfxBlend.Normal ? item.Blend : outerBlend;
            var active = Intersect(mask, outerClip);
            switch (_characters.GetValueOrDefault(item.Character))
            {
                case Shape shape:
                    AddShape(shape, matrix, colour, blend, active, pieces);
                    break;
                case Sprite sprite:
                    var count = sprite.Clip.Frames.Count;
                    Draw(sprite.Clip, count == 0 ? 0 : (frame - item.PlacedAt) % count, matrix, colour, blend, active, pieces, depth + 1);
                    break;
            }
        }
    }

    private static Dictionary<int, Item> Evaluate(Clip clip, int frame)
    {
        var list = new Dictionary<int, Item>();
        var last = Math.Clamp(frame, 0, clip.Frames.Count - 1);
        for (var f = 0; f <= last; f++)
        {
            foreach (var control in clip.Frames[f])
            {
                if (control is Remove)
                {
                    list.Remove(control.Depth);
                    continue;
                }

                var place = (Place)control;
                if (!place.Move || !list.TryGetValue(place.Depth, out var item))
                {
                    item = new Item { PlacedAt = f };
                    list[place.Depth] = item;
                }
                else if (place.Character is not null)
                {
                    item.PlacedAt = f;
                }

                item.Character = place.Character ?? item.Character;
                item.Matrix = place.Matrix ?? item.Matrix;
                item.Colour = place.Colour ?? item.Colour;
                item.ClipDepth = place.ClipDepth ?? item.ClipDepth;
                item.Blend = place.Blend ?? item.Blend;
            }
        }
        return list;
    }

    private static (double, double, double, double) Bounds(Shape shape, Matrix matrix)
    {
        (double X, double Y) Corner(double x, double y) => (x * matrix.A + y * matrix.C + matrix.X, x * matrix.B + y * matrix.D + matrix.Y);
        var corners = new[] { Corner(shape.Left, shape.Top), Corner(shape.Right, shape.Top), Corner(shape.Left, shape.Bottom), Corner(shape.Right, shape.Bottom) };
        return (corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
    }

    private static (double Left, double Top, double Right, double Bottom)? Intersect((double Left, double Top, double Right, double Bottom)? a, (double Left, double Top, double Right, double Bottom)? b)
    {
        if (a is not { } first)
            return b;
        if (b is not { } second)
            return a;
        return (Math.Max(first.Left, second.Left), Math.Max(first.Top, second.Top), Math.Min(first.Right, second.Right), Math.Min(first.Bottom, second.Bottom));
    }

    private static void AddShape(Shape shape, Matrix matrix, Colour colour, GfxBlend blend, (double, double, double, double)? clip, List<GfxPiece> pieces)
    {
        if (shape.Alpha <= 0 || colour.A <= 0)
            return;
        var width = shape.Right - shape.Left;
        var height = shape.Bottom - shape.Top;
        var tint = ((uint)(Math.Clamp(colour.R, 0, 1) * 255) << 16) | ((uint)(Math.Clamp(colour.G, 0, 1) * 255) << 8) | (uint)(Math.Clamp(colour.B, 0, 1) * 255);
        pieces.Add(new GfxPiece(shape.ImageId, shape.Rgb,
            width * matrix.A, width * matrix.B, height * matrix.C, height * matrix.D,
            shape.Left * matrix.A + shape.Top * matrix.C + matrix.X,
            shape.Left * matrix.B + shape.Top * matrix.D + matrix.Y,
            Math.Clamp(colour.A * shape.Alpha, 0, 1), tint, blend, clip));
    }

    private struct BitReader(byte[] data, int start)
    {
        private readonly byte[] _data = data;
        private int _byte = start;
        private int _bit;

        public int ByteEnd => _bit == 0 ? _byte : _byte + 1;

        public int Unsigned(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                value = (value << 1) | ((_data[_byte] >> (7 - _bit)) & 1);
                if (++_bit == 8)
                {
                    _bit = 0;
                    _byte++;
                }
            }
            return value;
        }

        public int Signed(int count)
        {
            if (count == 0)
                return 0;
            var value = Unsigned(count);
            return (value & (1 << (count - 1))) != 0 ? value - (1 << count) : value;
        }
    }
}
