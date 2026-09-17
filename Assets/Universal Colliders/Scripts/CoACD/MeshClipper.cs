using System;
using System.Collections.Generic;

namespace UColliders.CoACD
{
    /// <summary>
    /// Splits a mesh along a plane into positive and negative halves.
    /// Includes ear-clipping triangulation for the cut face.
    /// </summary>
    internal static class MeshClipper
    {
        /// <summary>
        /// Clip a mesh by a plane into positive (front) and negative (back) halves.
        /// Returns false if clipping fails or produces degenerate results.
        /// </summary>
        public static bool Clip(CoACDMesh mesh, CoACDPlane plane,
            out CoACDMesh posMesh, out CoACDMesh negMesh)
        {
            var tClip = CoACDProfiler.Begin();
            posMesh = null;
            negMesh = null;

            var posVerts = new List<Vec3d>();
            var negVerts = new List<Vec3d>();
            var posTris = new List<int>();
            var negTris = new List<int>();

            // Maps from original vertex index to new index in pos/neg lists
            var posMap = new Dictionary<int, int>();
            var negMap = new Dictionary<int, int>();

            // Edge intersection cache: (minIdx, maxIdx) → new vertex position
            var edgeIntersections = new Dictionary<long, Vec3d>();

            // Edge key → vertex index in pos/neg lists (for deduplication of intersection vertices)
            var posEdgeVertMap = new Dictionary<long, int>();
            var negEdgeVertMap = new Dictionary<long, int>();

            // Border edges for cut face triangulation (directed edges on the positive side)
            var borderEdgesPos = new List<int>(); // pairs: (from, to) indices into posVerts

            // Classify all vertices
            int[] sides = new int[mesh.vertices.Length];
            for (int i = 0; i < mesh.vertices.Length; i++)
                sides[i] = plane.Side(mesh.vertices[i]);

            // Process each triangle
            for (int t = 0; t < mesh.triangles.Length; t += 3)
            {
                int i0 = mesh.triangles[t];
                int i1 = mesh.triangles[t + 1];
                int i2 = mesh.triangles[t + 2];
                int s0 = sides[i0], s1 = sides[i1], s2 = sides[i2];
                int sum = s0 + s1 + s2;

                if (sum == 3 || (sum == 2 && (s0 >= 0 && s1 >= 0 && s2 >= 0)))
                {
                    // All on positive side (or on plane leaning positive)
                    AddTriangle(mesh.vertices, posVerts, posTris, posMap, i0, i1, i2);
                }
                else if (sum == -3 || (sum == -2 && (s0 <= 0 && s1 <= 0 && s2 <= 0)))
                {
                    // All on negative side
                    AddTriangle(mesh.vertices, negVerts, negTris, negMap, i0, i1, i2);
                }
                else if (s0 == 0 && s1 == 0 && s2 == 0)
                {
                    // Coplanar — assign based on normal direction
                    Vec3d normal = Vec3d.Cross(
                        mesh.vertices[i1] - mesh.vertices[i0],
                        mesh.vertices[i2] - mesh.vertices[i0]);
                    if (Vec3d.Dot(normal, plane.Normal) >= 0)
                        AddTriangle(mesh.vertices, posVerts, posTris, posMap, i0, i1, i2);
                    else
                        AddTriangle(mesh.vertices, negVerts, negTris, negMap, i0, i1, i2);
                }
                else
                {
                    // Triangle straddles the plane — split it
                    SplitTriangle(mesh.vertices, plane, sides,
                        i0, i1, i2, s0, s1, s2,
                        posVerts, posTris, posMap, posEdgeVertMap,
                        negVerts, negTris, negMap, negEdgeVertMap,
                        borderEdgesPos,
                        edgeIntersections);
                }
            }

            // Triangulate cut face and add to both halves
            if (borderEdgesPos.Count >= 4) // at least 2 edges = 4 ints
                TriangulateCutFace(posVerts, posTris, negVerts, negTris,
                    borderEdgesPos, plane);

            if (posVerts.Count < 4 || posTris.Count < 12 ||
                negVerts.Count < 4 || negTris.Count < 12)
            {
                CoACDProfiler.End("MeshClipper.Clip", tClip);
                return false;
            }

            posMesh = new CoACDMesh(posVerts.ToArray(), posTris.ToArray());
            negMesh = new CoACDMesh(negVerts.ToArray(), negTris.ToArray());
            CoACDProfiler.End("MeshClipper.Clip", tClip);
            return true;
        }

