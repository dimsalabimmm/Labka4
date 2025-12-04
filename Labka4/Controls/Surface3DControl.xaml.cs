using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Labka4.Models;

namespace Labka4.Controls
{
    public partial class Surface3DControl : UserControl
    {
        public static readonly DependencyProperty SurfaceDataProperty =
            DependencyProperty.Register(
                nameof(SurfaceData),
                typeof(SurfaceMeshData),
                typeof(Surface3DControl),
                new PropertyMetadata(null, OnSurfaceDataChanged));

        private readonly AxisAngleRotation3D _xRotation = new AxisAngleRotation3D(new Vector3D(1, 0, 0), -30);
        private readonly AxisAngleRotation3D _yRotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 35);
        private readonly Transform3DGroup _surfaceTransform = new Transform3DGroup();

        private bool _isRotating;
        private Point _lastMousePosition;
        private double _cameraDistance = 8;

        public Surface3DControl()
        {
            InitializeComponent();

            _surfaceTransform.Children.Add(new RotateTransform3D(_xRotation));
            _surfaceTransform.Children.Add(new RotateTransform3D(_yRotation));
            SurfaceVisual.Transform = _surfaceTransform;

            MainViewport.MouseRightButtonDown += OnMouseRightButtonDown;
            MainViewport.MouseRightButtonUp += OnMouseRightButtonUp;
            MainViewport.MouseMove += OnMouseMove;
            MainViewport.MouseWheel += OnMouseWheel;
        }

        public SurfaceMeshData SurfaceData
        {
            get => (SurfaceMeshData)GetValue(SurfaceDataProperty);
            set => SetValue(SurfaceDataProperty, value);
        }

