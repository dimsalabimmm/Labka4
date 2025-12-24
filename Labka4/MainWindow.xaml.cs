using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Media3D;
using Labka4.Models;

namespace Labka4
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<FunctionTab> FunctionTabs { get; } = new ObservableCollection<FunctionTab>();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            InitializeTabs();
        }

        private void InitializeTabs()
        {
            FunctionTabs.Clear();
            FunctionTabs.Add(new FunctionTab(
                "Волновая поверхность",
                new SurfaceMeshData(CreateSurface((x, y) => Math.Sin(x) * Math.Cos(y), -Math.PI, Math.PI, -Math.PI, Math.PI, 60, 60))));

            FunctionTabs.Add(new FunctionTab(
                "Гауссова вершина",
                new SurfaceMeshData(CreateSurface((x, y) => Math.Exp(-(x * x + y * y) / 4.0), -4, 4, -4, 4, 55, 55))));

            FunctionTabs.Add(new FunctionTab(
                "Седловая поверхность",
                new SurfaceMeshData(CreateSurface((x, y) => (x * x - y * y) / 4.0, -4, 4, -4, 4, 50, 50))));
        }

        private static Point3D[,] CreateSurface(
            Func<double, double, double> function,
            double xMin,
            double xMax,
            double yMin,
            double yMax,
            int xSegments,
            int ySegments)
        {
            if (xSegments < 2 || ySegments < 2)
            {
                throw new ArgumentException("Сегметов-то должно быть хотя бы 2!");
            }

            var points = new Point3D[ySegments, xSegments];
            double xStep = (xMax - xMin) / (xSegments - 1);
            double yStep = (yMax - yMin) / (ySegments - 1);

            for (int row = 0; row < ySegments; row++)
            {
                double y = yMin + row * yStep;
                for (int column = 0; column < xSegments; column++)
                {
                    double x = xMin + column * xStep;
                    double z = function(x, y);
                    points[row, column] = new Point3D(x, y, z);
                }
            }

            return points;
        }
    }

    public sealed class FunctionTab
    {
        public FunctionTab(string title, SurfaceMeshData surfaceData)
        {
            Title = title;
            SurfaceData = surfaceData ?? throw new ArgumentNullException(nameof(surfaceData));
        }

        public string Title { get; }

        public SurfaceMeshData SurfaceData { get; }

        public override string ToString()
        {
            return Title;
        }
    }
}
