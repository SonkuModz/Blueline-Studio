using System.Collections.Concurrent;
using System.Numerics;
using Blueline.Formats.Models;
using Blueline.Formats.Textures;

namespace Blueline.Core.Teams;

/// <summary>
/// Where each texel of one brand's jersey layout reads from another brand's layout, made through the jerseys' shared 3D shape.
/// <see cref="Source"/> holds a source UV pair per texel; <see cref="Turn"/> holds a 2x2 per texel that turns a normal map's
/// red and green from the source layout's directions into this one's.
/// </summary>
public sealed record JerseyRefitMap(string From, string To, int Size, float[] Source, float[] Turn, bool[] Covered);

/// <summary>
/// Fits a colour sheet drawn for one brand's jersey onto another brand's jersey. NHL 27's "rbk" sheets, for one, are drawn in
/// NHL 19's adidas layout: put straight into an rbk slot, their dark collar patch lands under the arm. For every texel the
/// target jersey covers, this finds the same spot on the source jersey (closest point, with the surfaces facing the same way)
/// and copies it, so stripes and logos stretch to fit. The stretch is smoothed so logos bend rather than break where
/// neighbouring texels land on different triangles; real seams stay sharp. Seen working in game on 185 kits (2026-11-16).
/// </summary>
public static class JerseyRefit
{
    public const int DefaultSize = 2048;
    private const string Folder = "nhl/data/kettle/rendering/characters/player/";
    private static readonly ConcurrentDictionary<(string, string, int), Lazy<JerseyRefitMap>> Cache = new();

    public static readonly IReadOnlyList<string> Brands = ["adidas", "rbk", "nineties"];

    public static string MeshPath(string brand) => $"{Folder}jersey_skater_{brand}_notuck_large_high.mcd";

    private sealed record Mesh(Vector3[] P, Vector2[] Uv, Vector3[] T, Vector3[] B, Vector3[] N)
    {
        public int Count => N.Length;
    }

    private static Mesh LoadMesh(Func<string, byte[]?> read, string brand)
    {
        var path = MeshPath(brand);
        var mcd = McdFile.Parse(read(path) ?? throw new InvalidDataException($"{path} is not in the game."));
        var (p, uv) = (new List<Vector3>(), new List<Vector2>());
        foreach (var shape in mcd.Shapes)
        {
            var uvs = mcd.ReadUvs(shape);
            if (uvs == null)
                continue;
            var pos = mcd.ReadPositions(mcd.Streams[shape.Stream]);
            var idx = mcd.ReadIndices(mcd.IndexBuffers[shape.IndexBuffer]);
            for (var i = 0; i + 2 < idx.Length && i / 3 < shape.Triangles; i += 3)
            {
                if (idx[i] >= uvs.Length || idx[i + 1] >= uvs.Length || idx[i + 2] >= uvs.Length ||
                    idx[i] >= pos.Length || idx[i + 1] >= pos.Length || idx[i + 2] >= pos.Length)
                    continue;
                for (var k = 0; k < 3; k++)
                {
                    p.Add(pos[idx[i + k]]);
                    uv.Add(uvs[idx[i + k]]);
                }
            }
        }
        return Frames(p.ToArray(), uv.ToArray());
    }

    private static Mesh Frames(Vector3[] p, Vector2[] uv)
    {
        var n = p.Length / 3;
        var (t, b, nn) = (new Vector3[n], new Vector3[n], new Vector3[n]);
        for (var i = 0; i < n; i++)
        {
            var (e1, e2) = (p[i * 3 + 1] - p[i * 3], p[i * 3 + 2] - p[i * 3]);
            var (d1, d2) = (uv[i * 3 + 1] - uv[i * 3], uv[i * 3 + 2] - uv[i * 3]);
            var det = d1.X * d2.Y - d2.X * d1.Y;
            if (MathF.Abs(det) < 1e-12f)
                det = 1e-12f;
            t[i] = Unit((e1 * d2.Y - e2 * d1.Y) / det);
            b[i] = Unit((e2 * d1.X - e1 * d2.X) / det);
            nn[i] = Unit(Vector3.Cross(e1, e2));
        }
        return new Mesh(p, uv, t, b, nn);
    }