        private static void OnSurfaceDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Surface3DControl control)
            {
                control.BuildSurface();
            }
        }

        private void BuildSurface()
        {
            var data = SurfaceData?.Points;
            if (data == null || data.Length == 0)
            {
                SurfaceVisual.Content = null;
                return;
            }

            int rows = data.GetLength(0);
            int columns = data.GetLength(1);

            if (rows < 2 || columns < 2)
            {
                SurfaceVisual.Content = null;
                return;
            }

            var bounds = CalculateBounds(data);
            var center = new Point3D(
                (bounds.minX + bounds.maxX) / 2,
                (bounds.minY + bounds.maxY) / 2,
                (bounds.minZ + bounds.maxZ) / 2);

            var positions = new Point3DCollection(rows * columns);
            var textureCoords = new PointCollection(rows * columns);

            double zRange = Math.Max(bounds.maxZ - bounds.minZ, double.Epsilon);

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    var point = data[r, c];
                    var centered = new Point3D(point.X - center.X, point.Y - center.Y, point.Z - center.Z);
                    positions.Add(centered);

                    double normalized = (point.Z - bounds.minZ) / zRange;
                    textureCoords.Add(new Point((double)c / (columns - 1), 1 - normalized));
                }
            }

            var triangleIndices = new Int32Collection((rows - 1) * (columns - 1) * 6);
            for (int r = 0; r < rows - 1; r++)
            {
                for (int c = 0; c < columns - 1; c++)
                {
                    int topLeft = (r * columns) + c;
                    int topRight = topLeft + 1;
                    int bottomLeft = ((r + 1) * columns) + c;
                    int bottomRight = bottomLeft + 1;

                    triangleIndices.Add(topLeft);
                    triangleIndices.Add(bottomLeft);
                    triangleIndices.Add(topRight);

                    triangleIndices.Add(topRight);
                    triangleIndices.Add(bottomLeft);
                    triangleIndices.Add(bottomRight);
                }
            }

            var mesh = new MeshGeometry3D
            {
                Positions = positions,
                TriangleIndices = triangleIndices,
                TextureCoordinates = textureCoords,
                Normals = CalculateNormals(positions, triangleIndices)
            };

            var gradientBrush = CreateGradientBrush();
            var materialGroup = new MaterialGroup();
            materialGroup.Children.Add(new DiffuseMaterial(gradientBrush));
            materialGroup.Children.Add(new SpecularMaterial(new SolidColorBrush(Colors.WhiteSmoke), 30));

            var geometryModel = new GeometryModel3D
            {
                Geometry = mesh,
                Material = materialGroup,
                BackMaterial = materialGroup
            };

            SurfaceVisual.Content = geometryModel;

            double span = Math.Max(bounds.maxX - bounds.minX, Math.Max(bounds.maxY - bounds.minY, bounds.maxZ - bounds.minZ));
            _cameraDistance = Math.Max(span * 1.8, 5);
            UpdateCamera();
        }

        private static (double minX, double maxX, double minY, double maxY, double minZ, double maxZ) CalculateBounds(Point3D[,] data)
        {
            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double minZ = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;
            double maxZ = double.MinValue;

            foreach (var point in data)
            {
                if (point.X < minX) minX = point.X;
                if (point.X > maxX) maxX = point.X;

                if (point.Y < minY) minY = point.Y;
                if (point.Y > maxY) maxY = point.Y;

                if (point.Z < minZ) minZ = point.Z;
                if (point.Z > maxZ) maxZ = point.Z;
            }

            return (minX, maxX, minY, maxY, minZ, maxZ);
        }

        private static Vector3DCollection CalculateNormals(Point3DCollection positions, Int32Collection triangleIndices)
        {
            var normals = new Vector3D[positions.Count];

            for (int i = 0; i < triangleIndices.Count; i += 3)
            {
                int i0 = triangleIndices[i];
                int i1 = triangleIndices[i + 1];
                int i2 = triangleIndices[i + 2];

                var p0 = positions[i0];
                var p1 = positions[i1];
                var p2 = positions[i2];

                var u = p1 - p0;
                var v = p2 - p0;
                var normal = Vector3D.CrossProduct(u, v);

                if (normal.LengthSquared > double.Epsilon)
                {
                    normal.Normalize();
                }

                normals[i0] += normal;
                normals[i1] += normal;
                normals[i2] += normal;
            }

            var collection = new Vector3DCollection(normals.Length);
            foreach (var normal in normals)
            {
                var normalized = normal;
                if (normalized.LengthSquared > double.Epsilon)
                {
                    normalized.Normalize();
                }
                collection.Add(normalized);
            }

            return collection;
        }

        private static Brush CreateGradientBrush()
        {
            return new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 1),
                EndPoint = new Point(0.5, 0),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop((Color)ColorConverter.ConvertFromString("#15224B"), 0),
                    new GradientStop(Colors.DeepSkyBlue, 0.2),
                    new GradientStop(Colors.LimeGreen, 0.45),
                    new GradientStop(Colors.Gold, 0.7),
                    new GradientStop(Colors.OrangeRed, 1)
                }
            };
        }

        private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isRotating = true;
            _lastMousePosition = e.GetPosition(this);
            MainViewport.CaptureMouse();
        }

        private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isRotating = false;
            MainViewport.ReleaseMouseCapture();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isRotating)
            {
                return;
            }

            var currentPosition = e.GetPosition(this);
            double deltaX = currentPosition.X - _lastMousePosition.X;
            double deltaY = currentPosition.Y - _lastMousePosition.Y;

            _yRotation.Angle += deltaX * 0.3;
            _xRotation.Angle += deltaY * 0.3;

            _lastMousePosition = currentPosition;
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            double zoomFactor = e.Delta > 0 ? 0.85 : 1.15;
            _cameraDistance = Math.Max(2, Math.Min(_cameraDistance * zoomFactor, 200));
            UpdateCamera();
        }

        private void UpdateCamera()
        {
            MainCamera.Position = new Point3D(0, 0, _cameraDistance);
            MainCamera.LookDirection = new Vector3D(0, 0, -_cameraDistance);
        }
    }
}

