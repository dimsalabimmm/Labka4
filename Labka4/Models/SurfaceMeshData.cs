using System;
using System.Windows.Media.Media3D;

namespace Labka4.Models
{
    public sealed class SurfaceMeshData
    {
        public SurfaceMeshData(Point3D[,] points)
        {
            Points = points ?? throw new ArgumentNullException(nameof(points));
        }

        public Point3D[,] Points { get; }

        public int Rows => Points.GetLength(0);

        public int Columns => Points.GetLength(1);
    }
}

