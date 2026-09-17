using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Random = System.Random;

namespace UColliders.CoACD
{
    /// <summary>
    /// Pure C# implementation of the CoACD (Collision-Aware Convex Decomposition) algorithm.
    /// Based on "Approximate Convex Decomposition for 3D Meshes with Collision-Aware Concavity
    /// and Tree Search" (Wei et al., SIGGRAPH 2022).
    /// </summary>
    internal static class CoACDEngine
    {
        /// <summary>
        /// Progress callback: (message, progress 0-1).
        /// Set by the editor before calling Run() to display a progress bar.
        /// </summary>
        public static Action<string, float> OnProgress;

        /// <summary>
        /// Set to <c>true</c> to cancel the current decomposition.
        /// Checked between iterations; throws <see cref="OperationCanceledException"/>.
        /// </summary>
        public static volatile bool Cancelled;

        static void ReportProgress(string message, float progress)
        {
            OnProgress?.Invoke(message, progress);
        }

        /// <summary>
        /// Run CoACD decomposition on a Unity Mesh and return a list of convex hull meshes.
        /// </summary>
        /// <param name="mesh">Input mesh to decompose.</param>
        /// <param name="parameters">CoACD algorithm parameters.</param>
        /// <returns>List of convex hull meshes.</returns>
        public static List<Mesh> Run(Mesh mesh, CoACDParameters parameters)
        {
            CoACDProfiler.Enabled = OnProgress != null;
            CoACDProfiler.Reset();

            // Convert Unity mesh to internal representation
            Vector3[] unityVerts = mesh.vertices;
            int[] unityTris = mesh.triangles;

            ReportProgress("Converting mesh...", 0f);

            var verts = new Vec3d[unityVerts.Length];
            for (int i = 0; i < unityVerts.Length; i++)
                verts[i] = new Vec3d(unityVerts[i].x, unityVerts[i].y, unityVerts[i].z);

            var inputMesh = new CoACDMesh(verts, unityTris);

            // Manifold preprocessing (voxel-based mesh repair)
            // Only run when explicitly triggered (OnProgress is set by the editor button).
            // Skipped during automatic Start()/Awake() calls to avoid freezing the editor.
            if (OnProgress != null && parameters.preprocessMode != CoACDPreprocessMode.Off)
            {
                bool needsRepair = parameters.preprocessMode == CoACDPreprocessMode.On
                    || !IsLikelyManifold(inputMesh);
                if (needsRepair)
                {
                    ReportProgress("Repairing mesh...", 0.01f);
                    inputMesh = MeshRepair.Repair(inputMesh, parameters.preprocessResolution,
                        (msg, p) => ReportProgress(msg, 0.01f + p * 0.04f));
                }
            }

            // Run decomposition
            List<CoACDMesh> hulls = Compute(inputMesh, parameters);

            ReportProgress("Creating Unity meshes...", 0.95f);

            // Convert back to Unity meshes, skipping degenerate hulls
            var result = new List<Mesh>(hulls.Count);
            int hullIdx = 0;
            for (int i = 0; i < hulls.Count; i++)
            {
                var hull = hulls[i];
                // Skip hulls that are too thin for PhysX (coplanar or near-zero volume)
                if (hull.vertices.Length < 4 || hull.triangles.Length < 12)
                    continue;
                double vol = Math.Abs(hull.ComputeVolume());
                if (vol < 1e-10)
                    continue;
                // Skip hulls whose bounding box is degenerate in any axis
                if (hull.bbox != null)
                {
                    double dx = hull.bbox[1] - hull.bbox[0];
                    double dy = hull.bbox[3] - hull.bbox[2];
                    double dz = hull.bbox[5] - hull.bbox[4];
                    if (dx < 1e-6 || dy < 1e-6 || dz < 1e-6)
                        continue;
                }

                var hullVerts = hull.vertices;
                var hullTris = hull.triangles;

                Mesh convexHull = new Mesh();
                var uVerts = new Vector3[hullVerts.Length];
                for (int j = 0; j < hullVerts.Length; j++)
                    uVerts[j] = new Vector3((float)hullVerts[j].x, (float)hullVerts[j].y, (float)hullVerts[j].z);

                convexHull.SetVertices(uVerts);
                convexHull.SetTriangles(hullTris, 0);
                convexHull.RecalculateNormals();
                convexHull.RecalculateBounds();
                convexHull.name = "CoACD_Hull_" + hullIdx++;
                result.Add(convexHull);
            }

            if (CoACDProfiler.Enabled)
                UnityEngine.Debug.Log(CoACDProfiler.GetReport());

            ReportProgress("Done", 1f);
            return result;
        }