        static int GetOrAddVertex(List<Vec3d> verts, Dictionary<int, int> map, Vec3d[] origVerts, int origIdx)
        {
            int idx;
            if (map.TryGetValue(origIdx, out idx)) return idx;
            idx = verts.Count;
            verts.Add(origVerts[origIdx]);
            map[origIdx] = idx;
            return idx;
        }

        static int GetOrAddEdgeVertex(List<Vec3d> verts, Dictionary<long, int> edgeVertMap, long edgeKey, Vec3d pos)
        {
            int idx;
            if (edgeVertMap.TryGetValue(edgeKey, out idx)) return idx;
            idx = verts.Count;
            verts.Add(pos);
            edgeVertMap[edgeKey] = idx;
            return idx;
        }

        static void AddTriangle(Vec3d[] origVerts, List<Vec3d> verts, List<int> tris,
            Dictionary<int, int> map, int i0, int i1, int i2)
        {
            int a = GetOrAddVertex(verts, map, origVerts, i0);
            int b = GetOrAddVertex(verts, map, origVerts, i1);
            int c = GetOrAddVertex(verts, map, origVerts, i2);
            tris.Add(a);
            tris.Add(b);
            tris.Add(c);
        }

        static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        static Vec3d GetEdgeIntersection(Vec3d[] origVerts, CoACDPlane plane,
            int i0, int i1, Dictionary<long, Vec3d> cache)
        {
            long key = EdgeKey(i0, i1);
            Vec3d pt;
            if (cache.TryGetValue(key, out pt)) return pt;
            double t;
            pt = plane.IntersectSegment(origVerts[i0], origVerts[i1], out t);
            cache[key] = pt;
            return pt;
        }

        static void SplitTriangle(Vec3d[] origVerts, CoACDPlane plane, int[] sides,
            int i0, int i1, int i2, int s0, int s1, int s2,
            List<Vec3d> posVerts, List<int> posTris, Dictionary<int, int> posMap,
            Dictionary<long, int> posEdgeVertMap,
            List<Vec3d> negVerts, List<int> negTris, Dictionary<int, int> negMap,
            Dictionary<long, int> negEdgeVertMap,
            List<int> borderEdgesPos,
            Dictionary<long, Vec3d> edgeCache)
        {
            int[] vi = { i0, i1, i2 };
            int[] si = { s0, s1, s2 };

            int posCount = (s0 > 0 ? 1 : 0) + (s1 > 0 ? 1 : 0) + (s2 > 0 ? 1 : 0);
            int negCount = (s0 < 0 ? 1 : 0) + (s1 < 0 ? 1 : 0) + (s2 < 0 ? 1 : 0);

            if (posCount == 1 && negCount >= 1)
            {
                int loneIdx = s0 > 0 ? 0 : (s1 > 0 ? 1 : 2);
                SplitOnePosTwo(origVerts, plane, vi, si, loneIdx,
                    posVerts, posTris, posMap, posEdgeVertMap,
                    negVerts, negTris, negMap, negEdgeVertMap,
                    borderEdgesPos, edgeCache);
            }
            else if (negCount == 1 && posCount >= 1)
            {
                int loneIdx = s0 < 0 ? 0 : (s1 < 0 ? 1 : 2);
                SplitOneNegTwo(origVerts, plane, vi, si, loneIdx,
                    posVerts, posTris, posMap, posEdgeVertMap,
                    negVerts, negTris, negMap, negEdgeVertMap,
                    borderEdgesPos, edgeCache);
            }
            else if (posCount == 2 && negCount == 0)
            {
                AddTriangle(origVerts, posVerts, posTris, posMap, i0, i1, i2);
            }
            else if (negCount == 2 && posCount == 0)
            {
                AddTriangle(origVerts, negVerts, negTris, negMap, i0, i1, i2);
            }
            else
            {
                AddTriangle(origVerts, posVerts, posTris, posMap, i0, i1, i2);
            }
        }

