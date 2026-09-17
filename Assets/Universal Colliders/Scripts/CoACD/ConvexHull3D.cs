using System;
using System.Collections.Generic;

namespace UColliders.CoACD
{
    /// <summary>
    /// 3D QuickHull convex hull algorithm.
    /// </summary>
    internal static class ConvexHull3D
    {
        struct HullFace
        {
            public int v0, v1, v2;
            public Vec3d normal;
            public double dist; // signed distance from origin along normal
            public List<int> conflictList; // indices into input points that are outside this face
            public bool alive;
        }

        /// <summary>
        /// Compute the convex hull of a set of 3D points.
        /// Returns the hull vertices and triangle indices.
        /// </summary>
        public static void Compute(Vec3d[] points, out Vec3d[] hullVerts, out int[] hullTris)
        {
            var tHull = CoACDProfiler.Begin();
            int n = points.Length;
            if (n < 4)
            {
                ComputeDegenerate(points, out hullVerts, out hullTris);
                CoACDProfiler.End("ConvexHull3D.Compute", tHull);
                return;
            }

            // Find initial tetrahedron
            int i0, i1, i2, i3;
            if (!FindInitialTetrahedron(points, out i0, out i1, out i2, out i3))
            {
                ComputeDegenerate(points, out hullVerts, out hullTris);
                CoACDProfiler.End("ConvexHull3D.Compute", tHull);
                return;
            }

            var faces = new List<HullFace>();
            // Edge → face index map. Key = (min_vertex, max_vertex, oriented_v0, oriented_v1)
            // We store oriented half-edge → face
            var edgeToFace = new Dictionary<long, int>();

            // Create initial 4 faces of tetrahedron
            // Make sure orientation is consistent (outward normals)
            Vec3d centroid = (points[i0] + points[i1] + points[i2] + points[i3]) * 0.25;

            int[][] tetraFaces = {
                new[] { i0, i1, i2 },
                new[] { i0, i2, i3 },
                new[] { i0, i3, i1 },
                new[] { i1, i3, i2 }
            };

            for (int f = 0; f < 4; f++)
            {
                int a = tetraFaces[f][0], b = tetraFaces[f][1], c = tetraFaces[f][2];
                Vec3d normal = Vec3d.Cross(points[b] - points[a], points[c] - points[a]);
                // Orient outward: normal should point away from centroid
                if (Vec3d.Dot(normal, points[a] - centroid) < 0)
                {
                    int tmp = b; b = c; c = tmp;
                    normal = -normal;
                }
                normal = normal.Normalized();

                var face = new HullFace
                {
                    v0 = a, v1 = b, v2 = c,
                    normal = normal,
                    dist = Vec3d.Dot(normal, points[a]),
                    conflictList = new List<int>(),
                    alive = true
                };
                int fi = faces.Count;
                faces.Add(face);
                AddEdge(edgeToFace, a, b, fi);
                AddEdge(edgeToFace, b, c, fi);
                AddEdge(edgeToFace, c, a, fi);
            }

            // Assign points to conflict lists
            var usedInTetra = new HashSet<int> { i0, i1, i2, i3 };
            for (int i = 0; i < n; i++)
            {
                if (usedInTetra.Contains(i)) continue;
                AssignPointToFace(points, faces, i);
            }

            // Main QuickHull loop
            // Process faces with conflict points
            bool progress = true;
            while (progress)
            {
                progress = false;
                for (int fi = 0; fi < faces.Count; fi++)
                {
                    var face = faces[fi];
                    if (!face.alive || face.conflictList == null || face.conflictList.Count == 0)
                        continue;

                    // Find furthest point
                    int eyeIdx = -1;
                    double maxDist = -1;
                    for (int ci = 0; ci < face.conflictList.Count; ci++)
                    {
                        int pi = face.conflictList[ci];
                        double d = Vec3d.Dot(face.normal, points[pi]) - face.dist;
                        if (d > maxDist)
                        {
                            maxDist = d;
                            eyeIdx = pi;
                        }
                    }

                    if (eyeIdx < 0 || maxDist < 1e-10) continue;

                    // Find all visible faces from eyeIdx
                    var visible = new List<int>();
                    var visited = new HashSet<int>();
                    var stack = new Stack<int>();
                    stack.Push(fi);
                    visited.Add(fi);

                    while (stack.Count > 0)
                    {
                        int cf = stack.Pop();
                        var cface = faces[cf];
                        if (!cface.alive) continue;

                        double d = Vec3d.Dot(cface.normal, points[eyeIdx]) - cface.dist;
                        if (d > -1e-10) // visible (or nearly coplanar)
                        {
                            visible.Add(cf);
                            // Visit neighbors
                            TryVisitNeighbor(edgeToFace, faces, cface.v0, cface.v1, visited, stack);
                            TryVisitNeighbor(edgeToFace, faces, cface.v1, cface.v2, visited, stack);
                            TryVisitNeighbor(edgeToFace, faces, cface.v2, cface.v0, visited, stack);
                        }
                    }

                    if (visible.Count == 0) continue;

                    // Find horizon edges: edges of visible faces whose opposite face is not visible
                    var horizonEdges = new List<long>();
                    var visibleSet = new HashSet<int>(visible);
                    for (int vi = 0; vi < visible.Count; vi++)
                    {
                        var vf = faces[visible[vi]];
                        CheckHorizonEdge(edgeToFace, visibleSet, vf.v0, vf.v1, horizonEdges);
                        CheckHorizonEdge(edgeToFace, visibleSet, vf.v1, vf.v2, horizonEdges);
                        CheckHorizonEdge(edgeToFace, visibleSet, vf.v2, vf.v0, horizonEdges);
                    }

                    if (horizonEdges.Count == 0) continue;

                    // Collect orphaned conflict points
                    var orphans = new List<int>();
                    for (int vi = 0; vi < visible.Count; vi++)
                    {
                        var vf = faces[visible[vi]];
                        if (vf.conflictList != null)
                        {
                            for (int ci = 0; ci < vf.conflictList.Count; ci++)
                            {
                                if (vf.conflictList[ci] != eyeIdx)
                                    orphans.Add(vf.conflictList[ci]);
                            }
                        }
                    }

                    // Remove visible faces
                    for (int vi = 0; vi < visible.Count; vi++)
                    {
                        int vfi = visible[vi];
                        var vf = faces[vfi];
                        RemoveEdge(edgeToFace, vf.v0, vf.v1);
                        RemoveEdge(edgeToFace, vf.v1, vf.v2);
                        RemoveEdge(edgeToFace, vf.v2, vf.v0);
                        vf.alive = false;
                        vf.conflictList = null;
                        faces[vfi] = vf;
                    }

                    // Create new faces from eye point to horizon edges
                    var newFaces = new List<int>();
                    for (int hi = 0; hi < horizonEdges.Count; hi++)
                    {
                        DecodeEdge(horizonEdges[hi], out int ea, out int eb);
                        // New face: eyeIdx, eb, ea (reversed horizon edge + eye point)
                        Vec3d normal = Vec3d.Cross(points[eb] - points[eyeIdx], points[ea] - points[eyeIdx]).Normalized();
                        if (normal.SqrMagnitude() < 1e-20)
                            continue;

                        var nf = new HullFace
                        {
                            v0 = eyeIdx, v1 = eb, v2 = ea,
                            normal = normal,
                            dist = Vec3d.Dot(normal, points[eyeIdx]),
                            conflictList = new List<int>(),
                            alive = true
                        };

                        int nfi = faces.Count;
                        faces.Add(nf);
                        newFaces.Add(nfi);
                        AddEdge(edgeToFace, eyeIdx, eb, nfi);
                        AddEdge(edgeToFace, eb, ea, nfi);
                        AddEdge(edgeToFace, ea, eyeIdx, nfi);
                    }

                    // Reassign orphans to new faces
                    for (int oi = 0; oi < orphans.Count; oi++)
                        AssignPointToFaces(points, faces, newFaces, orphans[oi]);

                    progress = true;
                    break; // restart scan since faces list changed
                }
            }

            // Extract result
            ExtractHull(points, faces, out hullVerts, out hullTris);
            CoACDProfiler.End("ConvexHull3D.Compute", tHull);
        }

