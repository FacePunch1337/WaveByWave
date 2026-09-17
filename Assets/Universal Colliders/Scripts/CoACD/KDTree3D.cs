using System;
using System.Collections.Generic;

namespace UColliders.CoACD
{
    /// <summary>
    /// Simple 3D KD-tree for nearest-neighbor queries used in Hausdorff distance computation.
    /// </summary>
    internal class KDTree3D
    {
        int[] _indices;
        Vec3d[] _points;
        int _count;

        // Tree stored as arrays for cache locality
        int[] _splitAxis;
        int[] _splitIndex; // index into _indices
        int[] _left;
        int[] _right;
        int _nodeCount;

        // Reusable priority queue for KNN
        struct KNNEntry : IComparable<KNNEntry>
        {
            public double dist;
            public int index;
            public int CompareTo(KNNEntry other) => other.dist.CompareTo(dist); // max-heap
        }

        KDTree3D() { }

        /// <summary>
        /// Build a KD-tree from an array of 3D points.
        /// </summary>
        public static KDTree3D Build(Vec3d[] points)
        {
            var tree = new KDTree3D();
            tree._points = points;
            tree._count = points.Length;
            tree._indices = new int[points.Length];
            for (int i = 0; i < points.Length; i++)
                tree._indices[i] = i;

            int maxNodes = points.Length * 2 + 1;
            tree._splitAxis = new int[maxNodes];
            tree._splitIndex = new int[maxNodes];
            tree._left = new int[maxNodes];
            tree._right = new int[maxNodes];
            tree._nodeCount = 0;

            if (points.Length > 0)
                tree.BuildNode(0, points.Length, 0);

            return tree;
        }

        int BuildNode(int lo, int hi, int depth)
        {
            if (lo >= hi) return -1;

            int nodeId = _nodeCount++;
            int axis = depth % 3;
            _splitAxis[nodeId] = axis;

            if (hi - lo == 1)
            {
                _splitIndex[nodeId] = lo;
                _left[nodeId] = -1;
                _right[nodeId] = -1;
                return nodeId;
            }

            // Partition around median using nth_element equivalent
            int mid = (lo + hi) / 2;
            NthElement(lo, hi, mid, axis);

            _splitIndex[nodeId] = mid;
            _left[nodeId] = BuildNode(lo, mid, depth + 1);
            _right[nodeId] = BuildNode(mid + 1, hi, depth + 1);
            return nodeId;
        }

        void NthElement(int lo, int hi, int nth, int axis)
        {
            // Quickselect
            while (lo < hi - 1)
            {
                int pivotIdx = lo + (hi - lo) / 2;
                double pivotVal = _points[_indices[pivotIdx]][axis];

                // Move pivot to end
                Swap(pivotIdx, hi - 1);
                int store = lo;
                for (int i = lo; i < hi - 1; i++)
                {
                    if (_points[_indices[i]][axis] < pivotVal)
                    {
                        Swap(i, store);
                        store++;
                    }
                }
                Swap(store, hi - 1);

                if (store == nth) return;
                if (store < nth) lo = store + 1;
                else hi = store;
            }
        }

        void Swap(int a, int b)
        {
            int tmp = _indices[a];
            _indices[a] = _indices[b];
            _indices[b] = tmp;
        }

        /// <summary>
        /// Find the single nearest neighbor to the query point.
        /// </summary>
        public int NearestNeighbor(Vec3d query, out double bestDist)
        {
            bestDist = double.MaxValue;
            int bestIdx = -1;
            NNSearch(0, query, ref bestDist, ref bestIdx);
            bestDist = Math.Sqrt(bestDist);
            return bestIdx;
        }

        void NNSearch(int nodeId, Vec3d query, ref double bestSqrDist, ref int bestIdx)
        {
            if (nodeId < 0) return;

            int ptIdx = _indices[_splitIndex[nodeId]];
            double sqrDist = Vec3d.SqrDistance(query, _points[ptIdx]);
            if (sqrDist < bestSqrDist)
            {
                bestSqrDist = sqrDist;
                bestIdx = ptIdx;
            }

            int axis = _splitAxis[nodeId];
            double diff = query[axis] - _points[ptIdx][axis];
            double diff2 = diff * diff;

            int first = diff < 0 ? _left[nodeId] : _right[nodeId];
            int second = diff < 0 ? _right[nodeId] : _left[nodeId];

            NNSearch(first, query, ref bestSqrDist, ref bestIdx);
            if (diff2 < bestSqrDist)
                NNSearch(second, query, ref bestSqrDist, ref bestIdx);
        }

        /// <summary>
        /// Find k nearest neighbors to the query point.
        /// Returns indices into the original points array and their distances.
        /// </summary>
        public void KNearest(Vec3d query, int k, List<int> resultIndices, List<double> resultDists)
        {
            resultIndices.Clear();
            resultDists.Clear();

            if (_count == 0) return;
            k = Math.Min(k, _count);

            // Max-heap of (distance, index)
            var heap = new List<KNNEntry>(k + 1);
            double worstDist = double.MaxValue;
            KNNSearch(0, query, k, heap, ref worstDist);

            // Sort by distance ascending
            heap.Sort((a, b) => a.dist.CompareTo(b.dist));
            for (int i = 0; i < heap.Count; i++)
            {
                resultIndices.Add(heap[i].index);
                resultDists.Add(Math.Sqrt(heap[i].dist));
            }
        }

        void KNNSearch(int nodeId, Vec3d query, int k, List<KNNEntry> heap, ref double worstSqrDist)
        {
            if (nodeId < 0) return;

            int ptIdx = _indices[_splitIndex[nodeId]];
            double sqrDist = Vec3d.SqrDistance(query, _points[ptIdx]);

            if (heap.Count < k)
            {
                heap.Add(new KNNEntry { dist = sqrDist, index = ptIdx });
                if (heap.Count == k)
                {
                    // Find worst
                    worstSqrDist = 0;
                    for (int i = 0; i < heap.Count; i++)
                        if (heap[i].dist > worstSqrDist) worstSqrDist = heap[i].dist;
                }
            }
            else if (sqrDist < worstSqrDist)
            {
                // Replace worst
                int worstIdx = 0;
                for (int i = 1; i < heap.Count; i++)
                    if (heap[i].dist > heap[worstIdx].dist) worstIdx = i;
                heap[worstIdx] = new KNNEntry { dist = sqrDist, index = ptIdx };
                worstSqrDist = 0;
                for (int i = 0; i < heap.Count; i++)
                    if (heap[i].dist > worstSqrDist) worstSqrDist = heap[i].dist;
            }

            int axis = _splitAxis[nodeId];
            double diff = query[axis] - _points[ptIdx][axis];
            double diff2 = diff * diff;

            int first = diff < 0 ? _left[nodeId] : _right[nodeId];
            int second = diff < 0 ? _right[nodeId] : _left[nodeId];

            KNNSearch(first, query, k, heap, ref worstSqrDist);
            if (heap.Count < k || diff2 < worstSqrDist)
                KNNSearch(second, query, k, heap, ref worstSqrDist);
        }
    }
}
