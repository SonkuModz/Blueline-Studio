using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Blueline.Formats.Models;

namespace Blueline.App.Controls;

/// <summary>Geometry prepared off the UI thread; frozen meshes can cross threads.</summary>
public sealed record PreparedModel(Model3DGroup Scene, Point3D Center, double Radius, bool Truncated)
{
    /// <summary>The meshes of each part given to <see cref="ModelViewport.Prepare(IReadOnlyList{ModelPart}, ISet{string})"/>, in order.</summary>
    public IReadOnlyList<PreparedPart> Parts { get; init; } = [];
}

/// <summary>One part's frozen meshes, their bounds, and the material they were prepared with.</summary>
public sealed record PreparedPart(IReadOnlyList<MeshGeometry3D> Meshes, Rect3D Bounds, Material Material);

/// <summary>A model to draw, and the picture to wrap on it. Without one it is shaded grey. Texture must be frozen.</summary>
public sealed record ModelPart(McdFile Model, BitmapSource? Texture);

/// <summary>An orbit-camera view of models. A model holds no material data, so a texture has to be supplied by the caller.</summary>
public partial class ModelViewport : UserControl
{
    private const int TriangleBudget = 1_500_000;
    private Point _last;
    private double _yaw = 0.7, _pitch = 0.35, _distance = 10;
    private Point3D _target;
    private Point3D _center;
    private double _radius = 1;
    private Point _pressedAt;
    private readonly List<(List<GeometryModel3D> Models, Rect3D Bounds)> _parts = [];
    private readonly Dictionary<GeometryModel3D, int> _partOf = [];

    public ModelViewport()
    {
        InitializeComponent();
        Message.Text = "Select a model to preview it.";
    }

    public void ShowMessage(string text)
    {
        Holder.Content = null;
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
        Hint.Visibility = Visibility.Collapsed;
    }

    /// <summary>Builds meshes for the shapes, up to the triangle budget. Thread-safe.</summary>
    public static PreparedModel Prepare(McdFile model, ISet<string>? hidden = null) => Prepare([new ModelPart(model, null)], hidden);