        static bool FindInitialTetrahedron(Vec3d[] pts, out int i0, out int i1, out int i2, out int i3)
        {
            i0 = i1 = i2 = i3 = 0;
            int n = pts.Length;

            // Find two most distant points along each axis
            int[] extremes = new int[6]; // minX, maxX, minY, maxY, minZ, maxZ
            for (int i = 1; i < n; i++)
            {
                if (pts[i].x < pts[extremes[0]].x) extremes[0] = i;
                if (pts[i].x > pts[extremes[1]].x) extremes[1] = i;
                if (pts[i].y < pts[extremes[2]].y) extremes[2] = i;
                if (pts[i].y > pts[extremes[3]].y) extremes[3] = i;
                if (pts[i].z < pts[extremes[4]].z) extremes[4] = i;
                if (pts[i].z > pts[extremes[5]].z) extremes[5] = i;
            }

            // Pick the pair with maximum distance
            double maxDist = -1;
            for (int a = 0; a < 6; a++)
            {
                for (int b = a + 1; b < 6; b++)
                {
                    double d = Vec3d.SqrDistance(pts[extremes[a]], pts[extremes[b]]);
                    if (d > maxDist)
                    {
                        maxDist = d;
                        i0 = extremes[a];
                        i1 = extremes[b];
                    }
                }
            }

            if (maxDist < 1e-20) return false;

            // Find point furthest from line i0-i1
            Vec3d lineDir = (pts[i1] - pts[i0]).Normalized();
            double maxLineDist = -1;
            for (int i = 0; i < n; i++)
            {
                Vec3d diff = pts[i] - pts[i0];
                Vec3d proj = lineDir * Vec3d.Dot(diff, lineDir);
                double d = (diff - proj).SqrMagnitude();
                if (d > maxLineDist)
                {
                    maxLineDist = d;
                    i2 = i;
                }
            }
            if (maxLineDist < 1e-20) return false;

            // Find point furthest from plane i0-i1-i2
            Vec3d planeNormal = Vec3d.Cross(pts[i1] - pts[i0], pts[i2] - pts[i0]).Normalized();
            double maxPlaneDist = -1;
            for (int i = 0; i < n; i++)
            {
                double d = Math.Abs(Vec3d.Dot(pts[i] - pts[i0], planeNormal));
                if (d > maxPlaneDist)
                {
                    maxPlaneDist = d;
                    i3 = i;
                }
            }
            return maxPlaneDist > 1e-15;
        }

