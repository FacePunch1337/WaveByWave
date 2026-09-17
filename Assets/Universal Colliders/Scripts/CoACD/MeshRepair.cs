using System;
using System.Collections.Generic;

namespace UColliders.CoACD
{
    /// <summary>
    /// Voxel-based mesh repair that produces a watertight manifold surface.
    /// Based on the ManifoldPlus approach (Huang et al., 2020):
    /// 1. Voxelize the mesh into an occupancy grid
    /// 2. Flood-fill from boundary to determine exterior voxels
    /// 3. Extract surface faces between occupied and exterior voxels
    /// 4. Project extracted vertices to the nearest point on the original mesh
    /// </summary>
    internal static class MeshRepair
    {
        const byte EMPTY = 0;
        const byte OCCUPIED = 1;
        const byte EXTERIOR = 2;

        /// <summary>
        /// Repair a mesh to be watertight and manifold using voxelization.
        /// </summary>
        /// <param name="input">Input mesh (may be non-manifold).</param>
        /// <param name="resolution">Voxel grid resolution per axis (20-100).</param>
        /// <param name="onProgress">Optional progress callback (message, 0-1).</param>
        /// <returns>Repaired watertight manifold mesh, or the original if repair is unnecessary.</returns>
        public static CoACDMesh Repair(CoACDMesh input, int resolution,
            Action<string, float> onProgress = null)
        {
            if (resolution < 4) resolution = 4;
            if (resolution > 200) resolution = 200;

            onProgress?.Invoke("Checking mesh manifoldness...", 0f);

            // Compute grid bounds with 5% padding to ensure boundary is exterior
            double[] bbox = input.bbox;
            double dx = bbox[1] - bbox[0];
            double dy = bbox[3] - bbox[2];
            double dz = bbox[5] - bbox[4];
            double pad = Math.Max(dx, Math.Max(dy, dz)) * 0.05 + 1e-8;

            double minX = bbox[0] - pad, maxX = bbox[1] + pad;
            double minY = bbox[2] - pad, maxY = bbox[3] + pad;
            double minZ = bbox[4] - pad, maxZ = bbox[5] + pad;

            double cellX = (maxX - minX) / resolution;
            double cellY = (maxY - minY) / resolution;
            double cellZ = (maxZ - minZ) / resolution;

            int n = resolution;
            byte[] grid = new byte[n * n * n];

            // 1. Voxelize — mark occupied cells
            onProgress?.Invoke("Voxelizing mesh...", 0.1f);
            var tVox = CoACDProfiler.Begin();
            Voxelize(input, grid, n, minX, minY, minZ, cellX, cellY, cellZ);
            CoACDProfiler.End("MeshRepair.Voxelize", tVox);

            // 2. Flood fill exterior from boundary
            onProgress?.Invoke("Flood-filling exterior...", 0.4f);
            var tFlood = CoACDProfiler.Begin();
            FloodFillExterior(grid, n);
            CoACDProfiler.End("MeshRepair.FloodFill", tFlood);

            // 3. Extract surface between occupied and exterior
            onProgress?.Invoke("Extracting surface...", 0.6f);
            var tExtract = CoACDProfiler.Begin();
            CoACDMesh extracted = ExtractSurface(grid, n,
                minX, minY, minZ, cellX, cellY, cellZ);
            CoACDProfiler.End("MeshRepair.ExtractSurface", tExtract);

            if (extracted.vertices.Length < 4 || extracted.triangles.Length < 12)
                return input; // repair failed, return original

            // 4. Project vertices to nearest point on original mesh
            onProgress?.Invoke("Projecting vertices...", 0.8f);
            var tProj = CoACDProfiler.Begin();
            ProjectVertices(extracted, input);
            CoACDProfiler.End("MeshRepair.ProjectVertices", tProj);

            onProgress?.Invoke("Mesh repair done.", 1f);
            return extracted;
        }

