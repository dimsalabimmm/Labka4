using System;
using System.Collections.Generic;
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

        // ===== Surface rotation (RMB) =====
        private readonly AxisAngleRotation3D _xRotation = new AxisAngleRotation3D(new Vector3D(1, 0, 0), -30);
        private readonly AxisAngleRotation3D _yRotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 35);
        private readonly Transform3DGroup _surfaceTransform = new Transform3DGroup();

        // ===== Camera pan/zoom (LMB drag / wheel) =====
        private bool _isRotating;
        private bool _isPanning;
        private Point _mouseDownPos;
        private Point _lastMousePos;
        private bool _movedEnoughForDrag;

        private double _cameraDistance = 8;
        private double _cameraOffsetX;
        private double _cameraOffsetY;

        // ===== Surface cached data (local, centered) =====
        private Point3D[,] _surfaceLocalPoints; // null until BuildSurface
        private int _rows;
        private int _cols;

        private double _xMinC, _xMaxC, _yMinC, _yMaxC;
        private double _spanForScale;

        // ===== Scene models =====
        private GeometryModel3D _surfaceModel; // null until built
        private Model3DGroup _rootGroup;       // null until built
        private Model3DGroup _dynamicGroup;    // null until built

        // Tracks (tread stamps)
        private GeometryModel3D _tracksModel;
        private MeshGeometry3D _tracksMesh;
        private Point3DCollection _tracksPos;
        private Int32Collection _tracksIdx;
        private Vector3DCollection _tracksNormals;

        // Cars
        private readonly List<CarInstance> _cars = new List<CarInstance>();
        private Model3DGroup _carPrototype; // model template (unit car)
        private Material _carBodyMaterial;
        private Material _carCabinMaterial;
        private Material _carWheelMaterial;

        // Animation timing
        private TimeSpan _lastRenderTime;

        private static readonly Random _rng = new Random();

        // Tuning
        private const double ClickDragThresholdPx = 5.0;

        public Surface3DControl()
        {
            InitializeComponent();

            _surfaceTransform.Children.Add(new RotateTransform3D(_xRotation));
            _surfaceTransform.Children.Add(new RotateTransform3D(_yRotation));
            SurfaceVisual.Transform = _surfaceTransform;

            MainViewport.MouseLeftButtonDown += OnMouseLeftButtonDown;
            MainViewport.MouseLeftButtonUp += OnMouseLeftButtonUp;
            MainViewport.MouseRightButtonDown += OnMouseRightButtonDown;
            MainViewport.MouseRightButtonUp += OnMouseRightButtonUp;
            MainViewport.MouseMove += OnMouseMove;
            MainViewport.MouseWheel += OnMouseWheel;

            CompositionTarget.Rendering += OnRendering;
            Unloaded += delegate { CompositionTarget.Rendering -= OnRendering; };
        }

        public SurfaceMeshData SurfaceData
        {
            get { return (SurfaceMeshData)GetValue(SurfaceDataProperty); }
            set { SetValue(SurfaceDataProperty, value); }
        }

        private static void OnSurfaceDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as Surface3DControl;
            if (control != null)
                control.BuildSurface();
        }

        // ============================================================
        // Build surface + init dynamic layers
        // ============================================================
        private void BuildSurface()
        {
            var data = SurfaceData != null ? SurfaceData.Points : null;
            if (data == null || data.GetLength(0) < 2 || data.GetLength(1) < 2)
            {
                SurfaceVisual.Content = null;
                _surfaceModel = null;
                _rootGroup = null;
                _dynamicGroup = null;
                _cars.Clear();
                return;
            }

            _rows = data.GetLength(0);
            _cols = data.GetLength(1);

            var bounds = CalculateBounds(data);

            var center = new Point3D(
                (bounds.minX + bounds.maxX) / 2.0,
                (bounds.minY + bounds.maxY) / 2.0,
                (bounds.minZ + bounds.maxZ) / 2.0);

            _surfaceLocalPoints = new Point3D[_rows, _cols];
            for (int r = 0; r < _rows; r++)
                for (int c = 0; c < _cols; c++)
                {
                    var p = data[r, c];
                    _surfaceLocalPoints[r, c] = new Point3D(p.X - center.X, p.Y - center.Y, p.Z - center.Z);
                }

            _xMinC = bounds.minX - center.X;
            _xMaxC = bounds.maxX - center.X;
            _yMinC = bounds.minY - center.Y;
            _yMaxC = bounds.maxY - center.Y;

            var positions = new Point3DCollection(_rows * _cols);
            for (int r = 0; r < _rows; r++)
                for (int c = 0; c < _cols; c++)
                    positions.Add(_surfaceLocalPoints[r, c]);

            var triangleIndices = new Int32Collection((_rows - 1) * (_cols - 1) * 6);
            for (int r = 0; r < _rows - 1; r++)
                for (int c = 0; c < _cols - 1; c++)
                {
                    int topLeft = r * _cols + c;
                    int topRight = topLeft + 1;
                    int bottomLeft = (r + 1) * _cols + c;
                    int bottomRight = bottomLeft + 1;

                    triangleIndices.Add(topLeft);
                    triangleIndices.Add(bottomLeft);
                    triangleIndices.Add(topRight);

                    triangleIndices.Add(topRight);
                    triangleIndices.Add(bottomLeft);
                    triangleIndices.Add(bottomRight);
                }

            // Gradient texture coords by Z
            double zMin = bounds.minZ;
            double zMax = bounds.maxZ;
            double zRange = Math.Max(zMax - zMin, double.Epsilon);

            var textureCoords = new PointCollection(_rows * _cols);
            for (int r = 0; r < _rows; r++)
                for (int c = 0; c < _cols; c++)
                {
                    double z = data[r, c].Z;
                    double normalized = (z - zMin) / zRange;
                    textureCoords.Add(new Point((double)c / (_cols - 1), 1 - normalized));
                }

            var mesh = new MeshGeometry3D
            {
                Positions = positions,
                TriangleIndices = triangleIndices,
                TextureCoordinates = textureCoords,
                Normals = CalculateNormals(positions, triangleIndices)
            };

            var gradientBrush = CreateGradientBrush();
            var surfaceMaterial = new MaterialGroup();
            surfaceMaterial.Children.Add(new DiffuseMaterial(gradientBrush));
            surfaceMaterial.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 40));

            _surfaceModel = new GeometryModel3D(mesh, surfaceMaterial)
            {
                BackMaterial = surfaceMaterial
            };

            _dynamicGroup = new Model3DGroup();
            InitTracksLayer();
            InitCarResources();

            _rootGroup = new Model3DGroup();
            _rootGroup.Children.Add(_surfaceModel);
            _rootGroup.Children.Add(_dynamicGroup);

            SurfaceVisual.Content = _rootGroup;

            double span = Math.Max(bounds.maxX - bounds.minX, Math.Max(bounds.maxY - bounds.minY, bounds.maxZ - bounds.minZ));
            _spanForScale = Math.Max(span, 1.0);

            _cameraDistance = Math.Max(span * 1.8, 5);
            _cameraOffsetX = 0;
            _cameraOffsetY = 0;
            UpdateCamera();

            _cars.Clear();
            ClearTracks();
        }

        // ============================================================
        // Tracks layer (tread stamps)
        // ============================================================
        private void InitTracksLayer()
        {
            _tracksPos = new Point3DCollection();
            _tracksIdx = new Int32Collection();
            _tracksNormals = new Vector3DCollection();

            _tracksMesh = new MeshGeometry3D
            {
                Positions = _tracksPos,
                TriangleIndices = _tracksIdx,
                Normals = _tracksNormals
            };

            var trackMat = new MaterialGroup();
            trackMat.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(20, 20, 20))));
            trackMat.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(10, 10, 10))));

            _tracksModel = new GeometryModel3D(_tracksMesh, trackMat)
            {
                BackMaterial = trackMat
            };

            _dynamicGroup.Children.Add(_tracksModel);
        }

        private void ClearTracks()
        {
            if (_tracksPos != null) _tracksPos.Clear();
            if (_tracksIdx != null) _tracksIdx.Clear();
            if (_tracksNormals != null) _tracksNormals.Clear();
        }

        // ============================================================
        // Car prototype (simple but "car-like")
        // ============================================================
        private void InitCarResources()
        {
            _carBodyMaterial = new MaterialGroup
            {
                Children =
                {
                    new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(230, 230, 230))),
                    new SpecularMaterial(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 70)
                }
            };

            _carCabinMaterial = new MaterialGroup
            {
                Children =
                {
                    new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(140, 180, 220))),
                    new SpecularMaterial(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), 90)
                }
            };

            _carWheelMaterial = new MaterialGroup
            {
                Children =
                {
                    new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(25, 25, 25))),
                    new SpecularMaterial(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 30)
                }
            };

            _carPrototype = CreateUnitCarModel();
        }

        private Model3DGroup CreateUnitCarModel()
        {
            // Car local axes: X forward, Y right, Z up.
            var group = new Model3DGroup();

            var body = CreateBoxModel(1.6, 0.8, 0.35, _carBodyMaterial);
            body.Transform = new TranslateTransform3D(0.0, 0.0, 0.25);

            var cabin = CreateBoxModel(0.8, 0.7, 0.35, _carCabinMaterial);
            cabin.Transform = new TranslateTransform3D(-0.2, 0.0, 0.52);

            double wL = 0.35, wW = 0.16, wH = 0.22;

            var wheelFL = CreateBoxModel(wL, wW, wH, _carWheelMaterial);
            wheelFL.Transform = new TranslateTransform3D(0.55, 0.38, 0.11);

            var wheelFR = CreateBoxModel(wL, wW, wH, _carWheelMaterial);
            wheelFR.Transform = new TranslateTransform3D(0.55, -0.38, 0.11);

            var wheelRL = CreateBoxModel(wL, wW, wH, _carWheelMaterial);
            wheelRL.Transform = new TranslateTransform3D(-0.55, 0.38, 0.11);

            var wheelRR = CreateBoxModel(wL, wW, wH, _carWheelMaterial);
            wheelRR.Transform = new TranslateTransform3D(-0.55, -0.38, 0.11);

            group.Children.Add(body);
            group.Children.Add(cabin);
            group.Children.Add(wheelFL);
            group.Children.Add(wheelFR);
            group.Children.Add(wheelRL);
            group.Children.Add(wheelRR);

            return group;
        }

        private GeometryModel3D CreateBoxModel(double sx, double sy, double sz, Material material)
        {
            double hx = sx / 2.0;
            double hy = sy / 2.0;
            double hz = sz / 2.0;

            var p = new[]
            {
                new Point3D(-hx, -hy, -hz),
                new Point3D( hx, -hy, -hz),
                new Point3D( hx,  hy, -hz),
                new Point3D(-hx,  hy, -hz),

                new Point3D(-hx, -hy,  hz),
                new Point3D( hx, -hy,  hz),
                new Point3D( hx,  hy,  hz),
                new Point3D(-hx,  hy,  hz)
            };

            int[] idx =
            {
                0,2,1, 0,3,2,
                4,5,6, 4,6,7,
                0,7,3, 0,4,7,
                1,2,6, 1,6,5,
                0,1,5, 0,5,4,
                3,7,6, 3,6,2
            };

            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection(p),
                TriangleIndices = new Int32Collection(idx)
            };

            var normals = new Vector3DCollection();
            for (int i = 0; i < p.Length; i++)
            {
                var v = new Vector3D(p[i].X, p[i].Y, p[i].Z);
                if (v.LengthSquared > 1e-12) v.Normalize();
                normals.Add(v);
            }
            mesh.Normals = normals;

            return new GeometryModel3D(mesh, material) { BackMaterial = material };
        }

        // ============================================================
        // Mouse: LMB click spawn OR drag pan; RMB rotate surface
        // ============================================================
        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isPanning = true;
            _movedEnoughForDrag = false;
            _mouseDownPos = e.GetPosition(MainViewport);
            _lastMousePos = _mouseDownPos;
            MainViewport.CaptureMouse();
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPanning)
                return;

            var upPos = e.GetPosition(MainViewport);
            _isPanning = false;

            if (!_isRotating)
                MainViewport.ReleaseMouseCapture();

            if (!_movedEnoughForDrag && Distance(upPos, _mouseDownPos) <= ClickDragThresholdPx)
            {
                TrySpawnCarAt(upPos);
            }
        }

        private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isRotating = true;
            _lastMousePos = e.GetPosition(this);
            MainViewport.CaptureMouse();
        }

        private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isRotating = false;
            if (!_isPanning)
                MainViewport.ReleaseMouseCapture();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                var cur = e.GetPosition(MainViewport);
                double dx = cur.X - _lastMousePos.X;
                double dy = cur.Y - _lastMousePos.Y;

                if (!_movedEnoughForDrag && Distance(cur, _mouseDownPos) > ClickDragThresholdPx)
                    _movedEnoughForDrag = true;

                if (_movedEnoughForDrag)
                    PanCamera(dx, dy);

                _lastMousePos = cur;
                return;
            }

            if (_isRotating)
            {
                var cur = e.GetPosition(this);
                double dx = cur.X - _lastMousePos.X;
                double dy = cur.Y - _lastMousePos.Y;

                _yRotation.Angle += dx * 0.3;
                _xRotation.Angle += dy * 0.3;

                _lastMousePos = cur;
            }
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            double zoomFactor = e.Delta > 0 ? 0.85 : 1.15;
            _cameraDistance = Math.Max(2, Math.Min(_cameraDistance * zoomFactor, 200));
            UpdateCamera();
        }

        private void PanCamera(double deltaXpx, double deltaYpx)
        {
            double height = Math.Max(1.0, MainViewport.ActualHeight);
            double width = Math.Max(1.0, MainViewport.ActualWidth);

            double fovRad = MainCamera.FieldOfView * Math.PI / 180.0;

            double worldSpanY = 2.0 * _cameraDistance * Math.Tan(fovRad / 2.0);
            double worldPerPixelY = worldSpanY / height;
            double worldPerPixelX = worldPerPixelY * (width / height);

            _cameraOffsetX -= deltaXpx * worldPerPixelX;
            _cameraOffsetY += deltaYpx * worldPerPixelY;

            UpdateCamera();
        }

        private void UpdateCamera()
        {
            MainCamera.Position = new Point3D(_cameraOffsetX, _cameraOffsetY, _cameraDistance);
            MainCamera.LookDirection = new Vector3D(0, 0, -_cameraDistance);
        }

        // ============================================================
        // Spawn car: click -> hit surface -> LOCALize hit -> correct side -> random direction
        // ============================================================
        private void TrySpawnCarAt(Point viewportPoint)
        {
            if (_surfaceModel == null || _dynamicGroup == null || _surfaceLocalPoints == null)
                return;

            RayMeshGeometry3DHitTestResult hit = null;

            VisualTreeHelper.HitTest(
                MainViewport,
                null,
                delegate (HitTestResult result)
                {
                    var ray = result as RayMeshGeometry3DHitTestResult;
                    if (ray != null && ray.ModelHit == _surfaceModel)
                    {
                        hit = ray;
                        return HitTestResultBehavior.Stop;
                    }
                    return HitTestResultBehavior.Continue;
                },
                new PointHitTestParameters(viewportPoint));

            if (hit == null)
                return;

            // HitTest returns PointHit in the local coordinate system of the hit Model3D/Visual3D.
            // This is the same "surface space" that _surfaceLocalPoints uses (before _surfaceTransform is applied).
            Point3D pLocal = hit.PointHit;

            // Ray direction in LOCAL space (camera -> hit).
            Point3D cameraLocal = WorldPointToLocal(MainCamera.Position);
            Vector3D rayDirLocal = pLocal - cameraLocal;
            if (rayDirLocal.LengthSquared > 1e-12)
                rayDirLocal.Normalize();


            // Local -> fractional grid coords
            double xSpan = Math.Max(_xMaxC - _xMinC, double.Epsilon);
            double ySpan = Math.Max(_yMaxC - _yMinC, double.Epsilon);

            double colF = (pLocal.X - _xMinC) / xSpan * (_cols - 1);
            double rowF = (pLocal.Y - _yMinC) / ySpan * (_rows - 1);

            // Determine side using grid normal near click
            int rr = Clamp((int)Math.Round(rowF), 0, _rows - 1);
            int cc = Clamp((int)Math.Round(colF), 0, _cols - 1);

            Vector3D nGrid = GetSurfaceNormal(rr, cc);
            if (nGrid.LengthSquared > 1e-12)
                nGrid.Normalize();

            // Choose normal sign so that nGrid * normalSign faces the camera
            int normalSign = (Vector3D.DotProduct(nGrid, rayDirLocal) < 0) ? 1 : -1;

            // IMPORTANT: no mirroring here. The hit point already corresponds to what you clicked,
            // even if the surface is flipped.
            TravelDir dir = PickRandomDirection(colF, rowF);
            SpawnCarFromFractional(colF, rowF, dir, normalSign);
        }

        private Vector3D GetTriangleNormalLocal(RayMeshGeometry3DHitTestResult hit)
        {
            var mesh = _surfaceModel.Geometry as MeshGeometry3D;
            if (mesh == null || mesh.Positions == null)
                return new Vector3D(0, 0, 1);

            int i0 = hit.VertexIndex1;
            int i1 = hit.VertexIndex2;
            int i2 = hit.VertexIndex3;

            if (i0 < 0 || i1 < 0 || i2 < 0) return new Vector3D(0, 0, 1);
            if (i0 >= mesh.Positions.Count || i1 >= mesh.Positions.Count || i2 >= mesh.Positions.Count)
                return new Vector3D(0, 0, 1);

            Point3D p0 = mesh.Positions[i0];
            Point3D p1 = mesh.Positions[i1];
            Point3D p2 = mesh.Positions[i2];

            Vector3D u = p1 - p0;
            Vector3D v = p2 - p0;
            Vector3D n = Vector3D.CrossProduct(u, v);
            return n;
        }

        private TravelDir PickRandomDirection(double colF, double rowF)
        {
            TravelDir last = TravelDir.Right;
            for (int i = 0; i < 8; i++)
            {
                int k = _rng.Next(0, 4);
                TravelDir d = (TravelDir)k;
                last = d;

                if (d == TravelDir.Right && colF < _cols - 1.01) return d;
                if (d == TravelDir.Left && colF > 0.01) return d;
                if (d == TravelDir.Down && rowF < _rows - 1.01) return d;
                if (d == TravelDir.Up && rowF > 0.01) return d;
            }
            return last;
        }

        private void SpawnCarFromFractional(double colF, double rowF, TravelDir dir, int normalSign)
        {
            if (_dynamicGroup == null || _carPrototype == null)
                return;

            double carScale = Math.Max(0.06 * _spanForScale, 0.25);

            // Lift: сильнее, чтобы визуально не "въезжала" в поверхность
            double lift = Math.Max(0.22 * carScale, 0.08);
            lift = Math.Max(lift, 0.006 * _spanForScale);

            double wheelOffset = Math.Max(0.18 * carScale, 0.08);
            double tireWidth = Math.Max(0.12 * carScale, 0.05);
            double treadLength = Math.Max(0.16 * carScale, 0.06);
            double treadSpacing = Math.Max(0.32 * carScale, 0.12);

            double span = Math.Max(Math.Abs(_xMaxC - _xMinC), Math.Abs(_yMaxC - _yMinC));
            double speed = Math.Max(span * 0.45, 0.8);

            var carModel = CloneModelGroup(_carPrototype);

            var scale = new ScaleTransform3D(carScale, carScale, carScale);

            // Flip around LOCAL X (forward axis) to make wheels face the surface when on underside
            var flipRot = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
            var flipT = new RotateTransform3D(flipRot);

            // Yaw around LOCAL Z to face forward direction in XY
            var yawRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
            var yawT = new RotateTransform3D(yawRot);

            var translate = new TranslateTransform3D(0, 0, 0);

            var tg = new Transform3DGroup();
            tg.Children.Add(scale);
            tg.Children.Add(flipT);   // <-- flip BEFORE yaw (so axis X remains "forward")
            tg.Children.Add(yawT);
            tg.Children.Add(translate);

            carModel.Transform = tg;

            CarInstance car = new CarInstance();
            car.Model = carModel;
            car.Translate = translate;
            car.Rotation = yawRot;
            car.FlipRotation = flipRot;

            car.Speed = speed;
            car.State = CarState.Driving;

            car.WheelOffset = wheelOffset;
            car.TireWidth = tireWidth;
            car.TreadLength = treadLength;
            car.TreadSpacing = treadSpacing;

            car.Lift = lift;
            car.NormalSign = normalSign;

            car.HasLastPos = false;
            car.DistanceSinceLastTread = 0;
            car.FallVelocityWorld = 0;

            if (dir == TravelDir.Right || dir == TravelDir.Left)
            {
                int row = Clamp((int)Math.Round(rowF), 0, _rows - 1);

                int col0 = Clamp((int)Math.Floor(colF), 0, _cols - 2);
                double t = colF - Math.Floor(colF);
                t = Clamp01(t);

                car.Axis = TravelAxis.Horizontal;
                car.FixedIndex = row;
                car.SegIndex = col0;
                car.SegT = t;
                car.DirSign = (dir == TravelDir.Right) ? 1 : -1;
            }
            else
            {
                int col = Clamp((int)Math.Round(colF), 0, _cols - 1);

                int row0 = Clamp((int)Math.Floor(rowF), 0, _rows - 2);
                double t = rowF - Math.Floor(rowF);
                t = Clamp01(t);

                car.Axis = TravelAxis.Vertical;
                car.FixedIndex = col;
                car.SegIndex = row0;
                car.SegT = t;
                car.DirSign = (dir == TravelDir.Down) ? 1 : -1;
            }

            UpdateCarPoseAndTreads(car, 0);

            _dynamicGroup.Children.Add(carModel);
            _cars.Add(car);
        }

        private static Model3DGroup CloneModelGroup(Model3DGroup src)
        {
            var g = new Model3DGroup();
            for (int i = 0; i < src.Children.Count; i++)
                g.Children.Add(src.Children[i]);
            return g;
        }

        // ============================================================
        // Animation loop
        // ============================================================
        private void OnRendering(object sender, EventArgs e)
        {
            if (_cars.Count == 0)
                return;

            var re = e as RenderingEventArgs;
            if (re == null)
                return;

            if (_lastRenderTime == default(TimeSpan))
            {
                _lastRenderTime = re.RenderingTime;
                return;
            }

            double dt = (re.RenderingTime - _lastRenderTime).TotalSeconds;
            _lastRenderTime = re.RenderingTime;

            if (dt <= 0 || dt > 0.2)
                return;

            UpdateCars(dt);
        }

        private void UpdateCars(double dt)
        {
            if (_surfaceLocalPoints == null)
                return;

            for (int i = _cars.Count - 1; i >= 0; i--)
            {
                var car = _cars[i];

                if (car.State == CarState.Driving)
                {
                    bool finished = MoveCarOnSurface(car, dt);
                    if (finished)
                        car.State = CarState.Falling;
                }
                else
                {
                    bool remove = UpdateFalling(car, dt);
                    if (remove)
                    {
                        if (_dynamicGroup != null)
                            _dynamicGroup.Children.Remove(car.Model);
                        _cars.RemoveAt(i);
                    }
                }
            }
        }

        private bool MoveCarOnSurface(CarInstance car, double dt)
        {
            double remaining = car.Speed * dt;

            while (remaining > 0)
            {
                if (car.Axis == TravelAxis.Horizontal)
                {
                    int row = car.FixedIndex;

                    if (car.DirSign > 0)
                    {
                        if (car.SegIndex >= _cols - 1) return true;
                        if (car.SegIndex > _cols - 2) return true;
                    }
                    else
                    {
                        if (car.SegIndex < 0) return true;
                        if (car.SegIndex == 0 && car.SegT <= 0) return true;
                    }

                    if (car.SegIndex < 0) return true;
                    if (car.SegIndex > _cols - 2) return true;

                    Point3D a = _surfaceLocalPoints[row, car.SegIndex];
                    Point3D b = _surfaceLocalPoints[row, car.SegIndex + 1];
                    Vector3D ab = b - a;
                    double len = ab.Length;
                    if (len < 1e-9)
                    {
                        car.SegIndex += car.DirSign;
                        car.SegT = (car.DirSign > 0) ? 0 : 1;
                        continue;
                    }

                    double distToEdge = (car.DirSign > 0) ? (1.0 - car.SegT) * len : car.SegT * len;

                    if (remaining < distToEdge)
                    {
                        double deltaT = remaining / len;
                        car.SegT += (car.DirSign > 0) ? deltaT : -deltaT;
                        remaining = 0;
                    }
                    else
                    {
                        remaining -= distToEdge;
                        if (car.DirSign > 0)
                        {
                            car.SegIndex++;
                            car.SegT = 0;
                            if (car.SegIndex >= _cols - 1) return true;
                        }
                        else
                        {
                            car.SegIndex--;
                            car.SegT = 1;
                            if (car.SegIndex < 0) return true;
                        }
                    }
                }
                else
                {
                    int col = car.FixedIndex;

                    if (car.DirSign > 0)
                    {
                        if (car.SegIndex >= _rows - 1) return true;
                        if (car.SegIndex > _rows - 2) return true;
                    }
                    else
                    {
                        if (car.SegIndex < 0) return true;
                        if (car.SegIndex == 0 && car.SegT <= 0) return true;
                    }

                    if (car.SegIndex < 0) return true;
                    if (car.SegIndex > _rows - 2) return true;

                    Point3D a = _surfaceLocalPoints[car.SegIndex, col];
                    Point3D b = _surfaceLocalPoints[car.SegIndex + 1, col];
                    Vector3D ab = b - a;
                    double len = ab.Length;
                    if (len < 1e-9)
                    {
                        car.SegIndex += car.DirSign;
                        car.SegT = (car.DirSign > 0) ? 0 : 1;
                        continue;
                    }

                    double distToEdge = (car.DirSign > 0) ? (1.0 - car.SegT) * len : car.SegT * len;

                    if (remaining < distToEdge)
                    {
                        double deltaT = remaining / len;
                        car.SegT += (car.DirSign > 0) ? deltaT : -deltaT;
                        remaining = 0;
                    }
                    else
                    {
                        remaining -= distToEdge;
                        if (car.DirSign > 0)
                        {
                            car.SegIndex++;
                            car.SegT = 0;
                            if (car.SegIndex >= _rows - 1) return true;
                        }
                        else
                        {
                            car.SegIndex--;
                            car.SegT = 1;
                            if (car.SegIndex < 0) return true;
                        }
                    }
                }
            }

            UpdateCarPoseAndTreads(car, dt);
            return false;
        }

        private void UpdateCarPoseAndTreads(CarInstance car, double dt)
        {
            Point3D pos;
            Vector3D forward;

            if (car.Axis == TravelAxis.Horizontal)
            {
                int row = Clamp(car.FixedIndex, 0, _rows - 1);
                int seg = Clamp(car.SegIndex, 0, _cols - 2);

                Point3D a = _surfaceLocalPoints[row, seg];
                Point3D b = _surfaceLocalPoints[row, seg + 1];
                pos = Lerp(a, b, car.SegT);

                forward = (b - a);
                if (car.DirSign < 0) forward = -forward;
            }
            else
            {
                int col = Clamp(car.FixedIndex, 0, _cols - 1);
                int seg = Clamp(car.SegIndex, 0, _rows - 2);

                Point3D a = _surfaceLocalPoints[seg, col];
                Point3D b = _surfaceLocalPoints[seg + 1, col];
                pos = Lerp(a, b, car.SegT);

                forward = (b - a);
                if (car.DirSign < 0) forward = -forward;
            }

            if (forward.LengthSquared > 1e-12)
                forward.Normalize();
            else
                forward = new Vector3D(1, 0, 0);

            // Normal near current point
            int rr, cc;
            if (car.Axis == TravelAxis.Horizontal)
            {
                rr = Clamp(car.FixedIndex, 0, _rows - 1);
                cc = Clamp(car.SegIndex + (car.SegT >= 0.5 ? 1 : 0), 0, _cols - 1);
            }
            else
            {
                rr = Clamp(car.SegIndex + (car.SegT >= 0.5 ? 1 : 0), 0, _rows - 1);
                cc = Clamp(car.FixedIndex, 0, _cols - 1);
            }

            Vector3D n = GetSurfaceNormal(rr, cc);
            if (n.LengthSquared > 1e-12) n.Normalize();

            // Keep on clicked side (normalSign chosen so this faces camera)
            n *= car.NormalSign;

            // Flip based on "downward normal" in LOCAL surface coordinates
            if (car.FlipRotation != null)
                car.FlipRotation.Angle = (n.Z < 0) ? 180.0 : 0.0;

            // Lift above surface to avoid intersection
            Point3D lifted = pos + n * car.Lift;

            car.Translate.OffsetX = lifted.X;
            car.Translate.OffsetY = lifted.Y;
            car.Translate.OffsetZ = lifted.Z;

            // Yaw in XY plane
            double angleRad = Math.Atan2(forward.Y, forward.X);
            car.Rotation.Angle = angleRad * 180.0 / Math.PI;

            // Treads
            if (!car.HasLastPos)
            {
                car.LastPos = pos;
                car.HasLastPos = true;
                car.DistanceSinceLastTread = 0;
                return;
            }

            Vector3D step = pos - car.LastPos;
            double d = step.Length;
            if (d < 1e-9)
                return;

            car.DistanceSinceLastTread += d;

            if (car.DistanceSinceLastTread >= car.TreadSpacing)
            {
                car.DistanceSinceLastTread = 0;

                Vector3D right = Vector3D.CrossProduct(n, forward);
                if (right.LengthSquared > 1e-12) right.Normalize();
                else right = new Vector3D(0, 1, 0);

                Point3D leftWheel = pos + right * car.WheelOffset;
                Point3D rightWheel = pos - right * car.WheelOffset;

                AddTreadStamp(leftWheel, forward, right, n, car.TreadLength, car.TireWidth);
                AddTreadStamp(rightWheel, forward, right, n, car.TreadLength, car.TireWidth);
            }

            car.LastPos = pos;
        }

        private void AddTreadStamp(Point3D center, Vector3D forward, Vector3D right, Vector3D normal, double length, double width)
        {
            if (_tracksPos == null || _tracksIdx == null || _tracksNormals == null)
                return;

            if (normal.LengthSquared < 1e-12)
                normal = new Vector3D(0, 0, 1);
            else
                normal.Normalize();

            if (forward.LengthSquared < 1e-12)
                forward = new Vector3D(1, 0, 0);
            else
                forward.Normalize();

            if (right.LengthSquared < 1e-12)
                right = new Vector3D(0, 1, 0);
            else
                right.Normalize();

            double halfL = length / 2.0;
            double halfW = width / 2.0;

            // Lift tread slightly above surface (also along the correct side normal)
            double lift = Math.Max(0.002 * _spanForScale, 0.01);
            Vector3D up = normal * lift;

            Point3D p1 = center + forward * halfL + right * halfW + up;
            Point3D p2 = center + forward * halfL - right * halfW + up;
            Point3D p3 = center - forward * halfL + right * halfW + up;
            Point3D p4 = center - forward * halfL - right * halfW + up;

            int baseIndex = _tracksPos.Count;

            _tracksPos.Add(p1);
            _tracksPos.Add(p2);
            _tracksPos.Add(p3);
            _tracksPos.Add(p4);

            _tracksNormals.Add(normal);
            _tracksNormals.Add(normal);
            _tracksNormals.Add(normal);
            _tracksNormals.Add(normal);

            _tracksIdx.Add(baseIndex + 0);
            _tracksIdx.Add(baseIndex + 2);
            _tracksIdx.Add(baseIndex + 1);

            _tracksIdx.Add(baseIndex + 2);
            _tracksIdx.Add(baseIndex + 3);
            _tracksIdx.Add(baseIndex + 1);
        }

        private bool UpdateFalling(CarInstance car, double dt)
        {
            car.FallVelocityWorld += 9.81 * dt;

            Vector3D worldMove = new Vector3D(0, -car.FallVelocityWorld * dt, 0);
            Vector3D localMove = WorldVectorToLocal(worldMove);

            car.Translate.OffsetX += localMove.X;
            car.Translate.OffsetY += localMove.Y;
            car.Translate.OffsetZ += localMove.Z;

            // Remove when sufficiently below in world space
            Point3D localPos = new Point3D(car.Translate.OffsetX, car.Translate.OffsetY, car.Translate.OffsetZ);
            Point3D worldPos = LocalPointToWorld(localPos);

            if (worldPos.Y < -_spanForScale * 3.0)
                return true;

            return false;
        }

        // ============================================================
        // Surface normal (central differences on grid)
        // ============================================================
        private Vector3D GetSurfaceNormal(int r, int c)
        {
            int r0 = Clamp(r - 1, 0, _rows - 1);
            int r1 = Clamp(r + 1, 0, _rows - 1);
            int c0 = Clamp(c - 1, 0, _cols - 1);
            int c1 = Clamp(c + 1, 0, _cols - 1);

            Vector3D dx = _surfaceLocalPoints[r, c1] - _surfaceLocalPoints[r, c0];
            Vector3D dy = _surfaceLocalPoints[r1, c] - _surfaceLocalPoints[r0, c];

            Vector3D n = Vector3D.CrossProduct(dx, dy);
            if (n.LengthSquared < 1e-12)
                return new Vector3D(0, 0, 1);

            n.Normalize();
            return n;
        }

        // ============================================================
        // Transforms: world<->local for surface rotation
        // ============================================================
        private Vector3D WorldVectorToLocal(Vector3D worldV)
        {
            var m = _surfaceTransform.Value;
            if (m.HasInverse)
            {
                m.Invert();
                return m.Transform(worldV);
            }
            return worldV;
        }

        private Point3D WorldPointToLocal(Point3D worldP)
        {
            var m = _surfaceTransform.Value;
            if (m.HasInverse)
            {
                m.Invert();
                return m.Transform(worldP);
            }
            return worldP;
        }

        private Point3D LocalPointToWorld(Point3D localP)
        {
            var m = _surfaceTransform.Value;
            return m.Transform(localP);
        }

        // ============================================================
        // Helpers
        // ============================================================
        private static double Distance(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static Point3D Lerp(Point3D a, Point3D b, double t)
        {
            return new Point3D(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t);
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        // ============================================================
        // Bounds, normals, gradient brush
        // ============================================================
        private static (double minX, double maxX, double minY, double maxY, double minZ, double maxZ) CalculateBounds(Point3D[,] data)
        {
            double minX = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity;
            double maxY = double.NegativeInfinity;
            double minZ = double.PositiveInfinity;
            double maxZ = double.NegativeInfinity;

            int rows = data.GetLength(0);
            int cols = data.GetLength(1);

            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    var p = data[r, c];

                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;

                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;

                    if (p.Z < minZ) minZ = p.Z;
                    if (p.Z > maxZ) maxZ = p.Z;
                }

            if (double.IsInfinity(minX))
                minX = maxX = minY = maxY = minZ = maxZ = 0;

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
                    normal.Normalize();

                normals[i0] += normal;
                normals[i1] += normal;
                normals[i2] += normal;
            }

            var normalizedNormals = new Vector3DCollection(positions.Count);
            for (int i = 0; i < normals.Length; i++)
            {
                var n = normals[i];
                if (n.LengthSquared > double.Epsilon)
                    n.Normalize();
                normalizedNormals.Add(n);
            }

            return normalizedNormals;
        }

        private static LinearGradientBrush CreateGradientBrush()
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

        // ============================================================
        // Internal types
        // ============================================================
        private enum CarState
        {
            Driving,
            Falling
        }

        private enum TravelAxis
        {
            Horizontal,
            Vertical
        }

        private enum TravelDir
        {
            Right = 0,
            Left = 1,
            Up = 2,
            Down = 3
        }

        private sealed class CarInstance
        {
            public Model3DGroup Model;
            public TranslateTransform3D Translate;
            public AxisAngleRotation3D Rotation;
            public AxisAngleRotation3D FlipRotation;

            public CarState State;

            public TravelAxis Axis;
            public int FixedIndex;
            public int SegIndex;
            public double SegT;
            public int DirSign;

            public double Speed;

            public double WheelOffset;
            public double TireWidth;
            public double TreadLength;
            public double TreadSpacing;

            public double Lift;
            public int NormalSign; // +1 or -1 (clicked side)

            public bool HasLastPos;
            public Point3D LastPos;
            public double DistanceSinceLastTread;

            public double FallVelocityWorld;
        }
    }
}