        // One vertex on positive side (vi[lone]), two on negative/plane
        static void SplitOnePosTwo(Vec3d[] origVerts, CoACDPlane plane,
            int[] vi, int[] si, int lone,
            List<Vec3d> posVerts, List<int> posTris, Dictionary<int, int> posMap,
            Dictionary<long, int> posEdgeVertMap,
            List<Vec3d> negVerts, List<int> negTris, Dictionary<int, int> negMap,
            Dictionary<long, int> negEdgeVertMap,
            List<int> borderEdgesPos, Dictionary<long, Vec3d> edgeCache)
        {
            int a = lone;
            int b = (lone + 1) % 3;
            int c = (lone + 2) % 3;

            int posA = GetOrAddVertex(posVerts, posMap, origVerts, vi[a]);

            if (si[b] == 0 && si[c] == 0)
            {
                // b and c are on the plane: whole triangle goes to positive
                int posB = GetOrAddVertex(posVerts, posMap, origVerts, vi[b]);
                int posC = GetOrAddVertex(posVerts, posMap, origVerts, vi[c]);
                posTris.Add(posA); posTris.Add(posB); posTris.Add(posC);
                borderEdgesPos.Add(posC); borderEdgesPos.Add(posB);
                return;
            }

            if (si[b] == 0)
            {
                // b is on plane, c is negative: split edge a-c
                Vec3d iAC = GetEdgeIntersection(origVerts, plane, vi[a], vi[c], edgeCache);
                long keyAC = EdgeKey(vi[a], vi[c]);
                int posB = GetOrAddVertex(posVerts, posMap, origVerts, vi[b]);
                int posIAC = GetOrAddEdgeVertex(posVerts, posEdgeVertMap, keyAC, iAC);
                posTris.Add(posA); posTris.Add(posB); posTris.Add(posIAC);
                borderEdgesPos.Add(posIAC); borderEdgesPos.Add(posB);

                int negB = GetOrAddVertex(negVerts, negMap, origVerts, vi[b]);
                int negC = GetOrAddVertex(negVerts, negMap, origVerts, vi[c]);
                int negIAC = GetOrAddEdgeVertex(negVerts, negEdgeVertMap, keyAC, iAC);
                negTris.Add(negB); negTris.Add(negC); negTris.Add(negIAC);
                return;
            }

            if (si[c] == 0)
            {
                // c is on plane, b is negative: split edge a-b
                Vec3d iAB = GetEdgeIntersection(origVerts, plane, vi[a], vi[b], edgeCache);
                long keyAB = EdgeKey(vi[a], vi[b]);
                int posC = GetOrAddVertex(posVerts, posMap, origVerts, vi[c]);
                int posIAB = GetOrAddEdgeVertex(posVerts, posEdgeVertMap, keyAB, iAB);
                posTris.Add(posA); posTris.Add(posIAB); posTris.Add(posC);
                borderEdgesPos.Add(posC); borderEdgesPos.Add(posIAB);

                int negB = GetOrAddVertex(negVerts, negMap, origVerts, vi[b]);
                int negC = GetOrAddVertex(negVerts, negMap, origVerts, vi[c]);
                int negIAB = GetOrAddEdgeVertex(negVerts, negEdgeVertMap, keyAB, iAB);
                negTris.Add(negIAB); negTris.Add(negB); negTris.Add(negC);
                return;
            }

            // Both b and c are negative: split edges a-b and a-c
            Vec3d intAB = GetEdgeIntersection(origVerts, plane, vi[a], vi[b], edgeCache);
            Vec3d intAC = GetEdgeIntersection(origVerts, plane, vi[a], vi[c], edgeCache);
            long edgeKeyAB = EdgeKey(vi[a], vi[b]);
            long edgeKeyAC = EdgeKey(vi[a], vi[c]);

            int pIAB = GetOrAddEdgeVertex(posVerts, posEdgeVertMap, edgeKeyAB, intAB);
            int pIAC = GetOrAddEdgeVertex(posVerts, posEdgeVertMap, edgeKeyAC, intAC);
            posTris.Add(posA); posTris.Add(pIAB); posTris.Add(pIAC);
            borderEdgesPos.Add(pIAC); borderEdgesPos.Add(pIAB);

            int nB = GetOrAddVertex(negVerts, negMap, origVerts, vi[b]);
            int nC = GetOrAddVertex(negVerts, negMap, origVerts, vi[c]);
            int nIAB = GetOrAddEdgeVertex(negVerts, negEdgeVertMap, edgeKeyAB, intAB);
            int nIAC = GetOrAddEdgeVertex(negVerts, negEdgeVertMap, edgeKeyAC, intAC);
            negTris.Add(nIAB); negTris.Add(nB); negTris.Add(nIAC);
            negTris.Add(nIAC); negTris.Add(nB); negTris.Add(nC);
        }