        /// <summary>
        /// Mark all voxels that intersect at least one triangle as OCCUPIED.
        /// Uses the Separating Axis Theorem for precise triangle-AABB tests.
        /// </summary>
        static void Voxelize(CoACDMesh mesh, byte[] grid, int n,
            double minX, double minY, double minZ,
            double cellX, double cellY, double cellZ)
        {
            Vec3d[] verts = mesh.vertices;
            int[] tris = mesh.triangles;
            int nn = n * n;

            for (int t = 0; t < tris.Length; t += 3)
            {
                Vec3d v0 = verts[tris[t]];
                Vec3d v1 = verts[tris[t + 1]];
                Vec3d v2 = verts[tris[t + 2]];

                // Triangle bounding box in grid coordinates
                int iMin = Math.Max(0, (int)((Math.Min(v0.x, Math.Min(v1.x, v2.x)) - minX) / cellX));
                int iMax = Math.Min(n - 1, (int)((Math.Max(v0.x, Math.Max(v1.x, v2.x)) - minX) / cellX));
                int jMin = Math.Max(0, (int)((Math.Min(v0.y, Math.Min(v1.y, v2.y)) - minY) / cellY));
                int jMax = Math.Min(n - 1, (int)((Math.Max(v0.y, Math.Max(v1.y, v2.y)) - minY) / cellY));
                int kMin = Math.Max(0, (int)((Math.Min(v0.z, Math.Min(v1.z, v2.z)) - minZ) / cellZ));
                int kMax = Math.Min(n - 1, (int)((Math.Max(v0.z, Math.Max(v1.z, v2.z)) - minZ) / cellZ));

                for (int i = iMin; i <= iMax; i++)
                    for (int j = jMin; j <= jMax; j++)
                        for (int k = kMin; k <= kMax; k++)
                        {
                            int idx = i * nn + j * n + k;
                            if (grid[idx] == OCCUPIED) continue;

                            double bMinX = minX + i * cellX;
                            double bMinY = minY + j * cellY;
                            double bMinZ = minZ + k * cellZ;

                            if (TriangleBoxIntersect(v0, v1, v2,
                                bMinX, bMinY, bMinZ,
                                bMinX + cellX, bMinY + cellY, bMinZ + cellZ))
                            {
                                grid[idx] = OCCUPIED;
                            }
                        }
            }
        }

        /// <summary>
        /// Test if a triangle intersects an AABB using the Separating Axis Theorem.
        /// Based on Akenine-Möller (2001) "Fast 3D Triangle-Box Overlap Testing".
        /// </summary>
        static bool TriangleBoxIntersect(Vec3d v0, Vec3d v1, Vec3d v2,
            double boxMinX, double boxMinY, double boxMinZ,
            double boxMaxX, double boxMaxY, double boxMaxZ)
        {
            // Center the box at origin
            double cx = (boxMinX + boxMaxX) * 0.5;
            double cy = (boxMinY + boxMaxY) * 0.5;
            double cz = (boxMinZ + boxMaxZ) * 0.5;
            double ex = (boxMaxX - boxMinX) * 0.5;
            double ey = (boxMaxY - boxMinY) * 0.5;
            double ez = (boxMaxZ - boxMinZ) * 0.5;

            // Translate triangle vertices relative to box center
            double ax = v0.x - cx, ay = v0.y - cy, az = v0.z - cz;
            double bx = v1.x - cx, by = v1.y - cy, bz = v1.z - cz;
            double gx = v2.x - cx, gy = v2.y - cy, gz = v2.z - cz;

            // Triangle edge vectors
            double e0x = bx - ax, e0y = by - ay, e0z = bz - az;
            double e1x = gx - bx, e1y = gy - by, e1z = gz - bz;
            double e2x = ax - gx, e2y = ay - gy, e2z = az - gz;

            // Test 9 cross-product axes (3 box face normals × 3 triangle edges)
            // (1,0,0) × edge = (0, -ez, ey) projected on YZ
            if (!SATAxis(ay, az, by, bz, gy, gz, ey, ez, -e0z, e0y)) return false;
            if (!SATAxis(ay, az, by, bz, gy, gz, ey, ez, -e1z, e1y)) return false;
            if (!SATAxis(ay, az, by, bz, gy, gz, ey, ez, -e2z, e2y)) return false;

            // (0,1,0) × edge = (ez, 0, -ex) projected on XZ
            if (!SATAxis(ax, az, bx, bz, gx, gz, ex, ez, e0z, -e0x)) return false;
            if (!SATAxis(ax, az, bx, bz, gx, gz, ex, ez, e1z, -e1x)) return false;
            if (!SATAxis(ax, az, bx, bz, gx, gz, ex, ez, e2z, -e2x)) return false;

            // (0,0,1) × edge = (-ey, ex, 0) projected on XY
            if (!SATAxis(ax, ay, bx, by, gx, gy, ex, ey, -e0y, e0x)) return false;
            if (!SATAxis(ax, ay, bx, by, gx, gy, ex, ey, -e1y, e1x)) return false;
            if (!SATAxis(ax, ay, bx, by, gx, gy, ex, ey, -e2y, e2x)) return false;

            // Test 3 AABB face normals (coordinate axes)
            if (Math.Min(ax, Math.Min(bx, gx)) > ex || Math.Max(ax, Math.Max(bx, gx)) < -ex)
                return false;
            if (Math.Min(ay, Math.Min(by, gy)) > ey || Math.Max(ay, Math.Max(by, gy)) < -ey)
                return false;
            if (Math.Min(az, Math.Min(bz, gz)) > ez || Math.Max(az, Math.Max(bz, gz)) < -ez)
                return false;

            // Test triangle face normal
            double nx = e0y * e1z - e0z * e1y;
            double ny = e0z * e1x - e0x * e1z;
            double nz = e0x * e1y - e0y * e1x;
            double d = nx * ax + ny * ay + nz * az;
            double r = ex * Math.Abs(nx) + ey * Math.Abs(ny) + ez * Math.Abs(nz);
            if (d > r || d < -r) return false;

            return true;
        }

