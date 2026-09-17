using System.Runtime.CompilerServices;

namespace UColliders.CoACD
{
    /// <summary>
    /// Plane equation ax + by + cz + d = 0 for mesh clipping.
    /// </summary>
    internal struct CoACDPlane
    {
        public double a, b, c, d;

        public CoACDPlane(double a, double b, double c, double d)
        {
            this.a = a;
            this.b = b;
            this.c = c;
            this.d = d;
        }

        /// <summary>
        /// Classify a point relative to this plane.
        /// Returns +1 (positive side), -1 (negative side), or 0 (on plane).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Side(Vec3d p, double eps = 1e-6)
        {
            double dist = a * p.x + b * p.y + c * p.z + d;
            if (dist > eps) return 1;
            if (dist < -eps) return -1;
            return 0;
        }

        /// <summary>
        /// Signed distance from point to plane.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double SignedDistance(Vec3d p) => a * p.x + b * p.y + c * p.z + d;

        /// <summary>
        /// Compute the intersection point of a line segment (p1, p2) with this plane.
        /// Assumes p1 and p2 are on opposite sides. Returns the interpolation parameter t.
        /// </summary>
        public Vec3d IntersectSegment(Vec3d p1, Vec3d p2, out double t)
        {
            double d1 = a * p1.x + b * p1.y + c * p1.z + d;
            double d2 = a * p2.x + b * p2.y + c * p2.z + d;
            t = d1 / (d1 - d2);
            return new Vec3d(
                p1.x + t * (p2.x - p1.x),
                p1.y + t * (p2.y - p1.y),
                p1.z + t * (p2.z - p1.z));
        }

        /// <summary>
        /// Create an axis-aligned plane. axis: 0=X, 1=Y, 2=Z. offset = position along axis.
        /// </summary>
        public static CoACDPlane AxisAligned(int axis, double offset)
        {
            switch (axis)
            {
                case 0: return new CoACDPlane(1, 0, 0, -offset);
                case 1: return new CoACDPlane(0, 1, 0, -offset);
                case 2: return new CoACDPlane(0, 0, 1, -offset);
                default: return new CoACDPlane(1, 0, 0, -offset);
            }
        }

        /// <summary>
        /// Returns which axis this plane is aligned to (0=X, 1=Y, 2=Z), or -1 if not axis-aligned.
        /// </summary>
        public int GetAxis()
        {
            if (a != 0 && b == 0 && c == 0) return 0;
            if (a == 0 && b != 0 && c == 0) return 1;
            if (a == 0 && b == 0 && c != 0) return 2;
            return -1;
        }

        /// <summary>
        /// Returns the offset along the axis for an axis-aligned plane.
        /// </summary>
        public double GetOffset()
        {
            int axis = GetAxis();
            switch (axis)
            {
                case 0: return -d / a;
                case 1: return -d / b;
                case 2: return -d / c;
                default: return 0;
            }
        }

        public Vec3d Normal => new Vec3d(a, b, c);
    }
}
