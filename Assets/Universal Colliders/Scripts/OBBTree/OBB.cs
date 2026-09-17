/** \file
    \brief Define an OBB, the base element for OBBTrees
*/
using System.Collections.Generic;
using UnityEngine;
using UColliders.CoACD;

namespace UColliders.OBBTree {

    /// <summary>
    /// Describes a clip vertex as a linear interpolation between two source vertices.
    /// Used to reconstruct clip vertices when the source mesh deforms (e.g. skinned meshes).
    /// </summary>
    public struct ClipVertexDef
    {
        /// <summary>First source vertex index.</summary>
        public int indexA;
        /// <summary>Second source vertex index.</summary>
        public int indexB;
        /// <summary>Interpolation factor: result = Lerp(vertices[indexA], vertices[indexB], t).</summary>
        public float t;
    }

    /// <summary>
    /// Representation of a single oriented bounding box.
    /// </summary>
    public struct OBB
    {
        /// <summary>
        /// Minimum number of triangle indices required per child for a valid subdivision.
        /// Children with fewer than this many indices are considered degenerate.
        /// </summary>
        public const int MinTriangleIndicesPerChild = 4;

        /// <summary>
        /// Rotation of this OBB relative to parent's space
        /// </summary>
        public Quaternion orientation;

        /// <summary>
        /// Bounds of this OBB.
        /// </summary>
        public Bounds bounds;

        /// <summary>
        /// Triangles in this OBB
        /// </summary>
        public int[] triangles;

        /// <summary>
        /// Indexes of long, medium, and short axes
        /// For instance {2, 0, 1} means is the longer extend, then x and y is shorter 
        /// </summary>
        public Vector3Int axesOrder;

        /// <summary>
        /// Area-weighted covariance matrix of triangle surfaces (Gottschalk 1996).
        /// Each triangle's contribution is weighted by its area, making the result
        /// independent of tessellation density.
        /// </summary>
        /// <param name="vertices">Vertices from the <c>UCollidersRoot</c> GameObject.</param>
        /// <param name="triangles">Triangle indices (length must be a multiple of 3).</param>
        /// <param name="bary">Area-weighted barycenter of the mesh.</param>
        /// <returns>The 3x3 covariance matrix.</returns>
        /// <see cref="Barycenter"/>
        public double[,] Covariance(Vector3[] vertices, int[] triangles, Vector3 bary)
        {
            var tCov = CoACDProfiler.Begin();
            if (vertices == null || vertices.Length == 0)
                throw new System.ArgumentException("Vertices array must not be null or empty.", nameof(vertices));
            if (triangles == null || triangles.Length == 0)
                throw new System.ArgumentException("Triangles array must not be null or empty.", nameof(triangles));

            double[,] covMatrix = new double[3, 3];
            double totalArea = 0.0;

            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vector3 p = vertices[triangles[t]];
                Vector3 q = vertices[triangles[t + 1]];
                Vector3 r = vertices[triangles[t + 2]];

                double area = Vector3.Cross(q - p, r - p).magnitude * 0.5;
                if (area < 1e-12)
                    continue;
                totalArea += area;

                Vector3 m = (p + q + r) / 3f;

                // Surface integral of (x - bary)(x - bary)^T over the triangle.
                // E[j*k] = (A/12) * (9*mj*mk + pj*pk + qj*qk + rj*rk)
                // Cov[j,k] = E[j*k] - bary_j * bary_k  (accumulated, divided by totalArea at the end)
                for (int j = 0; j < 3; j++)
                {
                    for (int k = j; k < 3; k++)
                    {
                        double secondMoment = (area / 12.0) *
                            (9.0 * (m[j] - bary[j]) * (m[k] - bary[k])
                            + (p[j] - bary[j]) * (p[k] - bary[k])
                            + (q[j] - bary[j]) * (q[k] - bary[k])
                            + (r[j] - bary[j]) * (r[k] - bary[k]));
                        covMatrix[j, k] += secondMoment;
                    }
                }
            }

            if (totalArea > 1e-12)
            {
                for (int j = 0; j < 3; j++)
                    for (int k = j; k < 3; k++)
                    {
                        covMatrix[j, k] /= totalArea;
                        covMatrix[k, j] = covMatrix[j, k];
                    }
            }

