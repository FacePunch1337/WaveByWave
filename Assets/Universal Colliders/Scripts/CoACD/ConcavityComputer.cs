using System;
using System.Collections.Generic;

namespace UColliders.CoACD
{
    /// <summary>
    /// Computes collision-aware concavity metrics (Rv, Hb, H) as defined in the CoACD paper.
    /// </summary>
    internal static class ConcavityComputer
    {
        const double FourThirdsPi = 4.0 / 3.0 * Math.PI;
        const double ThreeOverFourPi = 3.0 / (4.0 * Math.PI);

        /// <summary>
        /// Volume-based concavity Rv: converts volume difference between mesh and its
        /// convex hull into an equivalent sphere radius.
        /// Rv(M, CH) = k * (3|V_M - V_CH| / (4*pi))^(1/3)
        /// </summary>
        public static double ComputeRv(double meshVolume, double hullVolume, double k = 0.3)
        {
            double diff = Math.Abs(meshVolume - hullVolume);
            return k * Math.Pow(ThreeOverFourPi * diff, 1.0 / 3.0);
        }

        /// <summary>
        /// Merge Rv: concavity introduced by merging two convex hulls.
        /// Rv = k * (3|V1 + V2 - V_merged| / (4*pi))^(1/3)
        /// </summary>
        public static double ComputeMergeRv(double vol1, double vol2, double mergedVolume, double k = 0.3)
        {
            double diff = Math.Abs(vol1 + vol2 - mergedVolume);
            return k * Math.Pow(ThreeOverFourPi * diff, 1.0 / 3.0);
        }

        /// <summary>
        /// Bidirectional Hausdorff distance between two point sets, accelerated by KD-tree.
        /// Uses triangle-based distance refinement for accuracy.
        /// </summary>
        public static double ComputeHausdorff(
            Vec3d[] meshPoints, CoACDMesh mesh,
            Vec3d[] hullPoints, CoACDMesh hull)
        {
            // Build KD-trees once and reuse for both directions
            var tKD = CoACDProfiler.Begin();
            var hullTree = KDTree3D.Build(hullPoints);
            var meshTree = KDTree3D.Build(meshPoints);
            CoACDProfiler.End("KDTree.Build", tKD);

            double forward = DirectedHausdorff(meshPoints, hullTree, hullPoints.Length);
            double backward = DirectedHausdorff(hullPoints, meshTree, meshPoints.Length);
            return Math.Max(forward, backward);
        }

        /// <summary>
        /// Directed Hausdorff distance: max over all query points of (min distance to target).
        /// Takes a pre-built KD-tree for the target points.
        /// </summary>
        static double DirectedHausdorff(Vec3d[] queryPoints, KDTree3D targetTree, int targetCount)
        {
            if (queryPoints.Length == 0 || targetCount == 0)
                return 0;

            double maxDist = 0;

            for (int i = 0; i < queryPoints.Length; i++)
            {
                double dist;
                targetTree.NearestNeighbor(queryPoints[i], out dist);
                if (dist > maxDist)
                    maxDist = dist;
            }

            return maxDist;
        }

        /// <summary>
        /// Full collision-aware concavity H = max(Rv, Hb).
        /// </summary>
        public static double ComputeHCost(CoACDMesh mesh, CoACDMesh hull, int sampleResolution, double rvK = 0.3)
        {
            var tHCost = CoACDProfiler.Begin();
            double meshVol = Math.Abs(mesh.ComputeVolume());
            double hullVol = Math.Abs(hull.ComputeVolume());
            double rv = ComputeRv(meshVol, hullVol, rvK);

            var rng = new Random(42);
            var tSample = CoACDProfiler.Begin();
            Vec3d[] meshPts = mesh.SamplePoints(sampleResolution, rng);
            Vec3d[] hullPts = hull.SamplePoints(sampleResolution, rng);
            CoACDProfiler.End("SamplePoints", tSample);

            var tHaus = CoACDProfiler.Begin();
            double hb = ComputeHausdorff(meshPts, mesh, hullPts, hull);
            CoACDProfiler.End("Hausdorff", tHaus);

            CoACDProfiler.End("ComputeHCost (full)", tHCost);
            return Math.Max(rv, hb);
        }

        /// <summary>
        /// Quick Rv-only cost (used during MCTS exploration, much cheaper than full HCost).
        /// </summary>
        public static double ComputeRvCost(CoACDMesh mesh, CoACDMesh hull, double rvK = 0.3)
        {
            double meshVol = Math.Abs(mesh.ComputeVolume());
            double hullVol = Math.Abs(hull.ComputeVolume());
            return ComputeRv(meshVol, hullVol, rvK);
        }

        /// <summary>
        /// Quick Rv cost directly from volumes.
        /// </summary>
        public static double ComputeRvFromVolumes(double meshVol, double hullVol, double rvK = 0.3)
        {
            return ComputeRv(Math.Abs(meshVol), Math.Abs(hullVol), rvK);
        }

        /// <summary>
        /// Compute minimum distance between two convex hulls using KD-tree.
        /// Used for the merge adjacency test.
        /// </summary>
        public static double MeshDistance(CoACDMesh hull1, CoACDMesh hull2)
        {
            if (hull1.vertices.Length == 0 || hull2.vertices.Length == 0)
                return double.MaxValue;

            var tree = KDTree3D.Build(hull2.vertices);
            double minDist = double.MaxValue;
            for (int i = 0; i < hull1.vertices.Length; i++)
            {
                double dist;
                tree.NearestNeighbor(hull1.vertices[i], out dist);
                if (dist < minDist)
                    minDist = dist;
            }
            return minDist;
        }
    }
}
