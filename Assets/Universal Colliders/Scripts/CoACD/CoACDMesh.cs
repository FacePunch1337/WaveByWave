using System;
using System.Collections.Generic;

namespace UColliders.CoACD
{
    /// <summary>
    /// Internal mesh representation for CoACD computations.
    /// Uses double-precision vertices for numerical stability.
    /// </summary>
    internal class CoACDMesh
    {
        /// <summary>Vertex positions.</summary>
        public Vec3d[] vertices;

        /// <summary>Triangle indices (3 ints per triangle, 0-indexed).</summary>
        public int[] triangles;

        /// <summary>Axis-aligned bounding box: minX, maxX, minY, maxY, minZ, maxZ.</summary>
        public double[] bbox;

        CoACDMesh _cachedHull;

        public int VertexCount => vertices.Length;
        public int TriangleCount => triangles.Length / 3;

        public CoACDMesh(Vec3d[] vertices, int[] triangles)
        {
            this.vertices = vertices;
            this.triangles = triangles;
            ComputeBBox();
        }

        public CoACDMesh(CoACDMesh other)
        {
            vertices = (Vec3d[])other.vertices.Clone();
            triangles = (int[])other.triangles.Clone();
            bbox = (double[])other.bbox.Clone();
        }

        void ComputeBBox()
        {
            bbox = new double[6];
            if (vertices.Length == 0) return;
            bbox[0] = bbox[1] = vertices[0].x;
            bbox[2] = bbox[3] = vertices[0].y;
            bbox[4] = bbox[5] = vertices[0].z;
            for (int i = 1; i < vertices.Length; i++)
            {
                if (vertices[i].x < bbox[0]) bbox[0] = vertices[i].x;
                if (vertices[i].x > bbox[1]) bbox[1] = vertices[i].x;
                if (vertices[i].y < bbox[2]) bbox[2] = vertices[i].y;
                if (vertices[i].y > bbox[3]) bbox[3] = vertices[i].y;
                if (vertices[i].z < bbox[4]) bbox[4] = vertices[i].z;
                if (vertices[i].z > bbox[5]) bbox[5] = vertices[i].z;
            }
        }

        /// <summary>
        /// Compute the signed volume of this mesh (positive if normals point outward).
        /// </summary>
        public double ComputeVolume()
        {
            double vol = 0;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vec3d p0 = vertices[triangles[t]];
                Vec3d p1 = vertices[triangles[t + 1]];
                Vec3d p2 = vertices[triangles[t + 2]];
                vol += SignedTetraVolume(p0, p1, p2);
            }
            return vol;
        }