            CoACDProfiler.End("OBB.Covariance", tCov);
            return covMatrix;
        }

        /// <summary>
        /// Return directions of extremum variance, based on covariance matrix
        /// </summary>
        /// <param name="vertices">Vertices from the <c>UCollidersRoot</c> GameObject.</param>
        /// <param name="barycenter">Barycenter of the triangles.</param>
        /// <returns>An orthogonal base of three vectors.</returns>
        public Vector3[] GetMainAxes(Vector3[] vertices, Vector3 barycenter)
        {
            double[,] covMatrix = Covariance(vertices, this.triangles, barycenter);
            double[] eigenvalues;
            double[,] eigenVectors;
            SymmetricEigen3x3.Decompose(covMatrix, out eigenvalues, out eigenVectors);
            Vector3[] vectors = new Vector3[3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    vectors[i][j] = (float)eigenVectors[j, i];
            return vectors;
        }

        /// <summary>
        /// Area-weighted barycenter of the mesh surface.
        /// Each triangle's centroid is weighted by its area, making the result
        /// independent of tessellation density.
        /// </summary>
        /// <param name="vertices">Vertices from the <c>UCollidersRoot</c> GameObject.</param>
        /// <returns>Area-weighted centroid of the surface.</returns>
        public Vector3 Barycenter(Vector3[] vertices)
        {
            Vector3 output = Vector3.zero;
            float totalArea = 0f;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vector3 p = vertices[triangles[t]];
                Vector3 q = vertices[triangles[t + 1]];
                Vector3 r = vertices[triangles[t + 2]];
                float area = Vector3.Cross(q - p, r - p).magnitude * 0.5f;
                output += area * (p + q + r) / 3f;
                totalArea += area;
            }
            if (totalArea < 1e-12f)
                return output;
            return output / totalArea;
        }

        /// <summary>
        /// Sorts the axes of this OBB by decreasing length order
        /// </summary>
        /// <returns>Return the index of the longest, of the medium and of the shortest axis</returns>
        private Vector3Int ComputeAxesOrder()
        {
            Vector3 extents = this.bounds.extents;
            Vector3Int order = Vector3Int.one * -1;
            if (extents.x >= extents.y)
            {
                if (extents.x >= extents.z)
                {
                    order[0] = 0;
                    if (extents.y >= extents.z)
                    {
                        order[1] = 1;
                        order[2] = 2;
                    }
                    else
                    {
                        order[1] = 2;
                        order[2] = 1;
                    }
                }
                else
                {
                    order[0] = 2;
                    order[1] = 0;
                    order[2] = 1;
                }
            }
            else
            {
                if (extents.x >= extents.z)
                {
                    order[0] = 1;
                    order[1] = 0;
                    order[2] = 2;
                }
                else
                {
                    order[2] = 0;
                    if (extents.y >= extents.z)
                    {
                        order[0] = 1;
                        order[1] = 2;
                    }
                    else
                    {
                        order[0] = 2;
                        order[1] = 1;
                    }
                }
            }
            this.axesOrder = order;
            return order;
        }