        static void AssignPointToFace(Vec3d[] pts, List<HullFace> faces, int pi)
        {
            double bestDist = 1e-10;
            int bestFace = -1;
            for (int fi = 0; fi < faces.Count; fi++)
            {
                var f = faces[fi];
                if (!f.alive) continue;
                double d = Vec3d.Dot(f.normal, pts[pi]) - f.dist;
                if (d > bestDist)
                {
                    bestDist = d;
                    bestFace = fi;
                }
            }
            if (bestFace >= 0)
            {
                var f = faces[bestFace];
                f.conflictList.Add(pi);
                faces[bestFace] = f;
            }
        }

        static void AssignPointToFaces(Vec3d[] pts, List<HullFace> faces, List<int> faceIndices, int pi)
        {
            double bestDist = 1e-10;
            int bestFace = -1;
            for (int i = 0; i < faceIndices.Count; i++)
            {
                int fi = faceIndices[i];
                var f = faces[fi];
                if (!f.alive) continue;
                double d = Vec3d.Dot(f.normal, pts[pi]) - f.dist;
                if (d > bestDist)
                {
                    bestDist = d;
                    bestFace = fi;
                }
            }
            if (bestFace >= 0)
            {
                var f = faces[bestFace];
                f.conflictList.Add(pi);
                faces[bestFace] = f;
            }
        }

        static long EncodeEdge(int a, int b) => ((long)a << 32) | (long)(uint)b;
        static void DecodeEdge(long e, out int a, out int b)
        {
            a = (int)(e >> 32);
            b = (int)(e & 0xFFFFFFFFL);
        }

        static void AddEdge(Dictionary<long, int> map, int a, int b, int faceIdx)
        {
            map[EncodeEdge(a, b)] = faceIdx;
        }

