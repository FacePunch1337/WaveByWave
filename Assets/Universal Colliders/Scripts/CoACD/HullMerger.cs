using System;
using System.Collections.Generic;

namespace UColliders.CoACD
{
    /// <summary>
    /// Greedy pair-wise merging of convex hulls to reduce part count
    /// while staying within the concavity threshold.
    /// </summary>
    internal static class HullMerger
    {
        /// <summary>
        /// Merge convex hulls greedily. Two hulls are merged if their combined
        /// concavity stays below the threshold.
        /// </summary>
        /// <param name="hulls">List of convex hull meshes to merge.</param>
        /// <param name="threshold">Maximum allowed concavity for a merged pair.</param>
        /// <param name="maxConvexHull">Target number of hulls (-1 for no limit).</param>
        /// <param name="sampleResolution">Sampling resolution for Hausdorff distance.</param>
        /// <param name="rvK">Rv scaling factor.</param>
        /// <returns>Merged list of convex hull meshes.</returns>
        public static List<CoACDMesh> Merge(List<CoACDMesh> hulls, double threshold,
            int maxConvexHull, int sampleResolution, double rvK)
        {
            if (hulls.Count <= 1) return hulls;

            var parts = new List<CoACDMesh>(hulls);
            int n = parts.Count;

            // Use Dictionary-based sparse cost storage instead of n×n matrix
            // Key: (i << 16) | j where i < j
            var costs = new Dictionary<int, double>();
            var preCosts = new Dictionary<int, double>();

            // Build KD-trees once per hull for distance checks
            var trees = new KDTree3D[n];
            for (int i = 0; i < n; i++)
                trees[i] = KDTree3D.Build(parts[i].vertices);

            // Compute initial costs (upper triangle only)
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    double dist = MeshDistanceWithTree(parts[i], trees[j]);
                    if (dist < threshold)
                    {
                        int key = (i << 16) | j;
                        costs[key] = ComputeMergeCost(parts[i], parts[j], sampleResolution, rvK);
                        double ci = ConcavityComputer.ComputeRvCost(parts[i], parts[i], rvK);
                        double cj = ConcavityComputer.ComputeRvCost(parts[j], parts[j], rvK);
                        preCosts[key] = Math.Max(ci, cj);
                    }
                }
            }

            // Greedy merge loop
            while (parts.Count > 1)
            {
                // Find minimum cost pair from sparse map
                int bestKey = -1;
                double bestCost = double.MaxValue;

                foreach (var kv in costs)
                {
                    if (kv.Value < bestCost)
                    {
                        bestCost = kv.Value;
                        bestKey = kv.Key;
                    }
                }

                if (bestKey < 0) break;

                int bestI = bestKey >> 16;
                int bestJ = bestKey & 0xFFFF;

                // Check stopping criteria
                if (maxConvexHull <= 0)
                {
                    double preCost;
                    preCosts.TryGetValue(bestKey, out preCost);
                    double budget = Math.Max(threshold - preCost, 0.01);
                    if (bestCost > threshold && bestCost > budget)
                        break;
                }
                else
                {
                    if (parts.Count <= maxConvexHull)
                        break;
                }

                // Merge bestI and bestJ
                CoACDMesh merged = MergeTwo(parts[bestI], parts[bestJ]);

                // Remove bestJ (higher index), replace bestI
                parts.RemoveAt(bestJ);
                parts[bestI] = merged;
                n = parts.Count;

                // Rebuild trees array
                var newTrees = new KDTree3D[n];
                for (int i = 0; i < n; i++)
                {
                    if (i == bestI)
                        newTrees[i] = KDTree3D.Build(merged.vertices);
                    else
                    {
                        int oi = i < bestJ ? i : i + 1;
                        newTrees[i] = trees[oi];
                    }
                }
                trees = newTrees;

                // Rebuild cost maps with remapped indices
                var newCosts = new Dictionary<int, double>();
                var newPreCosts = new Dictionary<int, double>();

                foreach (var kv in costs)
                {
                    int oi = kv.Key >> 16;
                    int oj = kv.Key & 0xFFFF;

                    // Skip entries involving merged indices
                    if (oi == bestI || oj == bestI || oi == bestJ || oj == bestJ)
                        continue;

                    // Remap indices
                    int ni = oi < bestJ ? oi : oi - 1;
                    int nj = oj < bestJ ? oj : oj - 1;
                    int newKey = (ni << 16) | nj;
                    newCosts[newKey] = kv.Value;
                    double pc;
                    if (preCosts.TryGetValue(kv.Key, out pc))
                        newPreCosts[newKey] = pc;
                }

                // Compute costs for merged part vs all others
                for (int j = 0; j < n; j++)
                {
                    if (j == bestI) continue;
                    int i2 = Math.Min(bestI, j), j2 = Math.Max(bestI, j);

                    double dist = MeshDistanceWithTree(parts[i2], trees[j2]);
                    if (dist < threshold)
                    {
                        int key = (i2 << 16) | j2;
                        newCosts[key] = ComputeMergeCost(parts[i2], parts[j2], sampleResolution, rvK);
                        double ci = ConcavityComputer.ComputeRvCost(parts[i2], parts[i2], rvK);
                        double cj = ConcavityComputer.ComputeRvCost(parts[j2], parts[j2], rvK);
                        newPreCosts[key] = Math.Max(ci, cj);
                    }
                }

                costs = newCosts;
                preCosts = newPreCosts;
            }

            return parts;
        }

        /// <summary>
        /// Compute minimum distance from hull1 vertices to hull2 using a pre-built KD-tree.
        /// </summary>
        static double MeshDistanceWithTree(CoACDMesh hull1, KDTree3D tree2)
        {
            if (hull1.vertices.Length == 0) return double.MaxValue;
            double minDist = double.MaxValue;
            for (int i = 0; i < hull1.vertices.Length; i++)
            {
                double dist;
                tree2.NearestNeighbor(hull1.vertices[i], out dist);
                if (dist < minDist)
                    minDist = dist;
            }
            return minDist;
        }

        /// <summary>
        /// Merge two convex hulls by combining their vertices and computing a new convex hull.
        /// </summary>
        static CoACDMesh MergeTwo(CoACDMesh a, CoACDMesh b)
        {
            var combined = new Vec3d[a.vertices.Length + b.vertices.Length];
            Array.Copy(a.vertices, 0, combined, 0, a.vertices.Length);
            Array.Copy(b.vertices, 0, combined, a.vertices.Length, b.vertices.Length);
            return ConvexHull3D.ComputeHull(combined);
        }

        /// <summary>
        /// Compute the cost of merging two hulls using the full H metric.
        /// </summary>
        static double ComputeMergeCost(CoACDMesh hull1, CoACDMesh hull2,
            int sampleResolution, double rvK)
        {
            CoACDMesh merged = MergeTwo(hull1, hull2);
            double vol1 = Math.Abs(hull1.ComputeVolume());
            double vol2 = Math.Abs(hull2.ComputeVolume());
            double volMerged = Math.Abs(merged.ComputeVolume());
            return ConcavityComputer.ComputeMergeRv(vol1, vol2, volMerged, rvK);
        }
    }
}