    /// <summary>Builds meshes for several models in one scene, up to the triangle budget. Thread-safe.</summary>
    public static PreparedModel Prepare(IReadOnlyList<ModelPart> parts, ISet<string>? hidden = null)
    {
        var grey = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xB8, 0xC2, 0xD2)));
        grey.Freeze();

        var scene = new Model3DGroup();
        var (min, max) = (new Point3D(double.MaxValue, double.MaxValue, double.MaxValue), new Point3D(double.MinValue, double.MinValue, double.MinValue));
        var used = 0;
        var truncated = false;
        var prepared = new List<PreparedPart>(parts.Count);

        foreach (var part in parts)
        {
            var model = part.Model;
            Material material = part.Texture == null ? grey : Wrapped(part.Texture);
            var meshes = new List<MeshGeometry3D>();
            var bounds = Rect3D.Empty;
            foreach (var shape in model.Shapes)
            {
                if (hidden?.Contains(shape.Name) == true)
                    continue;
                if (used + shape.Triangles > TriangleBudget)
                {
                    truncated = true;
                    continue;
                }
                used += shape.Triangles;

                var positions = model.ReadPositions(model.Streams[shape.Stream]);
                var normals = model.ReadNormals(shape);
                var indices = model.ReadIndices(model.IndexBuffers[shape.IndexBuffer]);

                var mesh = new MeshGeometry3D { Positions = new Point3DCollection(positions.Length), TriangleIndices = new Int32Collection(indices) };
                foreach (var p in positions)
                {
                    mesh.Positions.Add(new Point3D(p.X, p.Y, p.Z));
                    min = new Point3D(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                    max = new Point3D(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
                }
                // Stored normals are used only when they look like unit vectors; some formats pack other data in "n".
                if (normals != null && normals.Take(32).All(n => Math.Abs(n.Length() - 1) < 0.2))
                {
                    mesh.Normals = new Vector3DCollection(normals.Length);
                    foreach (var n in normals)
                        mesh.Normals.Add(new Vector3D(n.X, n.Y, n.Z));
                }
                if (part.Texture != null && model.ReadUvs(shape) is { } uvs)
                {
                    mesh.TextureCoordinates = new PointCollection(uvs.Length);
                    foreach (var t in uvs)
                        mesh.TextureCoordinates.Add(new Point(t.X, t.Y));
                }
                mesh.Freeze();
                meshes.Add(mesh);
                bounds.Union(mesh.Bounds);

                var shown = mesh.TextureCoordinates.Count > 0 ? material : grey;
                var geometry = new GeometryModel3D(mesh, shown) { BackMaterial = shown };
                geometry.Freeze();
                scene.Children.Add(geometry);
            }
            prepared.Add(new PreparedPart(meshes, bounds, material));
        }
        scene.Freeze();

        if (scene.Children.Count == 0)
            return new PreparedModel(scene, new Point3D(), 1, truncated) { Parts = prepared };
        var center = new Point3D((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        return new PreparedModel(scene, center, Math.Max((max - min).Length / 2, 1e-3), truncated) { Parts = prepared };
    }

    /// <summary>A brush that takes a mesh's UVs as they are, rather than stretching the picture over their range.</summary>
    public static Material Wrapped(BitmapSource texture)
    {
        var brush = new ImageBrush(texture)
        {
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1),
            TileMode = TileMode.Tile,
        };
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }

    /// <summary>
    /// Shows a model whose parts can be given new textures afterwards (<see cref="SetTexture"/>) and picked with the mouse
    /// (<see cref="Picked"/>). The meshes stay shared and frozen; only the materials change.
    /// </summary>
    public void ShowEditable(PreparedModel prepared, bool keepView = false, double closeness = 3.2)
    {
        var scene = new Model3DGroup();
        _parts.Clear();
        _partOf.Clear();
        for (var i = 0; i < prepared.Parts.Count; i++)
        {
            var models = new List<GeometryModel3D>();
            foreach (var mesh in prepared.Parts[i].Meshes)
            {
                var geometry = new GeometryModel3D(mesh, prepared.Parts[i].Material) { BackMaterial = prepared.Parts[i].Material };
                scene.Children.Add(geometry);
                models.Add(geometry);
                _partOf[geometry] = i;
            }
            _parts.Add((models, prepared.Parts[i].Bounds));
        }
        ShowScene(prepared with { Scene = scene }, keepView, closeness);
    }

    /// <summary>Wraps a new picture on one part of a model shown with <see cref="ShowEditable"/>. Null greys it.</summary>
    public void SetTexture(int part, BitmapSource? texture)
    {
        if (part < 0 || part >= _parts.Count)
            return;
        Material material = texture == null ? new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xB8, 0xC2, 0xD2))) : Wrapped(texture);
        foreach (var geometry in _parts[part].Models)
            if (geometry.Geometry is MeshGeometry3D { TextureCoordinates.Count: > 0 } || texture == null)
                geometry.Material = geometry.BackMaterial = material;
    }

    /// <summary>A part's bounds, for pointing the camera at it.</summary>
    public Rect3D PartBounds(int part) => part >= 0 && part < _parts.Count ? _parts[part].Bounds : Rect3D.Empty;

    /// <summary>A click (not a drag) on a part of an editable model: the part and the texture coordinate under the mouse.</summary>
    public event Action<int, Point>? Picked;

    /// <summary>Points the camera at a box, keeping its angle unless one is given.</summary>
    public void FocusOn(Rect3D bounds, double closeness = 2.4, double? yaw = null, double? pitch = null)
    {
        if (bounds.IsEmpty)
            return;
        _target = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        var radius = Math.Max(new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ).Length / 2, _radius * 0.05);
        _distance = radius * closeness;
        _yaw = yaw ?? _yaw;
        _pitch = pitch ?? _pitch;
        UpdateCamera();
    }

    /// <summary>Back to the whole model, from the front or another angle.</summary>
    public void ResetView(double yaw, double pitch, double closeness)
    {
        _target = _center;
        _distance = _radius * closeness;
        (_yaw, _pitch) = (yaw, pitch);
        UpdateCamera();
    }

    /// <summary>Picks at a point of the view, as a click there would. Returns false when nothing editable is under it.</summary>
    public bool PickAt(Point point)
    {
        (int Part, Point Uv)? hit = null;
        VisualTreeHelper.HitTest(View, null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult mesh && mesh.ModelHit is GeometryModel3D model && _partOf.TryGetValue(model, out var part)
                && mesh.MeshHit.TextureCoordinates is { Count: > 0 } uvs)
            {
                var uv = (Vector)uvs[mesh.VertexIndex1] * mesh.VertexWeight1 + (Vector)uvs[mesh.VertexIndex2] * mesh.VertexWeight2
                         + (Vector)uvs[mesh.VertexIndex3] * mesh.VertexWeight3;
                hit = (part, (Point)uv);
            }
            return HitTestResultBehavior.Stop;
        }, new PointHitTestParameters(point));
        if (hit is not { } found)
            return false;
        Picked?.Invoke(found.Part, found.Uv);
        return true;
    }

    /// <summary>Turns the camera, in radians around the model and above it.</summary>
    public void SetView(double yaw, double pitch)
    {
        _yaw = yaw;
        _pitch = pitch;
        UpdateCamera();
    }

    /// <param name="keepView">Swaps the model under the camera, for changing between versions of one thing.</param>
    /// <param name="closeness">How far away the camera starts, in model radii. Less is closer.</param>
    public void Show(PreparedModel prepared, bool keepView = false, double closeness = 3.2)
    {
        _parts.Clear();
        _partOf.Clear();
        ShowScene(prepared, keepView, closeness);
    }

    private void ShowScene(PreparedModel prepared, bool keepView, double closeness)
    {
        Holder.Content = prepared.Scene;
        if (keepView)
        {
            Message.Visibility = Visibility.Collapsed;
            Hint.Visibility = Visibility.Visible;
            return;
        }
        _target = _center = prepared.Center;
        _radius = prepared.Radius;
        _distance = _radius * closeness;
        Camera.NearPlaneDistance = _radius / 100;
        Camera.FarPlaneDistance = _radius * 100;
        Message.Visibility = Visibility.Collapsed;
        Hint.Visibility = Visibility.Visible;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var direction = new Vector3D(Math.Cos(_pitch) * Math.Sin(_yaw), Math.Sin(_pitch), Math.Cos(_pitch) * Math.Cos(_yaw));
        Camera.Position = _target + direction * _distance;
        Camera.LookDirection = -direction * _distance;
        Camera.UpDirection = new Vector3D(0, 1, 0);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _last = _pressedAt = e.GetPosition(Surface);
        Surface.CaptureMouse();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        Surface.ReleaseMouseCapture();
        var at = e.GetPosition(Surface);
        if (e.ChangedButton == MouseButton.Left && _parts.Count > 0 && (at - _pressedAt).Length < 4)
            PickAt(e.GetPosition(View));
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!Surface.IsMouseCaptured)
            return;
        var now = e.GetPosition(Surface);
        var delta = now - _last;
        _last = now;

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _yaw -= delta.X * 0.01;
            _pitch = Math.Clamp(_pitch + delta.Y * 0.01, -1.5, 1.5);
        }
        else if (e.RightButton == MouseButtonState.Pressed)
        {
            var right = Vector3D.CrossProduct(Camera.LookDirection, Camera.UpDirection);
            right.Normalize();
            var up = Vector3D.CrossProduct(right, Camera.LookDirection);
            up.Normalize();
            var scale = _distance / Math.Max(Surface.ActualHeight, 1);
            _target += (-right * delta.X + up * delta.Y) * scale;
        }
        UpdateCamera();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.87 : 1.15), _radius * 0.05, _radius * 50);
        UpdateCamera();
    }
}
