using System;
using System.Runtime.CompilerServices;

namespace UColliders.CoACD
{
    /// <summary>
    /// Double-precision 3D vector for geometric computations.
    /// </summary>
    internal struct Vec3d : IEquatable<Vec3d>
    {
        public double x, y, z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vec3d(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public double this[int i]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                switch (i)
                {
                    case 0: return x;
                    case 1: return y;
                    case 2: return z;
                    default: throw new IndexOutOfRangeException();
                }
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                switch (i)
                {
                    case 0: x = value; break;
                    case 1: y = value; break;
                    case 2: z = value; break;
                    default: throw new IndexOutOfRangeException();
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double SqrMagnitude() => x * x + y * y + z * z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Magnitude() => Math.Sqrt(SqrMagnitude());

        public Vec3d Normalized()
        {
            double m = Magnitude();
            if (m < 1e-15) return Zero;
            return new Vec3d(x / m, y / m, z / m);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Dot(Vec3d a, Vec3d b) => a.x * b.x + a.y * b.y + a.z * b.z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3d Cross(Vec3d a, Vec3d b) =>
            new Vec3d(
                a.y * b.z - a.z * b.y,
                a.z * b.x - a.x * b.z,
                a.x * b.y - a.y * b.x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Distance(Vec3d a, Vec3d b) => (a - b).Magnitude();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double SqrDistance(Vec3d a, Vec3d b)
        {
            double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return dx * dx + dy * dy + dz * dz;
        }

        public static Vec3d operator +(Vec3d a, Vec3d b) => new Vec3d(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vec3d operator -(Vec3d a, Vec3d b) => new Vec3d(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vec3d operator *(Vec3d a, double s) => new Vec3d(a.x * s, a.y * s, a.z * s);
        public static Vec3d operator *(double s, Vec3d a) => new Vec3d(a.x * s, a.y * s, a.z * s);
        public static Vec3d operator /(Vec3d a, double s) => new Vec3d(a.x / s, a.y / s, a.z / s);
        public static Vec3d operator -(Vec3d a) => new Vec3d(-a.x, -a.y, -a.z);

        public static readonly Vec3d Zero = new Vec3d(0, 0, 0);
        public static readonly Vec3d One = new Vec3d(1, 1, 1);

        public bool Equals(Vec3d other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object obj) => obj is Vec3d v && Equals(v);
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() * 397) ^ (z.GetHashCode() * 17);
        public override string ToString() => $"({x:F4}, {y:F4}, {z:F4})";
    }
}