        /// <summary>
        /// This method completely builds the OBB from a set of vertices. Triangles should have been provided.
        /// </summary>
        /// <param name="vertices">Vertices from the <c>UCollidersRoot</c> GameObject.</param>
        /// <returns>A new OBB.</returns>
        public OBB BuildOBB(Vector3[] vertices)
        {
            var tBuild = CoACDProfiler.Begin();
            if (vertices == null || vertices.Length == 0)
                throw new System.ArgumentException("Vertices array must not be null or empty.", nameof(vertices));
            if (triangles == null || triangles.Length == 0)
                throw new System.ArgumentException("Triangles array must not be null or empty.", nameof(triangles));
            Vector3 bary = Barycenter(vertices);
            Vector3[] vec = GetMainAxes(vertices, bary);
            this.orientation = Quaternion.LookRotation(vec[2], vec[1]);
            Quaternion invOrientation = Quaternion.Inverse(this.orientation);
            this.bounds = new Bounds(invOrientation * bary, Vector3.zero);
            foreach (int i in triangles)
                this.bounds.Encapsulate(invOrientation * vertices[i]);

            // Fall back to AABB when PCA orientation produces a worse fit.
            // This handles degenerate covariance at deep recursion levels where
            // accumulated triangle clipping creates thin slivers with unstable eigenvectors.
            Bounds aabb = new Bounds(vertices[triangles[0]], Vector3.zero);
            for (int i = 1; i < triangles.Length; i++)
                aabb.Encapsulate(vertices[triangles[i]]);

            Vector3 pcaSize = this.bounds.size;
            Vector3 aabbSize = aabb.size;
            float pcaVolume = pcaSize.x * pcaSize.y * pcaSize.z;
            float aabbVolume = aabbSize.x * aabbSize.y * aabbSize.z;
            if (aabbVolume < pcaVolume)
            {
                this.orientation = Quaternion.identity;
                this.bounds = aabb;
            }

            this.axesOrder = ComputeAxesOrder();
            CoACDProfiler.End("OBB.BuildOBB", tBuild);
            return this;
        }

        /// <summary>
        /// Refit this OBB to new vertex positions without recomputing eigenvectors.
        /// Keeps the existing orientation and triangle assignment, only updates bounds.
        /// This avoids eigenvector instability when vertices change slightly between frames.
        /// </summary>
        /// <param name="vertices">Updated vertices from the <c>UCollidersRoot</c> GameObject.</param>
        /// <returns>The refitted OBB.</returns>
        public OBB RefitOBB(Vector3[] vertices)
        {
            var tRefit = CoACDProfiler.Begin();
            if (vertices == null || vertices.Length == 0)
                throw new System.ArgumentException("Vertices array must not be null or empty.", nameof(vertices));
            if (triangles == null || triangles.Length == 0)
                throw new System.ArgumentException("Triangles array must not be null or empty.", nameof(triangles));
            Quaternion invOrientation = Quaternion.Inverse(this.orientation);
            Vector3 first = invOrientation * vertices[triangles[0]];
            this.bounds = new Bounds(first, Vector3.zero);
            for (int i = 1; i < triangles.Length; i++)
                this.bounds.Encapsulate(invOrientation * vertices[triangles[i]]);
            this.axesOrder = ComputeAxesOrder();
            CoACDProfiler.End("OBB.RefitOBB", tRefit);
            return this;
        }

        /// <summary>
        /// Refit this OBB's bounds by merging two children's bounds.
        /// Both children's bounds are in their own OBB-local space, so we transform
        /// their corner points into this OBB's local space and encapsulate.
        /// Much faster than re-iterating all vertices for non-leaf nodes.
        /// </summary>
        /// <param name="childA">First child OBB (already refitted).</param>
        /// <param name="childB">Second child OBB (already refitted).</param>
        /// <returns>The refitted OBB.</returns>
        public OBB RefitFromChildren(OBB childA, OBB childB)
        {
            var tRefit = CoACDProfiler.Begin();
            Quaternion invOrientation = Quaternion.Inverse(this.orientation);

            // Transform child A's 8 corners into this OBB's local space
            Vector3 cA = childA.bounds.center;
            Vector3 eA = childA.bounds.extents;
            Vector3 first = invOrientation * (childA.orientation * new Vector3(cA.x - eA.x, cA.y - eA.y, cA.z - eA.z));
            this.bounds = new Bounds(first, Vector3.zero);
            EncapsulateChildCorners(invOrientation, childA.orientation, cA, eA);
            EncapsulateChildCorners(invOrientation, childB.orientation, childB.bounds.center, childB.bounds.extents);

            this.axesOrder = ComputeAxesOrder();
            CoACDProfiler.End("OBB.RefitFromChildren", tRefit);
            return this;
        }