    private static Vector3 Unit(Vector3 v) => v.Length() is var l and > 1e-12f ? v / l : Vector3.Zero;

    /// <summary>The map from one brand's layout to another's, built once per size and kept.</summary>
    public static JerseyRefitMap Map(Func<string, byte[]?> read, string from, string to, int size = DefaultSize) =>
        Cache.GetOrAdd((from, to, size), key => new Lazy<JerseyRefitMap>(() => Build(read, key.Item1, key.Item2, key.Item3))).Value;

    public static JerseyRefitMap Build(Func<string, byte[]?> read, string from, string to, int size = DefaultSize)
    {
        var source = LoadMesh(read, from);
        var target = LoadMesh(read, to);

        // The jerseys share a pose but not a cut: fit the target's bounding box onto the source's.
        var (sl, sh) = Bounds(source.P);
        var (tl, th) = Bounds(target.P);
        var fitted = target.P.Select(v => (v - tl) / Vector3.Max(th - tl, new Vector3(1e-6f)) * (sh - sl) + sl).ToArray();
        target = target with { P = fitted };

        var grid = new TriangleGrid(source, 0.02f);
        var count = size * size;
        var position = new Vector3[count];
        var triangle = new int[count];
        Array.Fill(triangle, -1);
        Rasterize(target, size, position, triangle);

        var src = new float[count * 2];
        var turn = new float[count * 4];
        Parallel.For(0, size, y =>
        {
            for (var x = 0; x < size; x++)
            {
                var i = y * size + x;
                var t = triangle[i];
                if (t < 0)
                    continue;
                var (s, uv) = grid.Closest(position[i], target.N[t]);
                src[i * 2] = uv.X;
                src[i * 2 + 1] = uv.Y;
                // A normal (x, y) in the source's tangent frame is x*Ts + y*Bs in 3D; read it back along this layout's frame.
                var (ts, bs, tt, bt) = (source.T[s], source.B[s], target.T[t], target.B[t]);
                turn[i * 4] = Vector3.Dot(ts, tt);
                turn[i * 4 + 1] = Vector3.Dot(bs, tt);
                turn[i * 4 + 2] = Vector3.Dot(ts, bt);
                turn[i * 4 + 3] = Vector3.Dot(bs, bt);
            }
        });

        var covered = triangle.Select(t => t >= 0).ToArray();
        var exact = (float[])src.Clone();
        Smooth(src, covered, size);
        KeepOnSource(src, exact, covered, source, size);
        for (var c = 0; c < 4; c++)
        {
            var channel = Channel(turn, 4, c, count);
            var smoothed = NormalizedBlur(channel, covered, size, 6.0 * size / DefaultSize);
            for (var i = 0; i < count; i++)
                if (covered[i])
                    turn[i * 4 + c] = smoothed[i];
        }
        PadAndFill(src, turn, covered, size, Math.Max(2, 8 * size / DefaultSize));
        return new JerseyRefitMap(from, to, size, src, turn, covered);
    }

    private static (Vector3 Low, Vector3 High) Bounds(Vector3[] p) =>
        (p.Aggregate(new Vector3(float.MaxValue), Vector3.Min), p.Aggregate(new Vector3(float.MinValue), Vector3.Max));