        /// <summary>
        /// Compute total surface area.
        /// </summary>
        public double ComputeArea()
        {
            double area = 0;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                area += TriangleArea(
                    vertices[triangles[t]],
                    vertices[triangles[t + 1]],
                    vertices[triangles[t + 2]]);
            }
            return area;
        }

        /// <summary>
        /// Compute the barycenter (centroid of vertices).
        /// </summary>
        public Vec3d ComputeBarycenter()
        {
            Vec3d c = Vec3d.Zero;
            for (int i = 0; i < vertices.Length; i++)
                c = c + vertices[i];
            if (vertices.Length > 0)
                c = c / vertices.Length;
            return c;
        }

        /// <summary>
        /// Compute convex hull of this mesh and cache the result.
        /// </summary>
        public CoACDMesh GetConvexHull()
        {
            if (_cachedHull == null)
                _cachedHull = ConvexHull3D.ComputeHull(this);
            return _cachedHull;
        }

        /// <summary>
        /// Compute an approximate convex hull using a downsampled vertex set.
        /// Uses at most <paramref name="maxVerts"/> vertices (including extremes).
        /// Much faster than full hull for large meshes; suitable for MCTS rollouts.
        /// </summary>
        public CoACDMesh GetApproxConvexHull(int maxVerts = 64)
        {
            if (vertices.Length <= maxVerts)
                return GetConvexHull();

            int n = vertices.Length;

            // Farthest-point sampling: preserves geometric features (corners, edges)
            // by always picking the point most distant from the current selection.
            // O(n × maxVerts) — fast for typical sizes.

            // Track min squared distance from each vertex to any selected vertex
            var minDist = new double[n];
            var selected = new Vec3d[maxVerts];
            var used = new bool[n];
            int count = 0;

            // Seed with the 6 axis extremes
            int[] extremeIdx = new int[6];
            for (int i = 1; i < n; i++)
            {
                if (vertices[i].x < vertices[extremeIdx[0]].x) extremeIdx[0] = i;
                if (vertices[i].x > vertices[extremeIdx[1]].x) extremeIdx[1] = i;
                if (vertices[i].y < vertices[extremeIdx[2]].y) extremeIdx[2] = i;
                if (vertices[i].y > vertices[extremeIdx[3]].y) extremeIdx[3] = i;
                if (vertices[i].z < vertices[extremeIdx[4]].z) extremeIdx[4] = i;
                if (vertices[i].z > vertices[extremeIdx[5]].z) extremeIdx[5] = i;
            }

            // Initialize distances to max
            for (int i = 0; i < n; i++)
                minDist[i] = double.MaxValue;

            // Add extremes as seeds
            for (int e = 0; e < 6; e++)
            {
                int idx = extremeIdx[e];
                if (used[idx]) continue;
                used[idx] = true;
                selected[count++] = vertices[idx];

                // Update distances from this new point
                for (int i = 0; i < n; i++)
                {
                    if (used[i]) continue;
                    double d = Vec3d.SqrDistance(vertices[i], vertices[idx]);
                    if (d < minDist[i]) minDist[i] = d;
                }
            }

            // Greedily add the farthest point from current selection
            while (count < maxVerts)
            {
                int bestIdx = -1;
                double bestDist = -1;
                for (int i = 0; i < n; i++)
                {
                    if (used[i]) continue;
                    if (minDist[i] > bestDist)
                    {
                        bestDist = minDist[i];
                        bestIdx = i;
                    }
                }
                if (bestIdx < 0) break;

                used[bestIdx] = true;
                selected[count++] = vertices[bestIdx];

                // Update distances from this new point
                Vec3d newPt = vertices[bestIdx];
                for (int i = 0; i < n; i++)
                {
                    if (used[i]) continue;
                    double d = Vec3d.SqrDistance(vertices[i], newPt);
                    if (d < minDist[i]) minDist[i] = d;
                }
            }

            if (count < maxVerts)
                Array.Resize(ref selected, count);

            return ConvexHull3D.ComputeHull(selected);
        }

        /// <summary>
        /// Invalidate the cached convex hull (call after modifying vertices/triangles).
        /// </summary>
        public void InvalidateHull()
        {
            _cachedHull = null;
        }

        /// <summary>
        /// Normalize mesh to [-1, 1] range centered at origin.
        /// Returns the center and scale factor for later recovery.
        /// </summary>
        public void Normalize(out Vec3d center, out double scale)
        {
            ComputeBBox();
            center = new Vec3d(
                (bbox[0] + bbox[1]) * 0.5,
                (bbox[2] + bbox[3]) * 0.5,
                (bbox[4] + bbox[5]) * 0.5);
            scale = Math.Max(bbox[1] - bbox[0],
                    Math.Max(bbox[3] - bbox[2], bbox[5] - bbox[4])) * 0.5;
            if (scale < 1e-15) scale = 1;

            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = (vertices[i] - center) / scale;

            ComputeBBox();
            InvalidateHull();
        }

        /// <summary>
        /// Recover from normalization.
        /// </summary>
        public void Recover(Vec3d center, double scale)
        {
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = vertices[i] * scale + center;
            ComputeBBox();
            InvalidateHull();
        }

        /// <summary>
        /// Sample points on the mesh surface proportional to triangle area.
        /// Uses a mix of random and quasi-random sampling.
        /// </summary>
        public Vec3d[] SamplePoints(int resolution, Random rng)
        {
            double totalArea = ComputeArea();
            if (totalArea < 1e-15 || TriangleCount == 0)
                return vertices;

            var points = new List<Vec3d>(resolution);
            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vec3d p0 = vertices[triangles[t]];
                Vec3d p1 = vertices[triangles[t + 1]];
                Vec3d p2 = vertices[triangles[t + 2]];
                double area = TriangleArea(p0, p1, p2);
                int count = Math.Max(1, (int)Math.Round(resolution * area / totalArea));

                for (int s = 0; s < count; s++)
                {
                    // Random barycentric coordinates
                    double u = rng.NextDouble();
                    double v = rng.NextDouble();
                    if (u + v > 1)
                    {
                        u = 1 - u;
                        v = 1 - v;
                    }
                    double w = 1 - u - v;
                    points.Add(p0 * w + p1 * u + p2 * v);
                }
            }
            return points.ToArray();
        }

        /// <summary>
        /// Signed volume of tetrahedron formed by triangle and origin.
        /// </summary>
        public static double SignedTetraVolume(Vec3d p0, Vec3d p1, Vec3d p2)
        {
            return (p0.x * (p1.y * p2.z - p2.y * p1.z)
                  - p1.x * (p0.y * p2.z - p2.y * p0.z)
                  + p2.x * (p0.y * p1.z - p1.y * p0.z)) / 6.0;
        }

        /// <summary>
        /// Area of a triangle.
        /// </summary>
        public static double TriangleArea(Vec3d p0, Vec3d p1, Vec3d p2)
        {
            return Vec3d.Cross(p1 - p0, p2 - p0).Magnitude() * 0.5;
        }

        /// <summary>
        /// Minimum distance from a point to a triangle (for Hausdorff computation).
        /// </summary>
        public static double PointToTriangleDistance(Vec3d p, Vec3d a, Vec3d b, Vec3d c)
        {
            Vec3d ab = b - a, ac = c - a, ap = p - a;
            double d1 = Vec3d.Dot(ab, ap);
            double d2 = Vec3d.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return Vec3d.Distance(p, a);

            Vec3d bp = p - b;
            double d3 = Vec3d.Dot(ab, bp);
            double d4 = Vec3d.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return Vec3d.Distance(p, b);

            Vec3d cp = p - c;
            double d5 = Vec3d.Dot(ab, cp);
            double d6 = Vec3d.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return Vec3d.Distance(p, c);

            double vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0)
            {
                double v = d1 / (d1 - d3);
                return Vec3d.Distance(p, a + ab * v);
            }

            double vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0)
            {
                double v = d2 / (d2 - d6);
                return Vec3d.Distance(p, a + ac * v);
            }

            double va = d3 * d6 - d5 * d4;
            if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
            {
                double v = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return Vec3d.Distance(p, b + (c - b) * v);
            }

            double denom = 1.0 / (va + vb + vc);
            double s = vb * denom;
            double t = vc * denom;
            return Vec3d.Distance(p, a + ab * s + ac * t);
        }
    }
}