        /// <summary>
        /// Encapsulate the 8 corners of a child OBB into this OBB's bounds.
        /// </summary>
        void EncapsulateChildCorners(Quaternion invParent, Quaternion childRot, Vector3 center, Vector3 extents)
        {
            float cx = center.x, cy = center.y, cz = center.z;
            float ex = extents.x, ey = extents.y, ez = extents.z;
            // All 8 combinations of ±extents
            this.bounds.Encapsulate(invParent * (childRot * new Vector3(cx - ex, cy - ey, cz - ez)));
            this.bounds.Encapsulate(invParent * (childRot * new Vector3(cx + ex, cy - ey, cz - ez)));
            this.bounds.Encapsulate(invParent * (childRot * new Vector3(cx - ex, cy + ey, cz - ez)));
            this.bounds.Encapsulate(invParent * (childRot * new Vector3(cx + ex, cy + ey, cz - ez)));
            this.bounds.Encapsulate(invParent * (childRot * new Vector3(cx - ex, cy - ey, cz + ez)));
            this.bounds.Encapsulate(invParent * (childRot * new Vector3(cx + ex, cy - ey, cz + ez)));
            this.bounds.Encapsulate(invParent * (childRot * new Vector3(cx - ex, cy + ey, cz + ez)));
            this.bounds.Encapsulate(invParent * (childRot * new Vector3(cx + ex, cy + ey, cz + ez)));
        }

        /// <summary>
        /// This method completely builds the OBB from a set of vertices and triangles.
        /// </summary>
        /// <param name="vertices">Vertices from the <c>UCollidersRoot</c> GameObject.</param>
        /// <param name="triangles">The triangles to filter the "useful" vertices.
        /// It will replace the <c>triangles</c> attribute.</param>
        /// <returns>A new <c>OBB</c>.</returns>
        public OBB BuildOBB(Vector3[] vertices, int[] triangles) {
            this.triangles = triangles;
            return BuildOBB(vertices);
        }

        /// <summary>
        /// Estimates how closely the source surface fills this OBB. A rectangular board
        /// scores close to 1 on every non-degenerate projection. Rings, bends and separated
        /// parts score lower because their projected bounding rectangle contains empty area.
        /// Values above 1 (overlapping/folded surfaces) are mirrored back below 1 so they are
        /// treated as complex rather than mistakenly accepted as a tight box.
        /// </summary>
        public float EstimateSurfaceCoverage(Vector3[] vertices)
        {
            if (vertices == null || triangles == null || triangles.Length < 3)
                return 0f;

            Quaternion inverse = Quaternion.Inverse(orientation);
            Vector3 positiveProjectedArea = Vector3.zero;
            Vector3 negativeProjectedArea = Vector3.zero;
            for (int triangle = 0; triangle + 2 < triangles.Length; triangle += 3)
            {
                Vector3 a = inverse * vertices[triangles[triangle]];
                Vector3 b = inverse * vertices[triangles[triangle + 1]];
                Vector3 c = inverse * vertices[triangles[triangle + 2]];
                Vector3 projectedArea = Vector3.Cross(b - a, c - a) * 0.5f;
                for (int axis = 0; axis < 3; axis++)
                {
                    float area = projectedArea[axis];
                    if (area >= 0f)
                        positiveProjectedArea[axis] += area;
                    else
                        negativeProjectedArea[axis] -= area;
                }
            }

            Vector3 size = bounds.size;
            float quality = 1f;
            bool hasProjection = false;
            for (int axis = 0; axis < 3; axis++)
            {
                int firstOtherAxis = (axis + 1) % 3;
                int secondOtherAxis = (axis + 2) % 3;
                float rectangleArea = size[firstOtherAxis] * size[secondOtherAxis];
                if (rectangleArea <= 0.0000001f)
                    continue;

                float positive = positiveProjectedArea[axis];
                float negative = negativeProjectedArea[axis];
                float largerSide = Mathf.Max(positive, negative);
                bool hasBothSides = Mathf.Min(positive, negative) > largerSide * 0.2f;
                float expectedArea = rectangleArea * (hasBothSides ? 2f : 1f);
                float coverage = (positive + negative) / Mathf.Max(0.0000001f, expectedArea);
                float axisQuality = coverage <= 1f
                    ? coverage
                    : 1f / coverage;
                quality = Mathf.Min(quality, Mathf.Clamp01(axisQuality));
                hasProjection = true;
            }

            return hasProjection ? quality : 0f;
        }

