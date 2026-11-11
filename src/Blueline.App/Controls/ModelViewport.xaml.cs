using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Blueline.Formats.Models;

namespace Blueline.App.Controls;

/// <summary>Geometry prepared off the UI thread; frozen meshes can cross threads.</summary>
public sealed record PreparedModel(Model3DGroup Scene, Point3D Center, double Radius, bool Truncated);

/// <summary>A model to draw, and the picture to wrap on it. Without one it is shaded grey. Texture must be frozen.</summary>
public sealed record ModelPart(McdFile Model, BitmapSource? Texture);

/// <summary>An orbit-camera view of models. A model holds no material data, so a texture has to be supplied by the caller.</summary>
public partial class ModelViewport : UserControl
{
    private const int TriangleBudget = 1_500_000;
    private Point _last;
    private double _yaw = 0.7, _pitch = 0.35, _distance = 10;
    private Point3D _target;
    private double _radius = 1;

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

        foreach (var part in parts)
        {
            var model = part.Model;
            Material material = part.Texture == null ? grey : Wrapped(part.Texture);
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

                var shown = mesh.TextureCoordinates.Count > 0 ? material : grey;
                var geometry = new GeometryModel3D(mesh, shown) { BackMaterial = shown };
                geometry.Freeze();
                scene.Children.Add(geometry);
            }
        }
        scene.Freeze();

        if (scene.Children.Count == 0)
            return new PreparedModel(scene, new Point3D(), 1, truncated);
        var center = new Point3D((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        return new PreparedModel(scene, center, Math.Max((max - min).Length / 2, 1e-3), truncated);
    }

    /// <summary>A brush that takes a mesh's UVs as they are, rather than stretching the picture over their range.</summary>
    private static Material Wrapped(BitmapSource texture)
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
        Holder.Content = prepared.Scene;
        if (keepView)
        {
            Message.Visibility = Visibility.Collapsed;
            Hint.Visibility = Visibility.Visible;
            return;
        }
        _target = prepared.Center;
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
        _last = e.GetPosition(Surface);
        Surface.CaptureMouse();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e) => Surface.ReleaseMouseCapture();

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