        // One vertex on negative side (vi[lone]), two on positive/plane
        static void SplitOneNegTwo(Vec3d[] origVerts, CoACDPlane plane,
            int[] vi, int[] si, int lone,
            List<Vec3d> posVerts, List<int> posTris, Dictionary<int, int> posMap,
            Dictionary<long, int> posEdgeVertMap,
            List<Vec3d> negVerts, List<int> negTris, Dictionary<int, int> negMap,
            Dictionary<long, int> negEdgeVertMap,
            List<int> borderEdgesPos, Dictionary<long, Vec3d> edgeCache)
        {
            int a = lone;
            int b = (lone + 1) % 3;
            int c = (lone + 2) % 3;

            int negA = GetOrAddVertex(negVerts, negMap, origVerts, vi[a]);

            if (si[b] == 0 && si[c] == 0)
            {
                int negB = GetOrAddVertex(negVerts, negMap, origVerts, vi[b]);
                int negC = GetOrAddVertex(negVerts, negMap, origVerts, vi[c]);
                negTris.Add(negA); negTris.Add(negB); negTris.Add(negC);
                int posB = GetOrAddVertex(posVerts, posMap, origVerts, vi[b]);
                int posC = GetOrAddVertex(posVerts, posMap, origVerts, vi[c]);
                borderEdgesPos.Add(posB); borderEdgesPos.Add(posC);
                return;
            }

            if (si[b] == 0)
            {
                Vec3d iAC = GetEdgeIntersection(origVerts, plane, vi[a], vi[c], edgeCache);
                long keyAC = EdgeKey(vi[a], vi[c]);
                int negB = GetOrAddVertex(negVerts, negMap, origVerts, vi[b]);
                int negIAC = GetOrAddEdgeVertex(negVerts, negEdgeVertMap, keyAC, iAC);
                negTris.Add(negA); negTris.Add(negB); negTris.Add(negIAC);

                int posB = GetOrAddVertex(posVerts, posMap, origVerts, vi[b]);
                int posC = GetOrAddVertex(posVerts, posMap, origVerts, vi[c]);
                int posIAC = GetOrAddEdgeVertex(posVerts, posEdgeVertMap, keyAC, iAC);
                posTris.Add(posB); posTris.Add(posC); posTris.Add(posIAC);
                borderEdgesPos.Add(posB); borderEdgesPos.Add(posIAC);
                return;
            }

            if (si[c] == 0)
            {
                Vec3d iAB = GetEdgeIntersection(origVerts, plane, vi[a], vi[b], edgeCache);
                long keyAB = EdgeKey(vi[a], vi[b]);
                int negC = GetOrAddVertex(negVerts, negMap, origVerts, vi[c]);
                int negIAB = GetOrAddEdgeVertex(negVerts, negEdgeVertMap, keyAB, iAB);
                negTris.Add(negA); negTris.Add(negIAB); negTris.Add(negC);

                int posB = GetOrAddVertex(posVerts, posMap, origVerts, vi[b]);
                int posC = GetOrAddVertex(posVerts, posMap, origVerts, vi[c]);
                int posIAB = GetOrAddEdgeVertex(posVerts, posEdgeVertMap, keyAB, iAB);
                posTris.Add(posIAB); posTris.Add(posB); posTris.Add(posC);
                borderEdgesPos.Add(posIAB); borderEdgesPos.Add(posC);
                return;
            }

            // Both b and c are positive: split edges a-b and a-c
            Vec3d intAB = GetEdgeIntersection(origVerts, plane, vi[a], vi[b], edgeCache);
            Vec3d intAC = GetEdgeIntersection(origVerts, plane, vi[a], vi[c], edgeCache);
            long edgeKeyAB = EdgeKey(vi[a], vi[b]);
            long edgeKeyAC = EdgeKey(vi[a], vi[c]);

            int nIAB = GetOrAddEdgeVertex(negVerts, negEdgeVertMap, edgeKeyAB, intAB);
            int nIAC = GetOrAddEdgeVertex(negVerts, negEdgeVertMap, edgeKeyAC, intAC);
            negTris.Add(negA); negTris.Add(nIAB); negTris.Add(nIAC);

            int pB = GetOrAddVertex(posVerts, posMap, origVerts, vi[b]);
            int pC = GetOrAddVertex(posVerts, posMap, origVerts, vi[c]);
            int pIAB = GetOrAddEdgeVertex(posVerts, posEdgeVertMap, edgeKeyAB, intAB);
            int pIAC = GetOrAddEdgeVertex(posVerts, posEdgeVertMap, edgeKeyAC, intAC);
            posTris.Add(pIAB); posTris.Add(pB); posTris.Add(pIAC);
            posTris.Add(pIAC); posTris.Add(pB); posTris.Add(pC);
            borderEdgesPos.Add(pIAB); borderEdgesPos.Add(pIAC);
        }