        /// <summary>
        /// Subdivide a set of vertices along a specific axis, without holes
        /// </summary>
        /// <param name="vertices">Vertices from the <c>UCollidersRoot</c> GameObject.</param>
        /// <param name="axis">The axis index to subdivide the OBB.</param>
        /// <param name="invOrientation">the inverse orientation of the OBB. Usually <c>Quaternion.Inverse(this.orientation)</c>.</param>
        /// <param name="splitPos">Split position along the axis in OBB-local space.</param>
        /// <returns>Two list of triangles.</returns>
        public List<int>[] InnerSubdivide(Vector3[] vertices, int axis, Quaternion invOrientation, float splitPos) {
            var tSub = CoACDProfiler.Begin();
            bool pos, neg;
            float sign;
            List<int>[] newTriangles = new List<int>[] {new List<int>(), new List<int> ()};
            for (int triangle = 0; triangle < triangles.Length; triangle += 3)
            {
                pos = false;
                neg = false;
                for (int vert = 0; vert < 3; vert++)
                {
                    sign = (invOrientation * vertices[triangles[triangle + vert]])[axis]
                        - splitPos;
                    pos |= sign >= 0;
                    neg |= sign <= 0;
                }
                if (pos)
                    for (int i = 0; i < 3; i++)
                        newTriangles[0].Add(triangles[triangle + i]);
                if (neg)
                    for (int i = 0; i < 3; i++)
                        newTriangles[1].Add(triangles[triangle + i]);
            }
            CoACDProfiler.End("OBB.InnerSubdivide", tSub);
            return newTriangles;
        }

        /// <summary>
        /// Subdivide a set of vertices along a specific axis, leaving holes.
        /// </summary>
        /// <param name="vertices">Vertices from the <c>UCollidersRoot</c> GameObject.</param>
        /// <param name="axis">The axis index to subdivide the OBB.</param>
        /// <param name="invOrientation">the inverse orientation of the OBB. Usually <c>Quaternion.Inverse(this.orientation)</c>.</param>
        /// <param name="splitPos">Split position along the axis in OBB-local space.</param>
        /// <returns>Two list of triangles.</returns>
        public List<int>[] OuterSubdivide(Vector3[] vertices, int axis, Quaternion invOrientation, float splitPos) {
            var tSub = CoACDProfiler.Begin();
            bool pos, neg;
            List<int>[] newTriangles = new List<int>[] {new List<int>(), new List<int> ()};
            for (int triangle = 0; triangle < triangles.Length; triangle += 3)
            {
                pos = true;
                neg = true;
                for (int vert = 0; vert < 3; vert++)
                {
                    float prod = (invOrientation * vertices[triangles[triangle + vert]])[axis]
                        - splitPos;
                    pos &= prod >= 0;
                    neg &= prod <= 0;
                }
                if (pos)
                    for (int i = 0; i < 3; i++)
                        newTriangles[0].Add(triangles[triangle + i]);
                if (neg)
                    for (int i = 0; i < 3; i++)
                        newTriangles[1].Add(triangles[triangle + i]);
            }
            CoACDProfiler.End("OBB.OuterSubdivide", tSub);
            return newTriangles;
        }

