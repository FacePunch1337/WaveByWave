using NUnit.Framework;
using UnityEngine;
using UColliders;

namespace UColliders.Tests.Editor
{
    /// <summary>
    /// Tests for the BalanceMesh feature (optimizeMesh = true).
    /// BalanceMesh is a private method, tested through the public InitOBB API.
    /// </summary>
    public class BalanceMeshTests
    {
        private GameObject _testObject;
        private UCollidersRoot _root;

        /// <summary>
        /// Creates a heavily unbalanced mesh: a long thin rectangle (100x1).
        /// 4 vertices, 2 triangles spanning a large area.
        /// </summary>
        static Mesh CreateLongRectangleMesh(float length = 100f)
        {
            var mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3(0, 0, 0),
                new Vector3(length, 0, 0),
                new Vector3(0, 1, 0),
                new Vector3(length, 1, 0)
            };
            mesh.triangles = new int[]
            {
                0, 1, 2,
                1, 3, 2
            };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        /// <summary>
        /// Creates a standard cube mesh for baseline comparison.
        /// </summary>
        static Mesh CreateCubeMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3(-1, -1, -1), new Vector3(1, -1, -1),
                new Vector3(-1,  1, -1), new Vector3(1,  1, -1),
                new Vector3(-1, -1,  1), new Vector3(1, -1,  1),
                new Vector3(-1,  1,  1), new Vector3(1,  1,  1)
            };
            mesh.triangles = new int[]
            {
                0,2,1, 1,2,3,
                4,5,6, 5,7,6,
                0,1,4, 1,5,4,
                2,6,3, 3,6,7,
                0,4,2, 2,4,6,
                1,3,5, 3,7,5
            };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        [SetUp]
        public void SetUp()
        {
            _testObject = new GameObject("TestBalanceMesh");
            var meshFilter = _testObject.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = CreateCubeMesh();
            _root = _testObject.AddComponent<UCollidersRoot>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Transform child in _testObject.transform)
                Object.DestroyImmediate(child.gameObject);
            if (_testObject != null)
                Object.DestroyImmediate(_testObject);
        }

        // ========================================================================
        // Basic functionality
        // ========================================================================

        [Test]
        public void OptimizeMesh_CubeMesh_IncreasesVertexCount()
        {
            _root.optimizeMesh = true;
            _root.recursionLevel = 2;
            _root.InitOBB();

            Assert.Greater(_root.mesh.vertexCount, 8,
                "Optimized cube mesh should have more than 8 original vertices");
        }

        [Test]
        public void OptimizeMesh_ProducesValidTriangleIndices()
        {
            _root.optimizeMesh = true;
            _root.recursionLevel = 2;
            _root.InitOBB();

            int vertexCount = _root.mesh.vertexCount;
            int[] triangles = _root.mesh.triangles;
            Assert.AreEqual(0, triangles.Length % 3,
                "Triangle index count should be a multiple of 3");
            for (int i = 0; i < triangles.Length; i++)
            {
                Assert.GreaterOrEqual(triangles[i], 0,
                    $"Triangle index [{i}] = {triangles[i]} should be >= 0");
                Assert.Less(triangles[i], vertexCount,
                    $"Triangle index [{i}] = {triangles[i]} should be < vertexCount ({vertexCount})");
            }
        }

        // ========================================================================
        // Vertex explosion on unbalanced meshes
        // ========================================================================

        [Test]
        public void OptimizeMesh_LongRectangle_VertexCountStaysReasonable()
        {
            // A 100x1 rectangle. BalanceMesh scales edges by bounds.extents,
            // so long edges get enormous cut counts: cuts = ceil(scaledMag * 2^level).
            // At level 2 with extents.x=50: scaledEdge.magnitude=5000, cuts=20000 PER EDGE.
            var meshFilter = _testObject.GetComponent<MeshFilter>();
            meshFilter.sharedMesh = CreateLongRectangleMesh(100f);

            _root.optimizeMesh = true;
            _root.recursionLevel = 2;
            _root.InitOBB();

            // With 4 original vertices and 6 edge-triangle pairs, a reasonable
            // optimization might add ~100-500 vertices.
            // The current implementation creates ~80,000.
            Assert.Less(_root.mesh.vertexCount, 1000,
                $"Vertex count {_root.mesh.vertexCount} exploded for a simple 100x1 rectangle at recursion level 2");
        }

        [Test]
        public void OptimizeMesh_ModerateRectangle_VertexCountStaysReasonable()
        {
            // Even a 10x1 rectangle should not produce excessive vertices
            var meshFilter = _testObject.GetComponent<MeshFilter>();
            meshFilter.sharedMesh = CreateLongRectangleMesh(10f);

            _root.optimizeMesh = true;
            _root.recursionLevel = 2;
            _root.InitOBB();

            Assert.Less(_root.mesh.vertexCount, 1000,
                $"Vertex count {_root.mesh.vertexCount} is too high for a 10x1 rectangle at recursion level 2");
        }

        // ========================================================================
        // Core bug: new triangles straddle the OBB split plane
        // ========================================================================

        [Test]
        public void OptimizeMesh_NewVerticesActuallyUsedAfterOuterSubdivide()
        {
            // This is the KEY test. BalanceMesh creates triangles of the form
            // (idxA, idxB, newPoint) where idxA and idxB are the ENDPOINTS of
            // a long edge. When OuterSubdivide splits along that edge, both
            // endpoints are on opposite sides, so ALL new triangles straddle
            // the split plane and get DROPPED. The new vertices become orphaned.
            var meshFilter = _testObject.GetComponent<MeshFilter>();
            meshFilter.sharedMesh = CreateLongRectangleMesh(10f);

            _root.optimizeMesh = true;
            _root.recursionLevel = 1;
            _root.InitOBB();

            int totalVerticesBefore = _root.mesh.vertexCount;

            // SplittingSubdivide (default mode)
            Vector3[] verts = _root.mesh.vertices;
            var children = _root.node.obb.ComputeChildren(
                ref verts, _root.node.id
            );
            Assert.AreEqual(2, children.Length,
                "Should produce 2 children");

            // Count unique vertices referenced by children's triangles
            var usedVertices = new System.Collections.Generic.HashSet<int>();
            foreach (int idx in children[0].obb.triangles)
                usedVertices.Add(idx);
            foreach (int idx in children[1].obb.triangles)
                usedVertices.Add(idx);

            float usageRatio = (float)usedVertices.Count / totalVerticesBefore;

            // If BalanceMesh works correctly, most new vertices should appear
            // in children's triangles after subdivision.
            // If the bug exists, only the original ~4 vertices will be used.
            Assert.Greater(usageRatio, 0.5f,
                $"Only {usedVertices.Count} of {totalVerticesBefore} vertices " +
                $"({usageRatio * 100:F1}%) are referenced after OuterSubdivide. " +
                "New BalanceMesh vertices are orphaned because their triangles " +
                "straddle the split plane.");
        }

        [Test]
        public void OptimizeMesh_ChildTriangleCountIncreasesWithOptimization()
        {
            var meshFilter = _testObject.GetComponent<MeshFilter>();
            meshFilter.sharedMesh = CreateLongRectangleMesh(10f);

            // Without optimization
            _root.optimizeMesh = false;
            _root.recursionLevel = 1;
            _root.InitOBB();

            Vector3[] vertsUnopt = _root.mesh.vertices;
            var childrenUnopt = _root.node.obb.ComputeChildren(
                ref vertsUnopt, _root.node.id
            );
            int unoptTriangles = 0;
            if (childrenUnopt.Length == 2)
                unoptTriangles = childrenUnopt[0].obb.triangles.Length
                    + childrenUnopt[1].obb.triangles.Length;

            // With optimization
            _root.optimizeMesh = true;
            _root.recursionLevel = 1;
            _root.InitOBB();

            Vector3[] vertsOpt = _root.mesh.vertices;
            var childrenOpt = _root.node.obb.ComputeChildren(
                ref vertsOpt, _root.node.id
            );
            int optTriangles = 0;
            if (childrenOpt.Length == 2)
                optTriangles = childrenOpt[0].obb.triangles.Length
                    + childrenOpt[1].obb.triangles.Length;

            // After BalanceMesh adds many vertices and triangles, the children
            // should have MORE triangles (better coverage). If they don't,
            // the new triangles were all dropped by OuterSubdivide.
            Assert.Greater(optTriangles, unoptTriangles,
                $"Optimized children have {optTriangles} triangle indices vs " +
                $"unoptimized {unoptTriangles}. BalanceMesh had no effect on subdivision.");
        }

        // ========================================================================
        // New triangle quality
        // ========================================================================

        [Test]
        public void OptimizeMesh_TriangleCountIncreasesOrStaysEqual()
        {
            // BalanceMesh replaces long-edge triangles with fan sub-triangles.
            // Each subdivided triangle produces (cuts+1) fan triangles, so the
            // total triangle count should be >= the original 12 faces (36 indices).
            _root.optimizeMesh = true;
            _root.recursionLevel = 1;
            _root.InitOBB();

            Assert.GreaterOrEqual(_root.mesh.triangles.Length, 36,
                "Balanced mesh should have at least as many triangle indices as the original");
        }

        [Test]
        public void OptimizeMesh_NewTrianglesHaveReasonableArea()
        {
            // BalanceMesh creates triangles (idxA, idxB, newPoint) where
            // idxA and idxB are endpoints of a long edge. These are very thin
            // sliver triangles, not proper subdivisions of the original faces.
            _root.optimizeMesh = true;
            _root.recursionLevel = 1;
            _root.InitOBB();

            Vector3[] vertices = _root.mesh.vertices;
            int[] triangles = _root.mesh.triangles;
            int totalTriangles = triangles.Length / 3;
            int thinCount = 0;
            float minAspectRatio = float.MaxValue;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 v0 = vertices[triangles[i]];
                Vector3 v1 = vertices[triangles[i + 1]];
                Vector3 v2 = vertices[triangles[i + 2]];

                float a = (v1 - v0).magnitude;
                float b = (v2 - v1).magnitude;
                float c = (v0 - v2).magnitude;
                float longestEdge = Mathf.Max(a, Mathf.Max(b, c));
                float area = Vector3.Cross(v1 - v0, v2 - v0).magnitude * 0.5f;

                if (longestEdge > 0.001f)
                {
                    // Aspect ratio: height / base. Thin triangles have very small ratios.
                    float height = 2 * area / longestEdge;
                    float aspectRatio = height / longestEdge;
                    minAspectRatio = Mathf.Min(minAspectRatio, aspectRatio);
                    if (aspectRatio < 0.01f)
                        thinCount++;
                }
            }

            float thinRatio = (float)thinCount / totalTriangles;
            Assert.Less(thinRatio, 0.5f,
                $"{thinCount} of {totalTriangles} triangles ({thinRatio * 100:F1}%) " +
                $"are extremely thin slivers (aspect ratio < 0.01). " +
                $"Min aspect ratio: {minAspectRatio:F6}. " +
                "BalanceMesh creates (idxA, idxB, newPoint) triangles that share " +
                "the full original edge, producing degenerate slivers instead of " +
                "proper face subdivisions.");
        }

        // ========================================================================
        // Bounds integrity
        // ========================================================================

        [Test]
        public void OptimizeMesh_BoundsDoNotGrowExcessively()
        {
            // Get unoptimized bounds
            _root.optimizeMesh = false;
            _root.recursionLevel = 2;
            _root.InitOBB();
            Bounds originalBounds = _root.mesh.bounds;

            // Optimize
            _root.optimizeMesh = true;
            _root.InitOBB();
            Bounds optimizedBounds = _root.mesh.bounds;

            // Random noise displaces vertices outside the original mesh.
            // Noise is: Scale(Random.insideUnitSphere, extents) / 10.
            // That's up to 10% of extents in each direction.
            // Bounds should not grow by more than 25% in any dimension.
            for (int axis = 0; axis < 3; axis++)
            {
                float originalSize = originalBounds.size[axis];
                float optimizedSize = optimizedBounds.size[axis];
                if (originalSize > 0.001f)
                {
                    float growth = (optimizedSize - originalSize) / originalSize;
                    Assert.Less(growth, 0.25f,
                        $"Axis {axis}: bounds grew by {growth * 100:F1}% " +
                        $"(from {originalSize:F3} to {optimizedSize:F3}). " +
                        "Random noise in BalanceMesh is pushing vertices outside the mesh.");
                }
            }
        }

        // ========================================================================
        // End-to-end pipeline
        // ========================================================================

        [Test]
        public void OptimizeMesh_CubeMesh_FullPipelineCreatesLeaves()
        {
            _root.optimizeMesh = true;
            _root.recursionLevel = 1;
            _root.InitOBB();
            _root.Start();

            var leaves = _root.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leaves.Length, 0,
                "Optimized cube should produce at least one leaf collider");
        }

        [Test]
        public void OptimizeMesh_LongRectangle_FullPipelineCreatesLeaves()
        {
            var meshFilter = _testObject.GetComponent<MeshFilter>();
            meshFilter.sharedMesh = CreateLongRectangleMesh(10f);

            _root.optimizeMesh = true;
            _root.recursionLevel = 1;
            _root.InitOBB();
            _root.Start();

            var leaves = _root.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leaves.Length, 0,
                "Optimized long rectangle should produce at least one leaf collider");
        }

        // ========================================================================
        // Edge duplication: shared edges processed multiple times
        // ========================================================================

        [Test]
        public void OptimizeMesh_SharedEdgesNotDuplicatedExcessively()
        {
            // Known limitation: shared edges between adjacent triangles produce
            // duplicate midpoints (same position, different vertex indices).
            // This is harmless for OBB computation and avoiding it would require
            // edge deduplication bookkeeping. We just verify it stays bounded.
            _root.optimizeMesh = true;
            _root.recursionLevel = 1;
            _root.InitOBB();

            Vector3[] vertices = _root.mesh.vertices;
            int duplicateCount = 0;
            float threshold = 0.5f;

            // Check for near-duplicate vertices (O(n^2) but mesh is small)
            for (int i = 0; i < vertices.Length; i++)
            {
                for (int j = i + 1; j < vertices.Length; j++)
                {
                    if ((vertices[i] - vertices[j]).sqrMagnitude < threshold * threshold)
                    {
                        duplicateCount++;
                    }
                }
            }

            float duplicateRatio = (float)duplicateCount / vertices.Length;
            Assert.Less(duplicateRatio, 5.0f,
                $"Found {duplicateCount} near-duplicate vertex pairs among {vertices.Length} vertices " +
                $"({duplicateRatio * 100:F1}%). Shared edge duplication is expected " +
                "but should stay bounded.");
        }
    }
}