        /// <summary>
        /// Triangulate the cut face and add triangles to both halves.
        /// Uses ear-clipping on the 2D projection of border points.
        /// </summary>
        static void TriangulateCutFace(List<Vec3d> posVerts, List<int> posTris,
            List<Vec3d> negVerts, List<int> negTris,
            List<int> borderEdgesPos, CoACDPlane plane)
        {
            // Dictionary for O(1) mirror vertex lookup (replaces O(n) linear search)
            var mirrorVertMap = new Dictionary<long, int>();
            // Build loops from directed border edges
            // borderEdgesPos stores pairs: [from0, to0, from1, to1, ...]
            if (borderEdgesPos.Count < 4) return;

            var edgeMap = new Dictionary<int, int>(); // from → to in posVerts indices
            for (int i = 0; i < borderEdgesPos.Count; i += 2)
            {
                int from = borderEdgesPos[i];
                int to = borderEdgesPos[i + 1];
                edgeMap[from] = to;
            }

            // Extract loops
            var usedEdges = new HashSet<int>();
            var loops = new List<List<int>>();

            foreach (var kvp in edgeMap)
            {
                if (usedEdges.Contains(kvp.Key)) continue;

                var loop = new List<int>();
                int current = kvp.Key;
                int safety = posVerts.Count + 1;
                while (!usedEdges.Contains(current) && safety-- > 0)
                {
                    usedEdges.Add(current);
                    loop.Add(current);
                    int next;
                    if (!edgeMap.TryGetValue(current, out next)) break;
                    current = next;
                }
                if (loop.Count >= 3)
                    loops.Add(loop);
            }

            // Project to 2D and ear-clip each loop
            Vec3d planeNormal = plane.Normal.Normalized();

            // Build a 2D coordinate system on the plane
            Vec3d u, v;
            BuildPlaneCoords(planeNormal, out u, out v);

            foreach (var loop in loops)
            {
                // Project loop vertices to 2D
                var pts2d = new double[loop.Count * 2];
                for (int i = 0; i < loop.Count; i++)
                {
                    Vec3d p = posVerts[loop[i]];
                    pts2d[i * 2] = Vec3d.Dot(p, u);
                    pts2d[i * 2 + 1] = Vec3d.Dot(p, v);
                }

                // Ensure CCW winding
                double area = 0;
                for (int i = 0; i < loop.Count; i++)
                {
                    int j = (i + 1) % loop.Count;
                    area += pts2d[i * 2] * pts2d[j * 2 + 1] - pts2d[j * 2] * pts2d[i * 2 + 1];
                }
                if (area < 0)
                {
                    loop.Reverse();
                    for (int i = 0; i < loop.Count; i++)
                    {
                        Vec3d p = posVerts[loop[i]];
                        pts2d[i * 2] = Vec3d.Dot(p, u);
                        pts2d[i * 2 + 1] = Vec3d.Dot(p, v);
                    }
                }

                // Ear clipping
                var indices = new List<int>(loop.Count);
                for (int i = 0; i < loop.Count; i++) indices.Add(i);

                int attempts = 0;
                int maxAttempts = indices.Count * indices.Count;
                while (indices.Count > 2 && attempts < maxAttempts)
                {
                    bool earFound = false;
                    for (int i = 0; i < indices.Count; i++)
                    {
                        int prev = (i + indices.Count - 1) % indices.Count;
                        int next = (i + 1) % indices.Count;

                        int pi = indices[prev], ci = indices[i], ni = indices[next];

                        // Check if this is a convex vertex (ear tip)
                        double cross = Cross2D(
                            pts2d[pi * 2], pts2d[pi * 2 + 1],
                            pts2d[ci * 2], pts2d[ci * 2 + 1],
                            pts2d[ni * 2], pts2d[ni * 2 + 1]);

                        if (cross <= 1e-10)
                        {
                            attempts++;
                            continue;
                        }

                        // Check no other vertex is inside this triangle
                        bool isEar = true;
                        for (int j = 0; j < indices.Count; j++)
                        {
                            if (j == prev || j == i || j == next) continue;
                            int ti = indices[j];
                            if (PointInTriangle2D(
                                pts2d[ti * 2], pts2d[ti * 2 + 1],
                                pts2d[pi * 2], pts2d[pi * 2 + 1],
                                pts2d[ci * 2], pts2d[ci * 2 + 1],
                                pts2d[ni * 2], pts2d[ni * 2 + 1]))
                            {
                                isEar = false;
                                break;
                            }
                        }

                        if (isEar)
                        {
                            // Add triangle to positive side
                            posTris.Add(loop[pi]);
                            posTris.Add(loop[ci]);
                            posTris.Add(loop[ni]);

                            // Add triangle to negative side (reversed winding)
                            int negPI = FindOrAddMirrorVertex(negVerts, posVerts[loop[pi]], mirrorVertMap);
                            int negCI = FindOrAddMirrorVertex(negVerts, posVerts[loop[ci]], mirrorVertMap);
                            int negNI = FindOrAddMirrorVertex(negVerts, posVerts[loop[ni]], mirrorVertMap);
                            negTris.Add(negPI);
                            negTris.Add(negNI);
                            negTris.Add(negCI);

                            indices.RemoveAt(i);
                            earFound = true;
                            attempts = 0;
                            break;
                        }
                        attempts++;
                    }
                    if (!earFound) break;
                }
            }
        }