        /// <summary>
        /// Subdivide triangles along a split plane, clipping straddling triangles.
        /// Unlike OuterSubdivide (which drops straddling triangles) or InnerSubdivide
        /// (which duplicates them to both sides), this method clips them against the
        /// plane, creating new vertices at edge-plane intersection points.
        /// Each side receives exactly the portion of geometry on its side.
        /// </summary>
        /// <param name="vertices">Mutable vertex list; new clip vertices are appended.</param>
        /// <param name="axis">The axis index to subdivide the OBB.</param>
        /// <param name="invOrientation">Inverse orientation of the OBB.</param>
        /// <param name="splitPos">Split position along the axis in OBB-local space.</param>
        /// <param name="clipDefs">Optional list to record clip vertex definitions for later reconstruction.</param>
        /// <returns>Two lists of triangle indices.</returns>
        public List<int>[] SplittingSubdivide(List<Vector3> vertices, int axis, Quaternion invOrientation, float splitPos, List<ClipVertexDef> clipDefs = null)
        {
            var tSub = CoACDProfiler.Begin();
            List<int>[] newTriangles = new List<int>[] { new List<int>(), new List<int>() };
            var clipVertexByEdge = new Dictionary<long, int>();

            int GetClipVertex(int from, int to, float fromDistance, float toDistance)
            {
                int minimumIndex = Mathf.Min(from, to);
                int maximumIndex = Mathf.Max(from, to);
                long edgeKey = ((long)minimumIndex << 32) | (uint)maximumIndex;
                if (clipVertexByEdge.TryGetValue(edgeKey, out int existingIndex))
                    return existingIndex;

                float interpolation = fromDistance / (fromDistance - toDistance);
                int newIndex = vertices.Count;
                vertices.Add(Vector3.Lerp(vertices[from], vertices[to], interpolation));
                clipVertexByEdge.Add(edgeKey, newIndex);
                clipDefs?.Add(new ClipVertexDef { indexA = from, indexB = to, t = interpolation });
                return newIndex;
            }

            for (int t = 0; t < triangles.Length; t += 3)
            {
                int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
                float d0 = (invOrientation * vertices[i0])[axis] - splitPos;
                float d1 = (invOrientation * vertices[i1])[axis] - splitPos;
                float d2 = (invOrientation * vertices[i2])[axis] - splitPos;

                bool p0 = d0 >= 0, p1 = d1 >= 0, p2 = d2 >= 0;

                if (p0 && p1 && p2)
                {
                    newTriangles[0].Add(i0);
                    newTriangles[0].Add(i1);
                    newTriangles[0].Add(i2);
                }
                else if (!p0 && !p1 && !p2)
                {
                    newTriangles[1].Add(i0);
                    newTriangles[1].Add(i1);
                    newTriangles[1].Add(i2);
                }
                else
                {
                    // Triangle straddles the split plane — clip it.
                    // Identify the lone vertex (on one side by itself).
                    int alone, with1, with2;
                    float dAlone, dWith1, dWith2;
                    bool aloneIsPositive;

                    if (p0 != p1 && p0 != p2)
                    {
                        alone = i0; with1 = i1; with2 = i2;
                        dAlone = d0; dWith1 = d1; dWith2 = d2;
                        aloneIsPositive = p0;
                    }
                    else if (p1 != p0 && p1 != p2)
                    {
                        alone = i1; with1 = i2; with2 = i0;
                        dAlone = d1; dWith1 = d2; dWith2 = d0;
                        aloneIsPositive = p1;
                    }
                    else
                    {
                        alone = i2; with1 = i0; with2 = i1;
                        dAlone = d2; dWith1 = d0; dWith2 = d1;
                        aloneIsPositive = p2;
                    }

                    // Adjacent triangles share edges. Reusing the intersection vertex avoids
                    // adding the same point two or more times at every tree level.
                    int clipIdx1 = GetClipVertex(alone, with1, dAlone, dWith1);
                    int clipIdx2 = GetClipVertex(alone, with2, dAlone, dWith2);

                    int aloneSide = aloneIsPositive ? 0 : 1;
                    int pairSide = 1 - aloneSide;

                    // Alone side: single triangle
                    newTriangles[aloneSide].Add(alone);
                    newTriangles[aloneSide].Add(clipIdx1);
                    newTriangles[aloneSide].Add(clipIdx2);

                    // Pair side: two triangles (quad)
                    newTriangles[pairSide].Add(clipIdx1);
                    newTriangles[pairSide].Add(with1);
                    newTriangles[pairSide].Add(with2);

                    newTriangles[pairSide].Add(clipIdx1);
                    newTriangles[pairSide].Add(with2);
                    newTriangles[pairSide].Add(clipIdx2);
                }
            }

            CoACDProfiler.End("OBB.SplittingSubdivide", tSub);
            return newTriangles;
        }

        /// <summary>
        /// Number of bins used by the binned split heuristic.
        /// </summary>
        const int NumSplitBins = 8;