    private static void Rasterize(Mesh mesh, int size, Vector3[] position, int[] triangle)
    {
        for (var t = 0; t < mesh.Count; t++)
        {
            var (a, b, c) = (mesh.Uv[t * 3] * size, mesh.Uv[t * 3 + 1] * size, mesh.Uv[t * 3 + 2] * size);
            var (v0, v1) = (b - a, c - a);
            var d = v0.X * v1.Y - v1.X * v0.Y;
            if (MathF.Abs(d) < 1e-9f)
                continue;
            var x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
            var x1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            var y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
            var y1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    var w = new Vector2(x + 0.5f, y + 0.5f) - a;
                    var l1 = (w.X * v1.Y - v1.X * w.Y) / d;
                    var l2 = (v0.X * w.Y - w.X * v0.Y) / d;
                    var l0 = 1 - l1 - l2;
                    if (l0 < -1e-3f || l1 < -1e-3f || l2 < -1e-3f)
                        continue;
                    var i = y * size + x;
                    position[i] = mesh.P[t * 3] * l0 + mesh.P[t * 3 + 1] * l1 + mesh.P[t * 3 + 2] * l2;
                    triangle[i] = t;
                }
        }
    }

    /// <summary>Source triangles bucketed in a 3D grid, for closest-point lookups.</summary>
    private sealed class TriangleGrid
    {
        private readonly Mesh _mesh;
        private readonly float _cell;
        private readonly Vector3 _low;
        private readonly Dictionary<(int, int, int), List<int>> _cells = new();

        public TriangleGrid(Mesh mesh, float cell)
        {
            _mesh = mesh;
            _cell = cell;
            _low = Bounds(mesh.P).Low;
            for (var t = 0; t < mesh.Count; t++)
            {
                var (lo, hi) = (Vector3.Min(mesh.P[t * 3], Vector3.Min(mesh.P[t * 3 + 1], mesh.P[t * 3 + 2])), Vector3.Max(mesh.P[t * 3], Vector3.Max(mesh.P[t * 3 + 1], mesh.P[t * 3 + 2])));
                var (a, b) = (Key(lo), Key(hi));
                for (var x = a.Item1; x <= b.Item1; x++)
                    for (var y = a.Item2; y <= b.Item2; y++)
                        for (var z = a.Item3; z <= b.Item3; z++)
                        {
                            if (!_cells.TryGetValue((x, y, z), out var list))
                                _cells[(x, y, z)] = list = [];
                            list.Add(t);
                        }
            }
        }

        private (int, int, int) Key(Vector3 v)
        {
            var k = (v - _low) / _cell;
            return ((int)MathF.Floor(k.X), (int)MathF.Floor(k.Y), (int)MathF.Floor(k.Z));
        }

        /// <summary>The closest point facing the same way (either side), or the closest at all if none does nearby.</summary>
        public (int Triangle, Vector2 Uv) Closest(Vector3 p, Vector3 normal)
        {
            var centre = Key(p);
            var (best, bestAny) = ((-1, Vector2.Zero, float.MaxValue), (-1, Vector2.Zero, float.MaxValue));
            for (var ring = 1; ring <= 8; ring++)
            {
                for (var x = centre.Item1 - ring; x <= centre.Item1 + ring; x++)
                    for (var y = centre.Item2 - ring; y <= centre.Item2 + ring; y++)
                        for (var z = centre.Item3 - ring; z <= centre.Item3 + ring; z++)
                        {
                            // Only the new shell of this ring; inner cells were done already.
                            if (ring > 1 && Math.Abs(x - centre.Item1) < ring && Math.Abs(y - centre.Item2) < ring && Math.Abs(z - centre.Item3) < ring)
                                continue;
                            if (!_cells.TryGetValue((x, y, z), out var list))
                                continue;
                            foreach (var t in list)
                            {
                                var (q, bary) = ClosestOnTriangle(p, _mesh.P[t * 3], _mesh.P[t * 3 + 1], _mesh.P[t * 3 + 2]);
                                var d = Vector3.DistanceSquared(p, q);
                                if (d >= best.Item3 && d >= bestAny.Item3)
                                    continue;
                                var uv = _mesh.Uv[t * 3] * bary.X + _mesh.Uv[t * 3 + 1] * bary.Y + _mesh.Uv[t * 3 + 2] * bary.Z;
                                if (d < bestAny.Item3)
                                    bestAny = (t, uv, d);
                                if (d < best.Item3 && MathF.Abs(Vector3.Dot(_mesh.N[t], normal)) > 0.5f)
                                    best = (t, uv, d);
                            }
                        }
                // Anything outside the searched block is at least ring * cell away.
                var reach = ring * _cell;
                if (best.Item1 >= 0 && best.Item3 <= reach * reach)
                    break;
            }
            return best.Item1 >= 0 ? (best.Item1, best.Item2) : (Math.Max(bestAny.Item1, 0), bestAny.Item2);
        }
    }

    /// <summary>Closest point on triangle abc to p, with its barycentric weights (Ericson, Real-Time Collision Detection 5.1.5).</summary>
    private static (Vector3 Point, Vector3 Bary) ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return (a, new Vector3(1, 0, 0));
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return (b, new Vector3(0, 1, 0));
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            var v = d1 / (d1 - d3);
            return (a + v * ab, new Vector3(1 - v, v, 0));
        }
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return (c, new Vector3(0, 0, 1));
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            var w = d2 / (d2 - d6);
            return (a + w * ac, new Vector3(1 - w, 0, w));
        }
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            var w = (d4 - d3) / (d4 - d3 + (d5 - d6));
            return (b + w * (c - b), new Vector3(0, 1 - w, w));
        }
        var denom = 1 / (va + vb + vc);
        float vv = vb * denom, ww = vc * denom;
        return (a + ab * vv + ac * ww, new Vector3(1 - vv - ww, vv, ww));
    }

    private static float[] Channel(float[] data, int stride, int c, int count)
    {
        var o = new float[count];
        for (var i = 0; i < count; i++)
            o[i] = data[i * stride + c];
        return o;
    }

    /// <summary>
    /// Smooths where each texel reads from, so stripes stay straight and logos bend rather than break. The covered texels
    /// are split into pieces wherever neighbours read from places far apart (a real seam: pieces that touch on this sheet
    /// but not on the jersey). Each piece is smoothed on its own over a wide window, with a first-order correction so the
    /// stretch is not pulled at a piece's edges. Slivers too small to be a piece take their neighbours' smoothed reading.
    /// </summary>
    private static void Smooth(float[] src, bool[] covered, int size)
    {
        var count = size * size;
        var scale = (float)size / DefaultSize;
        var cut = 10f * scale / size;              // a jump of 10 texels at 2048 is a seam
        var sigma = 32.0 * scale;                  // smoothing window, texels at 2048
        var minPiece = (int)(400 * scale * scale); // smaller pieces are slivers from folds, not real pieces

        var offset = new[] { new float[count], new float[count] };
        for (var i = 0; i < count; i++)
            if (covered[i])
            {
                offset[0][i] = src[i * 2] - (i % size + 0.5f) / size;
                offset[1][i] = src[i * 2 + 1] - (i / size + 0.5f) / size;
            }

        // Pieces: join neighbours that read from nearby spots.
        var parent = new int[count];
        for (var i = 0; i < count; i++)
            parent[i] = i;
        int Find(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }
        bool Near(int a, int b) => Math.Abs(offset[0][a] - offset[0][b]) < cut && Math.Abs(offset[1][a] - offset[1][b]) < cut;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var i = y * size + x;
                if (!covered[i])
                    continue;
                if (x + 1 < size && covered[i + 1] && Near(i, i + 1))
                    parent[Find(i + 1)] = Find(i);
                if (y + 1 < size && covered[i + size] && Near(i, i + size))
                    parent[Find(i + size)] = Find(i);
            }
        var pieces = new Dictionary<int, List<int>>();
        for (var i = 0; i < count; i++)
            if (covered[i])
            {
                var root = Find(i);
                if (!pieces.TryGetValue(root, out var list))
                    pieces[root] = list = [];
                list.Add(i);
            }

        var smoothed = new[] { new float[count], new float[count] };
        var done = new bool[count];
        Parallel.ForEach(pieces.Values.Where(p => p.Count >= minPiece), piece =>
        {
            int x0 = size, y0 = size, x1 = 0, y1 = 0;
            foreach (var i in piece)
            {
                x0 = Math.Min(x0, i % size); x1 = Math.Max(x1, i % size);
                y0 = Math.Min(y0, i / size); y1 = Math.Max(y1, i / size);
            }
            var (w, h) = (x1 - x0 + 1, y1 - y0 + 1);
            var mask = new float[w * h];
            foreach (var i in piece)
                mask[(i / size - y0) * w + i % size - x0] = 1;
            var weight = BoxBlur3(mask, w, h, sigma);
            float[] Mean(Func<int, int, float> value)
            {
                var a = new float[w * h];
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                        if (mask[y * w + x] > 0)
                            a[y * w + x] = value(x, y);
                var blurred = BoxBlur3(a, w, h, sigma);
                for (var k = 0; k < a.Length; k++)
                    blurred[k] = weight[k] > 1e-6f ? blurred[k] / weight[k] : 0;
                return blurred;
            }
            var cx = Mean((x, _) => x);
            var cy = Mean((_, y) => y);
            for (var c = 0; c < 2; c++)
            {
                var f = Mean((x, y) => offset[c][(y + y0) * size + x + x0]);
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        var k = y * w + x;
                        if (mask[k] == 0)
                            continue;
                        // Slope of the smoothed field, one-sided where the piece ends.
                        float Slope(int dx, int dy)
                        {
                            var (xa, ya, xb, yb) = (x - dx, y - dy, x + dx, y + dy);
                            var hasA = xa >= 0 && ya >= 0 && mask[ya * w + xa] > 0;
                            var hasB = xb < w && yb < h && mask[yb * w + xb] > 0;
                            return hasA && hasB ? (f[yb * w + xb] - f[ya * w + xa]) / 2
                                : hasB ? f[yb * w + xb] - f[k]
                                : hasA ? f[k] - f[ya * w + xa] : 0;
                        }
                        // The window's mean sits off-centre near the edge; step from there back to this texel.
                        smoothed[c][(y + y0) * size + x + x0] = f[k] + Slope(1, 0) * (x - cx[k]) + Slope(0, 1) * (y - cy[k]);
                    }
            }
            foreach (var i in piece)
                done[i] = true;
        });

        // Slivers: the smoothed reading of the pieces around them.
        var have = new float[count];
        for (var i = 0; i < count; i++)
            have[i] = done[i] ? 1 : 0;
        var haveBlur = BoxBlur3(have, size, size, 8 * scale);
        var fill = new float[2][];
        for (var c = 0; c < 2; c++)
        {
            var v = new float[count];
            for (var i = 0; i < count; i++)
                v[i] = done[i] ? smoothed[c][i] : 0;
            fill[c] = BoxBlur3(v, size, size, 8 * scale);
        }
        for (var i = 0; i < count; i++)
        {
            if (!covered[i])
                continue;
            var (ox, oy) = done[i] ? (smoothed[0][i], smoothed[1][i])
                : haveBlur[i] > 1e-3f ? (fill[0][i] / haveBlur[i], fill[1][i] / haveBlur[i])
                : (offset[0][i], offset[1][i]);
            src[i * 2] = ox + (i % size + 0.5f) / size;
            src[i * 2 + 1] = oy + (i / size + 0.5f) / size;
        }
    }

    /// <summary>
    /// Smoothing can carry a texel at a piece's edge just past the edge of the source jersey on the sheet, where the sheet
    /// holds whatever lies around it (NHL 27's dark collar patch, for one). Such a texel moves back toward its exact reading,
    /// which always lies on the source jersey, only as far as it must.
    /// </summary>
    private static void KeepOnSource(float[] src, float[] exact, bool[] covered, Mesh source, int size)
    {
        var onSource = new int[size * size];
        Array.Fill(onSource, -1);
        Rasterize(source, size, new Vector3[size * size], onSource);
        bool On(float u, float v)
        {
            var (x, y) = ((int)(u * size), (int)(v * size));
            return (uint)x < (uint)size && (uint)y < (uint)size && onSource[y * size + x] >= 0;
        }
        Parallel.For(0, size * size, i =>
        {
            if (!covered[i] || On(src[i * 2], src[i * 2 + 1]))
                return;
            var (eu, ev, su, sv) = (exact[i * 2], exact[i * 2 + 1], src[i * 2], src[i * 2 + 1]);
            float lo = 0, hi = 1; // share of the smoothing kept
            for (var step = 0; step < 12; step++)
            {
                var mid = (lo + hi) / 2;
                if (On(eu + (su - eu) * mid, ev + (sv - ev) * mid))
                    lo = mid;
                else
                    hi = mid;
            }
            src[i * 2] = eu + (su - eu) * lo;
            src[i * 2 + 1] = ev + (sv - ev) * lo;
        });
    }

    /// <summary>Three box blurs in each direction, close to a Gaussian of this sigma, at a cost that does not grow with it.</summary>
    private static float[] BoxBlur3(float[] input, int w, int h, double sigma)
    {
        var radius = Math.Max(1, (int)Math.Round((Math.Sqrt(4 * sigma * sigma + 1) - 1) / 2));
        var a = input;
        for (var pass = 0; pass < 3; pass++)
        {
            a = BoxLine(a, w, h, radius, horizontal: true);
            a = BoxLine(a, w, h, radius, horizontal: false);
        }
        return a;
    }

    private static float[] BoxLine(float[] input, int w, int h, int r, bool horizontal)
    {
        var output = new float[input.Length];
        var (lines, length) = horizontal ? (h, w) : (w, h);
        Parallel.For(0, lines, line =>
        {
            int At(int k) => horizontal ? line * w + k : k * w + line;
            double sum = 0;
            for (var k = 0; k <= r && k < length; k++)
                sum += input[At(k)];
            for (var k = 0; k < length; k++)
            {
                output[At(k)] = (float)sum;
                var (add, drop) = (k + r + 1, k - r);
                if (add < length)
                    sum += input[At(add)];
                if (drop >= 0)
                    sum -= input[At(drop)];
            }
        });
        return output;
    }

    /// <summary>Gaussian blur over the covered texels only, weighted so uncovered ones do not pull values toward zero.</summary>
    private static float[] NormalizedBlur(float[] values, bool[] covered, int size, double sigma)
    {
        var radius = (int)Math.Ceiling(sigma * 3);
        var kernel = Enumerable.Range(-radius, radius * 2 + 1).Select(k => (float)Math.Exp(-k * k / (2 * sigma * sigma))).ToArray();
        var count = size * size;
        var (num, den) = (new float[count], new float[count]);
        for (var i = 0; i < count; i++)
            if (covered[i])
            {
                num[i] = values[i];
                den[i] = 1;
            }
        num = Separable(num, size, kernel, radius);
        den = Separable(den, size, kernel, radius);
        var output = new float[count];
        for (var i = 0; i < count; i++)
            output[i] = den[i] > 1e-6f ? num[i] / den[i] : values[i];
        return output;
    }

    private static float[] Separable(float[] input, int size, float[] kernel, int radius)
    {
        var pass = new float[input.Length];
        Parallel.For(0, size, y =>
        {
            for (var x = 0; x < size; x++)
            {
                float s = 0;
                for (int k = -radius, xx = x - radius; k <= radius; k++, xx++)
                    if ((uint)xx < (uint)size)
                        s += input[y * size + xx] * kernel[k + radius];
                pass[y * size + x] = s;
            }
        });
        var output = new float[input.Length];
        Parallel.For(0, size, y =>
        {
            for (var x = 0; x < size; x++)
            {
                float s = 0;
                for (int k = -radius, yy = y - radius; k <= radius; k++, yy++)
                    if ((uint)yy < (uint)size)
                        s += pass[yy * size + x] * kernel[k + radius];
                output[y * size + x] = s;
            }
        });
        return output;
    }

    /// <summary>
    /// Grows the covered area a few texels so mip levels and filtering at the mesh's edges read the right colour; past that
    /// a texel reads its own spot, so the sheet's unused areas are kept as they were.
    /// </summary>
    private static void PadAndFill(float[] src, float[] turn, bool[] covered, int size, int pad)
    {
        var count = size * size;
        var from = new int[count];
        for (var i = 0; i < count; i++)
            from[i] = covered[i] ? i : -1;
        var frontier = Enumerable.Range(0, count).Where(i => covered[i]).ToList();
        for (var ring = 0; ring < pad && frontier.Count > 0; ring++)
        {
            var next = new List<int>();
            foreach (var i in frontier)
            {
                var (x, y) = (i % size, i / size);
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var (nx, ny) = (x + dx, y + dy);
                    if ((uint)nx >= (uint)size || (uint)ny >= (uint)size)
                        continue;
                    var n = ny * size + nx;
                    if (from[n] >= 0)
                        continue;
                    from[n] = from[i];
                    next.Add(n);
                }
            }
            frontier = next;
        }
        for (var i = 0; i < count; i++)
        {
            if (covered[i])
                continue;
            if (from[i] >= 0)
            {
                src[i * 2] = src[from[i] * 2];
                src[i * 2 + 1] = src[from[i] * 2 + 1];
                Array.Copy(turn, from[i] * 4, turn, i * 4, 4);
            }
            else
            {
                src[i * 2] = (i % size + 0.5f) / size;
                src[i * 2 + 1] = (i / size + 0.5f) / size;
                turn[i * 4] = 1;
                turn[i * 4 + 1] = 0;
                turn[i * 4 + 2] = 0;
                turn[i * 4 + 3] = 1;
            }
        }
    }

    private static int MapIndex(JerseyRefitMap map, int x, int y, int width, int height) =>
        (int)((long)y * map.Size / height) * map.Size + (int)((long)x * map.Size / width);

    /// <summary>A colour sheet drawn for the map's source layout, fitted to its target layout, at the sheet's own size.</summary>
    public static RgbaImage Colour(JerseyRefitMap map, RgbaImage sheet)
    {
        var (w, h) = (sheet.Width, sheet.Height);
        var output = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            Span<float> px = stackalloc float[4];
            for (var x = 0; x < w; x++)
            {
                var m = MapIndex(map, x, y, w, h);
                Sample(sheet, map.Source[m * 2], map.Source[m * 2 + 1], px);
                for (var c = 0; c < 4; c++)
                    output[(y * w + x) * 4 + c] = (byte)Math.Clamp(MathF.Round(px[c]), 0, 255);
            }
        });
        return new RgbaImage(w, h, output);
    }

    /// <summary>
    /// A normal map drawn for the source layout, fitted to the target layout: read from the same spot as the colour, with
    /// red and green turned into this layout's directions and their strength kept. Blue is left empty, as the game's are.
    /// </summary>
    public static RgbaImage Normal(JerseyRefitMap map, RgbaImage sheet)
    {
        var (w, h) = (sheet.Width, sheet.Height);
        var output = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            Span<float> px = stackalloc float[4];
            for (var x = 0; x < w; x++)
            {
                var m = MapIndex(map, x, y, w, h);
                Sample(sheet, map.Source[m * 2], map.Source[m * 2 + 1], px);
                var (nx, ny) = ((px[0] - 128) / 127, (px[1] - 128) / 127);
                var length = MathF.Sqrt(nx * nx + ny * ny);
                var tx = map.Turn[m * 4] * nx + map.Turn[m * 4 + 1] * ny;
                var ty = map.Turn[m * 4 + 2] * nx + map.Turn[m * 4 + 3] * ny;
                var turned = MathF.Sqrt(tx * tx + ty * ty);
                var k = turned > 1e-6f ? length / turned : 0;
                var o = (y * w + x) * 4;
                output[o] = (byte)Math.Clamp(MathF.Round(tx * k * 127 + 128), 0, 255);
                output[o + 1] = (byte)Math.Clamp(MathF.Round(ty * k * 127 + 128), 0, 255);
                output[o + 2] = 0;
                output[o + 3] = 255;
            }
        });
        return new RgbaImage(w, h, output);
    }

    /// <summary>Bilinear read at a UV, clamped at the sheet's edges.</summary>
    private static void Sample(RgbaImage image, float u, float v, Span<float> into)
    {
        var (w, h) = (image.Width, image.Height);
        var fx = Math.Clamp(u * w - 0.5f, 0, w - 1);
        var fy = Math.Clamp(v * h - 0.5f, 0, h - 1);
        var (x0, y0) = ((int)fx, (int)fy);
        var (x1, y1) = (Math.Min(x0 + 1, w - 1), Math.Min(y0 + 1, h - 1));
        var (ax, ay) = (fx - x0, fy - y0);
        var p = image.Pixels;
        for (var c = 0; c < 4; c++)
        {
            float Px(int x, int y) => p[(y * w + x) * 4 + c];
            into[c] = (Px(x0, y0) * (1 - ax) + Px(x1, y0) * ax) * (1 - ay) + (Px(x0, y1) * (1 - ax) + Px(x1, y1) * ax) * ay;
        }
    }
}