        static long MirrorVertKey(Vec3d pos)
        {
            // Quantize to ~1e-6 precision for stable hashing
            long x = (long)Math.Round(pos.x * 1e6);
            long y = (long)Math.Round(pos.y * 1e6);
            long z = (long)Math.Round(pos.z * 1e6);
            return x * 73856093L ^ y * 19349669L ^ z * 83492791L;
        }

        static int FindOrAddMirrorVertex(List<Vec3d> verts, Vec3d pos, Dictionary<long, int> map)
        {
            long key = MirrorVertKey(pos);
            int idx;
            if (map.TryGetValue(key, out idx))
                return idx;
            idx = verts.Count;
            verts.Add(pos);
            map[key] = idx;
            return idx;
        }

        static void BuildPlaneCoords(Vec3d normal, out Vec3d u, out Vec3d v)
        {
            // Pick a vector not parallel to normal
            Vec3d up = Math.Abs(normal.y) < 0.9 ? new Vec3d(0, 1, 0) : new Vec3d(1, 0, 0);
            u = Vec3d.Cross(normal, up).Normalized();
            v = Vec3d.Cross(normal, u).Normalized();
        }

        static double Cross2D(double ax, double ay, double bx, double by, double cx, double cy)
        {
            return (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        }

        static bool PointInTriangle2D(double px, double py,
            double ax, double ay, double bx, double by, double cx, double cy)
        {
            double d1 = Cross2D(ax, ay, bx, by, px, py);
            double d2 = Cross2D(bx, by, cx, cy, px, py);
            double d3 = Cross2D(cx, cy, ax, ay, px, py);
            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }
    }
}