        /// <summary>
        /// Find the optimal split position along an axis using a binned Surface Area Heuristic (SAH).
        /// Projects triangle centroids onto the axis in OBB-local space, bins them,
        /// then sweeps to find the split that minimizes <c>SA_left * N_left + SA_right * N_right</c>.
        /// </summary>
        /// <param name="vertices">Vertices from the <c>UCollidersRoot</c> GameObject.</param>
        /// <param name="axis">The axis index to split along (in OBB-local space).</param>
        /// <param name="invOrientation">Inverse orientation of the OBB.</param>
        /// <returns>The optimal split position along the axis in OBB-local space.</returns>
        float BinnedSplitPosition(Vector3[] vertices, int axis, Quaternion invOrientation)
        {
            float axisMin = float.MaxValue, axisMax = float.MinValue;

            // First pass: find centroid range along axis
            for (int t = 0; t < triangles.Length; t += 3)
            {
                float centroid = 0f;
                for (int v = 0; v < 3; v++)
                    centroid += (invOrientation * vertices[triangles[t + v]])[axis];
                centroid /= 3f;
                if (centroid < axisMin) axisMin = centroid;
                if (centroid > axisMax) axisMax = centroid;
            }

            // Degenerate: all centroids at the same position
            if (axisMax - axisMin < 1e-6f)
                return bounds.center[axis];

            float binWidth = (axisMax - axisMin) / NumSplitBins;

            // Per-bin accumulators
            int[] binCounts = new int[NumSplitBins];
            Vector3[] binMin = new Vector3[NumSplitBins];
            Vector3[] binMax = new Vector3[NumSplitBins];
            for (int i = 0; i < NumSplitBins; i++)
            {
                binMin[i] = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                binMax[i] = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            }

            // Second pass: assign triangles to bins and accumulate bounds
            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vector3 v0 = invOrientation * vertices[triangles[t]];
                Vector3 v1 = invOrientation * vertices[triangles[t + 1]];
                Vector3 v2 = invOrientation * vertices[triangles[t + 2]];
                float centroid = (v0[axis] + v1[axis] + v2[axis]) / 3f;

                int bin = Mathf.Clamp((int)((centroid - axisMin) / binWidth), 0, NumSplitBins - 1);
                binCounts[bin]++;

                binMin[bin] = Vector3.Min(binMin[bin], Vector3.Min(v0, Vector3.Min(v1, v2)));
                binMax[bin] = Vector3.Max(binMax[bin], Vector3.Max(v0, Vector3.Max(v1, v2)));
            }

            // Left sweep: cumulative bounds and counts
            Vector3[] leftMin = new Vector3[NumSplitBins - 1];
            Vector3[] leftMax = new Vector3[NumSplitBins - 1];
            int[] leftCount = new int[NumSplitBins - 1];

            Vector3 cumMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 cumMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            int cumCount = 0;

            for (int i = 0; i < NumSplitBins - 1; i++)
            {
                if (binCounts[i] > 0)
                {
                    cumMin = Vector3.Min(cumMin, binMin[i]);
                    cumMax = Vector3.Max(cumMax, binMax[i]);
                }
                cumCount += binCounts[i];
                leftMin[i] = cumMin;
                leftMax[i] = cumMax;
                leftCount[i] = cumCount;
            }

            // Right sweep: find best split by SAH cost
            float bestCost = float.MaxValue;
            int bestSplit = NumSplitBins / 2;

            cumMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            cumMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            cumCount = 0;