        static void RemoveEdge(Dictionary<long, int> map, int a, int b)
        {
            map.Remove(EncodeEdge(a, b));
        }

        static void TryVisitNeighbor(Dictionary<long, int> edgeToFace, List<HullFace> faces,
            int ea, int eb, HashSet<int> visited, Stack<int> stack)
        {
            // The opposite half-edge is (eb, ea)
            int neighborFace;
            if (edgeToFace.TryGetValue(EncodeEdge(eb, ea), out neighborFace))
            {
                if (!visited.Contains(neighborFace) && faces[neighborFace].alive)
                {
                    visited.Add(neighborFace);
                    stack.Push(neighborFace);
                }
            }
        }

        static void CheckHorizonEdge(Dictionary<long, int> edgeToFace, HashSet<int> visibleSet,
            int ea, int eb, List<long> horizonEdges)
        {
            // Opposite half-edge
            int neighbor;
            if (edgeToFace.TryGetValue(EncodeEdge(eb, ea), out neighbor))
            {
                if (!visibleSet.Contains(neighbor))
                    horizonEdges.Add(EncodeEdge(ea, eb));
            }
            else
            {
                // No opposite face means boundary — treat as horizon
                horizonEdges.Add(EncodeEdge(ea, eb));
            }
        }

        static void ExtractHull(Vec3d[] pts, List<HullFace> faces, out Vec3d[] hullVerts, out int[] hullTris)
        {
            var vertMap = new Dictionary<int, int>();
            var verts = new List<Vec3d>();
            var tris = new List<int>();

            for (int fi = 0; fi < faces.Count; fi++)
            {
                var f = faces[fi];
                if (!f.alive) continue;

                int a = GetOrAddVertex(vertMap, verts, pts, f.v0);
                int b = GetOrAddVertex(vertMap, verts, pts, f.v1);
                int c = GetOrAddVertex(vertMap, verts, pts, f.v2);
                tris.Add(a);
                tris.Add(b);
                tris.Add(c);
            }

            hullVerts = verts.ToArray();
            hullTris = tris.ToArray();
        }

        static int GetOrAddVertex(Dictionary<int, int> map, List<Vec3d> verts, Vec3d[] pts, int origIdx)
        {
            int idx;
            if (map.TryGetValue(origIdx, out idx)) return idx;
            idx = verts.Count;
            verts.Add(pts[origIdx]);
            map[origIdx] = idx;
            return idx;
        }

        static void ComputeDegenerate(Vec3d[] points, out Vec3d[] hullVerts, out int[] hullTris)
        {
            // For degenerate cases (< 4 points or coplanar), return a minimal hull
            if (points.Length == 0)
            {
                hullVerts = Array.Empty<Vec3d>();
                hullTris = Array.Empty<int>();
                return;
            }

            // Return all unique points as vertices, with a trivial triangle fan if possible
            var unique = new List<Vec3d>();
            for (int i = 0; i < points.Length; i++)
            {
                bool dup = false;
                for (int j = 0; j < unique.Count; j++)
                {
                    if (Vec3d.SqrDistance(points[i], unique[j]) < 1e-20)
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup) unique.Add(points[i]);
            }

            hullVerts = unique.ToArray();
            if (unique.Count < 3)
            {
                hullTris = Array.Empty<int>();
                return;
            }

            // Simple triangle fan for coplanar points
            var tris = new List<int>();
            for (int i = 1; i < unique.Count - 1; i++)
            {
                tris.Add(0);
                tris.Add(i);
                tris.Add(i + 1);
            }
            hullTris = tris.ToArray();
        }

        /// <summary>
        /// Compute the convex hull and return it as a CoACDMesh.
        /// </summary>
        public static CoACDMesh ComputeHull(CoACDMesh mesh)
        {
            Compute(mesh.vertices, out Vec3d[] hv, out int[] ht);
            var result = new CoACDMesh(hv, ht);
            return result;
        }

        /// <summary>
        /// Compute the convex hull from a raw set of points and return it as a CoACDMesh.
        /// </summary>
        public static CoACDMesh ComputeHull(Vec3d[] points)
        {
            Compute(points, out Vec3d[] hv, out int[] ht);
            return new CoACDMesh(hv, ht);
        }
    }
}