        /// <summary>
        /// Test one SAT cross-product axis. Projects 3 triangle vertices and
        /// the box extent onto the given 2D axis, returns false if separated.
        /// </summary>
        static bool SATAxis(
            double a1, double a2, double b1, double b2, double g1, double g2,
            double extent1, double extent2, double axis1, double axis2)
        {
            double p0 = a1 * axis1 + a2 * axis2;
            double p1 = b1 * axis1 + b2 * axis2;
            double p2 = g1 * axis1 + g2 * axis2;
            double min = Math.Min(p0, Math.Min(p1, p2));
            double max = Math.Max(p0, Math.Max(p1, p2));
            double r = extent1 * Math.Abs(axis1) + extent2 * Math.Abs(axis2);
            return min <= r && max >= -r;
        }

        /// <summary>
        /// BFS flood fill from all boundary cells to mark exterior voxels.
        /// After this, EMPTY voxels that are not reachable from the boundary are interior.
        /// </summary>
        static void FloodFillExterior(byte[] grid, int n)
        {
            int nn = n * n;
            var queue = new Queue<int>();

            // Seed all EMPTY cells on the 6 faces of the grid
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    TrySeed(grid, queue, nn, n, i, j, 0);
                    TrySeed(grid, queue, nn, n, i, j, n - 1);
                    TrySeed(grid, queue, nn, n, i, 0, j);
                    TrySeed(grid, queue, nn, n, i, n - 1, j);
                    TrySeed(grid, queue, nn, n, 0, i, j);
                    TrySeed(grid, queue, nn, n, n - 1, i, j);
                }

            // BFS expand
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int i = idx / nn;
                int rem = idx % nn;
                int j = rem / n;
                int k = rem % n;