            for (int i = NumSplitBins - 1; i >= 1; i--)
            {
                if (binCounts[i] > 0)
                {
                    cumMin = Vector3.Min(cumMin, binMin[i]);
                    cumMax = Vector3.Max(cumMax, binMax[i]);
                }
                cumCount += binCounts[i];

                int splitIdx = i - 1;
                int lc = leftCount[splitIdx];
                int rc = cumCount;

                if (lc == 0 || rc == 0) continue;

                Vector3 lSize = leftMax[splitIdx] - leftMin[splitIdx];
                Vector3 rSize = cumMax - cumMin;
                float lSA = 2f * (lSize.x * lSize.y + lSize.y * lSize.z + lSize.z * lSize.x);
                float rSA = 2f * (rSize.x * rSize.y + rSize.y * rSize.z + rSize.z * rSize.x);

                float cost = lSA * lc + rSA * rc;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestSplit = splitIdx;
                }
            }

            return axisMin + (bestSplit + 1) * binWidth;
        }

        /// <summary>
        /// Try to split this OBB Node in two children.
        /// Uses <see cref="SplittingSubdivide"/> for both enclosure paths. Clipping gives
        /// complete surface coverage without the exponential triangle duplication caused
        /// by assigning every straddling triangle in full to both children.
        /// New clip vertices are appended to <paramref name="vertices"/>.
        /// </summary>
        /// <param name="vertices">Vertices from the <c>UCollidersRoot</c> GameObject.
        /// May be extended with clip vertices when triangles straddle the split plane.</param>
        /// <param name="id">Id of the parent <c>OBBTreeNode</c>.</param>
        /// <param name="noise">A value to randomize vertex positions and avoiding issues with planes.</param>
        /// <param name="outerSubdivide">If <c>false</c>, subdivide enclosing all triangles instead of vertices.</param>
        /// <returns>
        /// <list type="bullet">
        /// <item>
        /// <description>Two OBBs if the division was successful.</description>
        /// </item>
        /// <item>
        /// <description>An array of OBB of size 0 if no further subdivion was possible.</description>
        /// </item>
        /// </list>
        /// </returns>
        /// <remarks>By default, an OBB has two children that are <c>{ null, null }</c>.</remarks>
        public OBBTreeNode[] ComputeChildren(ref Vector3[] vertices, string id, Vector3 noise=new Vector3(), bool outerSubdivide=true, List<ClipVertexDef> clipDefs=null)
        {
            var tChildren = CoACDProfiler.Begin();
            int axisIndex = 0;
            // A simple shifter
            int outerShift = outerSubdivide ? 0 : 2;
            List<int>[] newTriangles = new List<int>[2];
            Quaternion inv = Quaternion.Inverse(orientation);
            List<Vector3> vertexList = new List<Vector3>(vertices);
            int clipDefsStart = clipDefs?.Count ?? 0;
            do
            {
                int axis = axesOrder[axisIndex];
                float splitPos = BinnedSplitPosition(vertices, axis, inv);
                // Reset temporary clips when another principal axis must be tried.
                if (vertexList.Count > vertices.Length)
                    vertexList.RemoveRange(vertices.Length, vertexList.Count - vertices.Length);
                if (clipDefs != null && clipDefs.Count > clipDefsStart)
                    clipDefs.RemoveRange(clipDefsStart, clipDefs.Count - clipDefsStart);
                newTriangles = SplittingSubdivide(vertexList, axis, inv, splitPos, clipDefs);
                axisIndex++;
            } while (axisIndex < 3 && (newTriangles[0].Count < MinTriangleIndicesPerChild || newTriangles[1].Count < MinTriangleIndicesPerChild));

            // Stop everything if some child is ill-formed
            if (newTriangles[0].Count < MinTriangleIndicesPerChild || newTriangles[1].Count < MinTriangleIndicesPerChild)
            {
                CoACDProfiler.End("OBB.ComputeChildren", tChildren);
                return new OBBTreeNode[0];
            }

            // Commit clip vertices to the shared vertex array
            if (vertexList.Count > vertices.Length)
                vertices = vertexList.ToArray();

            // Otherwise build the stuff
            OBBTreeNode[] children = new OBBTreeNode[] {
                new OBBTreeNode(id + outerShift.ToString()),
                new OBBTreeNode(id + (outerShift + 1).ToString())
            };
            for (int i = 0; i < 2; i++) {
                try
                {
                    children[i].obb = children[i].obb.BuildOBB(vertices, newTriangles[i].ToArray());
                } catch (UColliders.CovarianceNotConvergentException)
                {
                    Vector3[] noised_vertices = new Vector3[vertices.Length];
                    for (int j = 0; j < vertices.Length; j++)
                        noised_vertices[j] = vertices[j] + Vector3.Scale(noise, Random.onUnitSphere);
                    try {
                        children[i].obb = children[i].obb.BuildOBB(noised_vertices, newTriangles[i].ToArray());
                    } catch (UColliders.CovarianceNotConvergentException) {
                        return new OBBTreeNode[0];
                    }
                }
                children[i].obb.triangles = newTriangles[i].ToArray();
            }
            CoACDProfiler.End("OBB.ComputeChildren", tChildren);
            return children;
        }

    }
}