        /// <summary>
        /// Core decomposition pipeline.
        /// </summary>
        static List<CoACDMesh> Compute(CoACDMesh inputMesh, CoACDParameters parameters)
        {
            double threshold = parameters.threshold;
            int mctsNodes = parameters.mctsNodes;
            int mctsIterations = parameters.mctsIteration;
            int mctsMaxDepth = parameters.mctsMaxDepth;
            int sampleResolution = parameters.sampleResolution;
            double rvK = 0.3;
            uint seed = parameters.seed > 0 ? parameters.seed : 1234;

            // 1. Normalize mesh to [-1, 1]
            ReportProgress("Normalizing mesh...", 0.02f);
            var tNorm = CoACDProfiler.Begin();
            Vec3d center;
            double scale;
            inputMesh.Normalize(out center, out scale);
            CoACDProfiler.End("Normalize", tNorm);

            // 2. Check volume orientation — if negative, reverse winding
            double vol = inputMesh.ComputeVolume();
            if (vol < 0)
            {
                for (int t = 0; t < inputMesh.triangles.Length; t += 3)
                {
                    int tmp = inputMesh.triangles[t + 1];
                    inputMesh.triangles[t + 1] = inputMesh.triangles[t + 2];
                    inputMesh.triangles[t + 2] = tmp;
                }
            }

            // 3. BFS decomposition loop
            var tBFS = CoACDProfiler.Begin();
            var inputParts = new List<CoACDMesh> { inputMesh };
            var finalParts = new List<CoACDMesh>();

            int iteration = 0;
            int maxIterations = 100;
            while (inputParts.Count > 0 && iteration < maxIterations)
            {
                if (Cancelled)
                    throw new OperationCanceledException("CoACD decomposition cancelled by user.");

                float progress = 0.05f + 0.80f * (1f - 1f / (1f + finalParts.Count));
                ReportProgress(
                    $"Decomposing: {inputParts.Count} parts to split, {finalParts.Count} done...",
                    progress);

                var nextParts = new List<CoACDMesh>();
                var partLock = new object();

                if (inputParts.Count >= 4)
                {
                    Parallel.For(0, inputParts.Count, i =>
                    {
                        var rng = new Random((int)(seed + (uint)i * 7919));
                        ProcessPart(inputParts[i], threshold, mctsNodes, mctsIterations,
                            mctsMaxDepth, sampleResolution, rvK, rng,
                            nextParts, finalParts, partLock);
                    });
                }
                else
                {
                    for (int i = 0; i < inputParts.Count; i++)
                    {
                        var rng = new Random((int)(seed + (uint)i * 7919));
                        ProcessPart(inputParts[i], threshold, mctsNodes, mctsIterations,
                            mctsMaxDepth, sampleResolution, rvK, rng,
                            nextParts, finalParts, partLock);
                    }
                }

                inputParts = nextParts;
                iteration++;
            }

            CoACDProfiler.End("BFS decomposition", tBFS);

            // Any remaining parts that couldn't be split further
            for (int i = 0; i < inputParts.Count; i++)
                finalParts.Add(inputParts[i].GetConvexHull());

            // 4. Merge if enabled
            if (parameters.merge && finalParts.Count > 1)
            {
                ReportProgress($"Merging {finalParts.Count} hulls...", 0.88f);
                var tMerge = CoACDProfiler.Begin();
                finalParts = HullMerger.Merge(finalParts, threshold,
                    parameters.maxConvexHull, sampleResolution, rvK);
                CoACDProfiler.End("HullMerger.Merge", tMerge);
            }

            // 5. Recover original coordinates
            ReportProgress("Recovering coordinates...", 0.93f);
            for (int i = 0; i < finalParts.Count; i++)
                finalParts[i].Recover(center, scale);

            return finalParts;
        }