                if (i > 0)     TrySeed(grid, queue, nn, n, i - 1, j, k);
                if (i < n - 1) TrySeed(grid, queue, nn, n, i + 1, j, k);
                if (j > 0)     TrySeed(grid, queue, nn, n, i, j - 1, k);
                if (j < n - 1) TrySeed(grid, queue, nn, n, i, j + 1, k);
                if (k > 0)     TrySeed(grid, queue, nn, n, i, j, k - 1);
                if (k < n - 1) TrySeed(grid, queue, nn, n, i, j, k + 1);
            }
        }

        static void TrySeed(byte[] grid, Queue<int> queue, int nn, int n, int i, int j, int k)
        {
            int idx = i * nn + j * n + k;
            if (grid[idx] == EMPTY)
            {
                grid[idx] = EXTERIOR;
                queue.Enqueue(idx);
            }
        }

        /// <summary>
        /// Extract a triangle mesh from the boundary between occupied and exterior voxels.
        /// Each boundary face becomes two triangles with outward-facing normals.
        /// Vertices are deduplicated by grid-point position.
        /// </summary>
        static CoACDMesh ExtractSurface(byte[] grid, int n,
            double minX, double minY, double minZ,
            double cellX, double cellY, double cellZ)
        {
            var verts = new List<Vec3d>();
            var tris = new List<int>();
            // Deduplicate vertices by grid-point index.
            // Grid points range from (0,0,0) to (n,n,n) — i.e. (n+1)^3 possible points.
            var vertexMap = new Dictionary<long, int>();
            int stride = n + 1;
            int nn = n * n;

            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    for (int k = 0; k < n; k++)
                    {
                        if (grid[i * nn + j * n + k] != OCCUPIED) continue;

                        // Check each of the 6 face neighbors
                        // -X face
                        if (IsExterior(grid, n, nn, i - 1, j, k))
                            EmitQuad(verts, tris, vertexMap, stride,
                                minX, minY, minZ, cellX, cellY, cellZ,
                                i, j, k, i, j + 1, k, i, j + 1, k + 1, i, j, k + 1);
                        // +X face
                        if (IsExterior(grid, n, nn, i + 1, j, k))
                            EmitQuad(verts, tris, vertexMap, stride,
                                minX, minY, minZ, cellX, cellY, cellZ,
                                i + 1, j, k, i + 1, j, k + 1, i + 1, j + 1, k + 1, i + 1, j + 1, k);
                        // -Y face
                        if (IsExterior(grid, n, nn, i, j - 1, k))
                            EmitQuad(verts, tris, vertexMap, stride,
                                minX, minY, minZ, cellX, cellY, cellZ,
                                i, j, k, i, j, k + 1, i + 1, j, k + 1, i + 1, j, k);
                        // +Y face
                        if (IsExterior(grid, n, nn, i, j + 1, k))
                            EmitQuad(verts, tris, vertexMap, stride,
                                minX, minY, minZ, cellX, cellY, cellZ,
                                i, j + 1, k, i + 1, j + 1, k, i + 1, j + 1, k + 1, i, j + 1, k + 1);
                        // -Z face
                        if (IsExterior(grid, n, nn, i, j, k - 1))
                            EmitQuad(verts, tris, vertexMap, stride,
                                minX, minY, minZ, cellX, cellY, cellZ,
                                i, j, k, i + 1, j, k, i + 1, j + 1, k, i, j + 1, k);
                        // +Z face
                        if (IsExterior(grid, n, nn, i, j, k + 1))
                            EmitQuad(verts, tris, vertexMap, stride,
                                minX, minY, minZ, cellX, cellY, cellZ,
                                i, j, k + 1, i, j + 1, k + 1, i + 1, j + 1, k + 1, i + 1, j, k + 1);
                    }

            return new CoACDMesh(verts.ToArray(), tris.ToArray());
        }

        static bool IsExterior(byte[] grid, int n, int nn, int i, int j, int k)
        {
            if (i < 0 || i >= n || j < 0 || j >= n || k < 0 || k >= n)
                return true; // out of bounds = exterior
            return grid[i * nn + j * n + k] == EXTERIOR;
        }

        /// <summary>
        /// Emit a quad (two triangles) from four grid-point corners.
        /// Vertices are deduplicated via the vertexMap dictionary.
        /// </summary>
        static void EmitQuad(List<Vec3d> verts, List<int> tris,
            Dictionary<long, int> vertexMap, int stride,
            double minX, double minY, double minZ,
            double cellX, double cellY, double cellZ,
            int g0i, int g0j, int g0k,
            int g1i, int g1j, int g1k,
            int g2i, int g2j, int g2k,
            int g3i, int g3j, int g3k)
        {
            int v0 = GetOrAddVertex(verts, vertexMap, stride,
                minX, minY, minZ, cellX, cellY, cellZ, g0i, g0j, g0k);
            int v1 = GetOrAddVertex(verts, vertexMap, stride,
                minX, minY, minZ, cellX, cellY, cellZ, g1i, g1j, g1k);
            int v2 = GetOrAddVertex(verts, vertexMap, stride,
                minX, minY, minZ, cellX, cellY, cellZ, g2i, g2j, g2k);
            int v3 = GetOrAddVertex(verts, vertexMap, stride,
                minX, minY, minZ, cellX, cellY, cellZ, g3i, g3j, g3k);

            // Two triangles: (v0, v1, v2) and (v0, v2, v3)
            tris.Add(v0); tris.Add(v1); tris.Add(v2);
            tris.Add(v0); tris.Add(v2); tris.Add(v3);
        }

        static int GetOrAddVertex(List<Vec3d> verts, Dictionary<long, int> map,
            int stride, double minX, double minY, double minZ,
            double cellX, double cellY, double cellZ,
            int gi, int gj, int gk)
        {
            long key = (long)gi * stride * stride + (long)gj * stride + gk;
            int idx;
            if (map.TryGetValue(key, out idx)) return idx;

            idx = verts.Count;
            verts.Add(new Vec3d(
                minX + gi * cellX,
                minY + gj * cellY,
                minZ + gk * cellZ));
            map[key] = idx;
            return idx;
        }

        /// <summary>
        /// Project each extracted vertex to the nearest point on the reference mesh.
        /// Uses a spatial grid to accelerate nearest-triangle lookups.
        /// </summary>
        static void ProjectVertices(CoACDMesh extracted, CoACDMesh reference)
        {
            Vec3d[] refVerts = reference.vertices;
            int[] refTris = reference.triangles;
            Vec3d[] outVerts = extracted.vertices;
            int triCount = refTris.Length / 3;

            // Build a spatial grid over reference triangles for O(1) lookups
            double[] bbox = reference.bbox;
            int gridRes = Math.Max(4, Math.Min(64,
                (int)Math.Ceiling(Math.Pow(triCount, 1.0 / 3.0))));
            double gMinX = bbox[0], gMinY = bbox[2], gMinZ = bbox[4];
            double gCellX = (bbox[1] - bbox[0]) / gridRes + 1e-15;
            double gCellY = (bbox[3] - bbox[2]) / gridRes + 1e-15;
            double gCellZ = (bbox[5] - bbox[4]) / gridRes + 1e-15;
            int gNN = gridRes * gridRes;

            // Each grid cell stores a list of triangle indices
            var grid = new List<int>[gridRes * gridRes * gridRes];

            for (int t = 0; t < refTris.Length; t += 3)
            {
                Vec3d a = refVerts[refTris[t]];
                Vec3d b = refVerts[refTris[t + 1]];
                Vec3d c = refVerts[refTris[t + 2]];

                int iMin = Clamp((int)((Math.Min(a.x, Math.Min(b.x, c.x)) - gMinX) / gCellX), 0, gridRes - 1);
                int iMax = Clamp((int)((Math.Max(a.x, Math.Max(b.x, c.x)) - gMinX) / gCellX), 0, gridRes - 1);
                int jMin = Clamp((int)((Math.Min(a.y, Math.Min(b.y, c.y)) - gMinY) / gCellY), 0, gridRes - 1);
                int jMax = Clamp((int)((Math.Max(a.y, Math.Max(b.y, c.y)) - gMinY) / gCellY), 0, gridRes - 1);
                int kMin = Clamp((int)((Math.Min(a.z, Math.Min(b.z, c.z)) - gMinZ) / gCellZ), 0, gridRes - 1);
                int kMax = Clamp((int)((Math.Max(a.z, Math.Max(b.z, c.z)) - gMinZ) / gCellZ), 0, gridRes - 1);

                for (int gi = iMin; gi <= iMax; gi++)
                    for (int gj = jMin; gj <= jMax; gj++)
                        for (int gk = kMin; gk <= kMax; gk++)
                        {
                            int idx = gi * gNN + gj * gridRes + gk;
                            if (grid[idx] == null)
                                grid[idx] = new List<int>();
                            grid[idx].Add(t);
                        }
            }

            // Project each vertex using expanding neighborhood search
            for (int i = 0; i < outVerts.Length; i++)
            {
                Vec3d p = outVerts[i];
                int ci = Clamp((int)((p.x - gMinX) / gCellX), 0, gridRes - 1);
                int cj = Clamp((int)((p.y - gMinY) / gCellY), 0, gridRes - 1);
                int ck = Clamp((int)((p.z - gMinZ) / gCellZ), 0, gridRes - 1);

                double bestDist = double.MaxValue;
                Vec3d bestPoint = p;

                // Search expanding rings until we have a guaranteed closest point
                for (int radius = 0; radius < gridRes; radius++)
                {
                    // Check if best distance so far is closer than the next ring
                    if (radius > 1 && bestDist < double.MaxValue)
                    {
                        double ringDist = (radius - 1) * Math.Min(gCellX, Math.Min(gCellY, gCellZ));
                        if (bestDist <= ringDist * ringDist)
                            break;
                    }

                    int rMin = -radius, rMax = radius;
                    for (int di = rMin; di <= rMax; di++)
                        for (int dj = rMin; dj <= rMax; dj++)
                            for (int dk = rMin; dk <= rMax; dk++)
                            {
                                // Only process cells on the shell of this ring
                                if (radius > 0 &&
                                    Math.Abs(di) != radius &&
                                    Math.Abs(dj) != radius &&
                                    Math.Abs(dk) != radius)
                                    continue;

                                int gi = ci + di, gj = cj + dj, gk = ck + dk;
                                if (gi < 0 || gi >= gridRes || gj < 0 || gj >= gridRes ||
                                    gk < 0 || gk >= gridRes)
                                    continue;

                                var cell = grid[gi * gNN + gj * gridRes + gk];
                                if (cell == null) continue;

                                for (int ti = 0; ti < cell.Count; ti++)
                                {
                                    int t = cell[ti];
                                    Vec3d closest = ClosestPointOnTriangle(p,
                                        refVerts[refTris[t]],
                                        refVerts[refTris[t + 1]],
                                        refVerts[refTris[t + 2]]);
                                    double dist = Vec3d.SqrDistance(p, closest);
                                    if (dist < bestDist)
                                    {
                                        bestDist = dist;
                                        bestPoint = closest;
                                    }
                                }
                            }
                }

                outVerts[i] = bestPoint;
            }

            extracted.InvalidateHull();
        }

        static int Clamp(int v, int min, int max)
        {
            return v < min ? min : (v > max ? max : v);
        }

        /// <summary>
        /// Find the closest point on triangle (a, b, c) to point p.
        /// Uses Voronoi region classification.
        /// </summary>
        static Vec3d ClosestPointOnTriangle(Vec3d p, Vec3d a, Vec3d b, Vec3d c)
        {
            Vec3d ab = b - a, ac = c - a, ap = p - a;
            double d1 = Vec3d.Dot(ab, ap);
            double d2 = Vec3d.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return a;

            Vec3d bp = p - b;
            double d3 = Vec3d.Dot(ab, bp);
            double d4 = Vec3d.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return b;

            Vec3d cp = p - c;
            double d5 = Vec3d.Dot(ab, cp);
            double d6 = Vec3d.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return c;

            double vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0)
            {
                double v = d1 / (d1 - d3);
                return a + ab * v;
            }

            double vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0)
            {
                double v = d2 / (d2 - d6);
                return a + ac * v;
            }

            double va = d3 * d6 - d5 * d4;
            if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
            {
                double v = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return b + (c - b) * v;
            }

            double denom = 1.0 / (va + vb + vc);
            double s = vb * denom;
            double t = vc * denom;
            return a + ab * s + ac * t;
        }
    }
}