        /// <summary>
        /// Quick heuristic to check if a mesh is likely 2-manifold.
        /// Checks that every geometric edge is shared by exactly 2 triangles.
        /// Uses position-based vertex deduplication to handle split vertices
        /// at UV seams and hard edges (common in Unity meshes).
        /// </summary>
        static bool IsLikelyManifold(CoACDMesh mesh)
        {
            Vec3d[] verts = mesh.vertices;
            int[] tris = mesh.triangles;

            // 1. Build a position-based vertex map (snap to grid for dedup)
            // Use a scale factor to create integer keys from positions
            double scale = 1e6;
            var posToId = new Dictionary<long, int>();
            var canonId = new int[verts.Length];
            int nextId = 0;

            for (int i = 0; i < verts.Length; i++)
            {
                long key = PositionKey(verts[i], scale);
                int id;
                if (!posToId.TryGetValue(key, out id))
                {
                    id = nextId++;
                    posToId[key] = id;
                }
                canonId[i] = id;
            }

            // 2. Count triangles per canonical edge
            var edgeCounts = new Dictionary<long, int>(tris.Length);
            for (int t = 0; t < tris.Length; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = canonId[tris[t + e]];
                    int b = canonId[tris[t + (e + 1) % 3]];
                    if (a == b) continue; // degenerate edge
                    long edgeKey = a < b
                        ? (long)a * nextId + b
                        : (long)b * nextId + a;
                    int count;
                    edgeCounts.TryGetValue(edgeKey, out count);
                    edgeCounts[edgeKey] = count + 1;
                }
            }

            // For a closed 2-manifold, every edge has exactly 2 incident triangles
            foreach (var kv in edgeCounts)
            {
                if (kv.Value != 2)
                    return false;
            }
            return edgeCounts.Count > 0;
        }

        static long PositionKey(Vec3d v, double scale)
        {
            // Pack quantized position into a long
            // Use Cantor-style combination for 3 ints
            long x = (long)Math.Round(v.x * scale);
            long y = (long)Math.Round(v.y * scale);
            long z = (long)Math.Round(v.z * scale);
            // Combine with large primes to minimize collisions
            return x * 73856093L ^ y * 19349669L ^ z * 83492791L;
        }

        static void ProcessPart(CoACDMesh part, double threshold, int mctsNodes,
            int mctsIterations, int mctsMaxDepth, int sampleResolution, double rvK,
            Random rng, List<CoACDMesh> nextParts, List<CoACDMesh> finalParts, object lockObj)
        {
            var tHull = CoACDProfiler.Begin();
            var hull = part.GetConvexHull();
            CoACDProfiler.End("ConvexHull", tHull);

            var tHCost = CoACDProfiler.Begin();
            double hCost = ConcavityComputer.ComputeHCost(part, hull, sampleResolution, rvK);
            CoACDProfiler.End("ComputeHCost", tHCost);

            if (hCost <= threshold)
            {
                lock (lockObj) { finalParts.Add(hull); }
                return;
            }

            if (Cancelled)
                throw new OperationCanceledException("CoACD decomposition cancelled by user.");

            var tMCTS = CoACDProfiler.Begin();
            var mcts = new MCTSearch(part, threshold, mctsNodes, mctsIterations,
                mctsMaxDepth, rvK, rng);

            CoACDPlane bestPlane;
            CoACDMesh posMesh, negMesh;
            if (mcts.Search(out bestPlane, out posMesh, out negMesh))
            {
                lock (lockObj)
                {
                    nextParts.Add(posMesh);
                    nextParts.Add(negMesh);
                }
            }
            else
            {
                lock (lockObj) { finalParts.Add(hull); }
            }
            CoACDProfiler.End("MCTS.Search", tMCTS);
        }
    }
}
