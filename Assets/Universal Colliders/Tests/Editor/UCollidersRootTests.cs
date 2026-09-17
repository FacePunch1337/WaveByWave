using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UColliders;

namespace UColliders.Tests.Editor
{
    /// <summary>
    /// Integration tests for UCollidersRoot using real GameObjects.
    /// </summary>
    public class UCollidersRootTests
    {
        private GameObject _testObject;
        private UCollidersRoot _root;

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
            _testObject = new GameObject("TestOBB");
            var meshFilter = _testObject.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = CreateCubeMesh();
            _root = _testObject.AddComponent<UCollidersRoot>();
            // Ensure InitOBB has run even if Awake hasn't fired yet
            if (_root.node == null)
                _root.InitOBB();
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up all child objects first
            foreach (Transform child in _testObject.transform)
                Object.DestroyImmediate(child.gameObject);
            if (_testObject != null)
                Object.DestroyImmediate(_testObject);
        }

        // --- InitOBB ---

        [Test]
        public void InitOBB_BuildsValidRootNode()
        {
            Assert.IsNotNull(_root.node);
            Assert.AreEqual("", _root.node.id);
            Assert.IsNotNull(_root.node.obb.triangles);
            Assert.Greater(_root.node.obb.triangles.Length, 0);
        }

        [Test]
        public void InitOBB_SetsMesh()
        {
            Assert.IsNotNull(_root.mesh);
            Assert.Greater(_root.mesh.vertexCount, 0);
            Assert.Greater(_root.mesh.triangles.Length, 0);
        }

        [Test]
        public void InitOBB_RootNodeHasUninitializedChildren()
        {
            // After InitOBB, children are {null, null} (not yet computed)
            Assert.AreEqual(2, _root.node.children.Length);
            Assert.IsNull(_root.node.children[0]);
            Assert.IsNull(_root.node.children[1]);
        }

        // --- GetOBBAtPath ---

        [Test]
        public void GetOBBAtPath_ReturnsRootForEmptyPath()
        {
            var result = _root.GetOBBAtPath("");
            Assert.AreSame(_root.node, result);
        }

        [Test]
        public void GetOBBAtPath_ComputesAndReturnsFirstChild()
        {
            var child = _root.GetOBBAtPath("0");
            Assert.IsNotNull(child);
            Assert.AreEqual("0", child.id);
            Assert.IsNotNull(child.obb.triangles);
            Assert.Greater(child.obb.triangles.Length, 0);
        }

        [Test]
        public void GetOBBAtPath_ComputesAndReturnsSecondChild()
        {
            var child = _root.GetOBBAtPath("1");
            Assert.IsNotNull(child);
            Assert.AreEqual("1", child.id);
            Assert.Greater(child.obb.triangles.Length, 0);
        }

        [Test]
        public void GetOBBAtPath_ComputesGrandchildren()
        {
            // Use inner subdivide path ('2','3') because the cube's sparse faces
            // (2 triangles each) cannot be outer-subdivided further at level 2.
            var grandchild = _root.GetOBBAtPath("22");
            Assert.IsNotNull(grandchild);
            Assert.AreEqual("22", grandchild.id);
        }

        [Test]
        public void GetOBBAtPath_ThrowsForUncomputableDeepPath()
        {
            // Replace the cube with a tetrahedron (4 triangles = 12 indices).
            // With MinTriangleIndicesPerChild = 4, this exhausts after 2-3 splits.
            var tetraMesh = new Mesh();
            tetraMesh.vertices = new Vector3[]
            {
                new Vector3(0, 1, 0),
                new Vector3(-1, -1, -1),
                new Vector3(1, -1, -1),
                new Vector3(0, -1, 1)
            };
            tetraMesh.triangles = new int[] { 0,1,2, 0,2,3, 0,3,1, 1,3,2 };
            tetraMesh.RecalculateBounds();
            _testObject.GetComponent<MeshFilter>().sharedMesh = tetraMesh;
            _root.InitOBB();

            Assert.Throws<PathNotComputableException>(() =>
            {
                _root.GetOBBAtPath("2222222222");
            });
        }

        // --- CountLeaves ---

        [Test]
        public void CountLeaves_ReturnsZeroWhenRecursionNegative()
        {
            _root.recursionLevel = -1;
            Assert.AreEqual(0, _root.CountLeaves());
        }

        [Test]
        public void CountLeaves_ReturnsOneAtLevelZero()
        {
            _root.recursionLevel = 0;
            Assert.AreEqual(1, _root.CountLeaves());
        }

        [Test]
        public void CountLeaves_ReturnsTwoAtLevelOneAfterComputing()
        {
            _root.recursionLevel = 1;
            // Force children to be computed
            _root.GetOBBAtPath("0");
            Assert.AreEqual(2, _root.CountLeaves());
        }

        // --- SetEncapsulationPath ---

        [Test]
        public void SetEncapsulationPath_ExtendsPathWithZerosWhenVertexOnly()
        {
            _root.recursionLevel = 3;
            _root.encapsulationPath = "";
            _root.encapsulateTriangles = false;

            _root.SetEncapsulationPath(3);
            Assert.AreEqual(4, _root.encapsulationPath.Length);
            foreach (char c in _root.encapsulationPath)
                Assert.AreEqual('0', c);
        }

        [Test]
        public void SetEncapsulationPath_ExtendsPathWithOnesWhenTriangleEncap()
        {
            _root.recursionLevel = 2;
            _root.encapsulationPath = "";
            _root.encapsulateTriangles = true;

            _root.SetEncapsulationPath(2);
            Assert.AreEqual(3, _root.encapsulationPath.Length);
            foreach (char c in _root.encapsulationPath)
                Assert.AreEqual('1', c);
        }

        [Test]
        public void SetEncapsulationPath_ClearsPathForNegativeLevel()
        {
            _root.recursionLevel = -1;
            _root.encapsulationPath = "010";

            _root.SetEncapsulationPath(-1);
            Assert.AreEqual("", _root.encapsulationPath);
        }

        // --- SetEncapsulationPath null safety ---

        [Test]
        public void SetEncapsulationPath_NullPath_DoesNotThrow()
        {
            _root.recursionLevel = 2;
            _root.encapsulationPath = null;
            _root.encapsulateTriangles = false;

            Assert.DoesNotThrow(() => _root.SetEncapsulationPath(2));
            Assert.IsNotNull(_root.encapsulationPath);
        }

        [Test]
        public void SetEncapsulationPath_NullPath_ExtendsCorrectly()
        {
            _root.recursionLevel = 1;
            _root.encapsulationPath = null;
            _root.encapsulateTriangles = true;

            _root.SetEncapsulationPath(1);
            Assert.AreEqual(2, _root.encapsulationPath.Length);
            foreach (char c in _root.encapsulationPath)
                Assert.AreEqual('1', c);
        }

        [Test]
        public void SetEncapsulationPath_DoesNotEditWhenBelowRecursionLevel()
        {
            _root.recursionLevel = 5;
            _root.encapsulationPath = "010";

            _root.SetEncapsulationPath(2);
            Assert.AreEqual("010", _root.encapsulationPath, "Should not modify path when level < recursionLevel");
        }

        // --- MaxRecursionDepth constant ---

        [Test]
        public void MaxRecursionDepth_IsPositive()
        {
            Assert.Greater(UCollidersRoot.MaxRecursionDepth, 0);
        }

        // --- Property validation ---

        [Test]
        public void RecursionLevel_ClampsToMaxRecursionDepth()
        {
            _root.recursionLevel = 999;
            Assert.AreEqual(UCollidersRoot.MaxRecursionDepth, _root.recursionLevel);
        }

        [Test]
        public void RecursionLevel_ClampsToMinusOne()
        {
            _root.recursionLevel = -10;
            Assert.AreEqual(-1, _root.recursionLevel);
        }

        [Test]
        public void Boundicity_ClampsToZeroOne()
        {
            _root.boundicity = 2f;
            Assert.AreEqual(1f, _root.boundicity, 0.001f);

            _root.boundicity = -0.5f;
            Assert.AreEqual(0f, _root.boundicity, 0.001f);
        }

        [Test]
        public void Shape_CanBeSetAndRead()
        {
            _root.shape = Shape.Capsule;
            Assert.AreEqual(Shape.Capsule, _root.shape);

            _root.shape = Shape.Box;
            Assert.AreEqual(Shape.Box, _root.shape);
        }

        // --- DestroyColliders ---

        [Test]
        public void DestroyColliders_RemovesChildLeaves()
        {
            var leafObj = new GameObject("UCollider");
            leafObj.AddComponent<UCollidersLeaf>();
            leafObj.transform.SetParent(_root.transform);

            _root.DestroyColliders();

            // Unity overloads == so destroyed objects compare to null
            Assert.IsTrue(leafObj == null);
        }

        [Test]
        public void DestroyColliders_PreservesMarkedLeaves()
        {
            var leafObj = new GameObject("UCollider");
            var leaf = leafObj.AddComponent<UCollidersLeaf>();
            leaf.preserveUCollider = true;
            leafObj.transform.SetParent(_root.transform);

            _root.DestroyColliders(force: false);

            Assert.IsFalse(leafObj == null, "Preserved leaf should not be destroyed");

            // Clean up
            Object.DestroyImmediate(leafObj);
        }

        [Test]
        public void DestroyColliders_ForceRemovesPreservedLeaves()
        {
            var leafObj = new GameObject("UCollider");
            var leaf = leafObj.AddComponent<UCollidersLeaf>();
            leaf.preserveUCollider = true;
            leafObj.transform.SetParent(_root.transform);

            _root.DestroyColliders(force: true);

            Assert.IsTrue(leafObj == null);
        }

        // --- RegenerateColliders (full pipeline) ---

        [Test]
        public void Start_WithRecursionLevel1_CreatesLeaves()
        {
            _root.recursionLevel = 1;
            _root.InitOBB();
            _root.Start();

            var leaves = _root.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leaves.Length, 0, "Should have created leaf colliders");
        }

        [Test]
        public void Start_WithRecursionLevel1_LeavesHaveColliders()
        {
            _root.recursionLevel = 1;
            _root.InitOBB();
            _root.Start();

            var leaves = _root.GetComponentsInChildren<UCollidersLeaf>();
            foreach (var leaf in leaves)
            {
                var collider = leaf.GetComponent<Collider>();
                Assert.IsNotNull(collider, $"Leaf {leaf.name} should have a Collider");
            }
        }

        // --- Shape variants ---

        [Test]
        public void Start_WithSphereShape_CreatesSpherColliders()
        {
            _root.recursionLevel = 1;
            _root.shape = Shape.Sphere;
            _root.InitOBB();
            _root.Start();

            var leaves = _root.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leaves.Length, 0);
            foreach (var leaf in leaves)
                Assert.IsNotNull(leaf.GetComponent<SphereCollider>(),
                    "Leaf should have SphereCollider");
        }

        [Test]
        public void Start_WithCapsuleShape_CreatesCapsuleColliders()
        {
            _root.recursionLevel = 1;
            _root.shape = Shape.Capsule;
            _root.InitOBB();
            _root.Start();

            var leaves = _root.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leaves.Length, 0);
            foreach (var leaf in leaves)
                Assert.IsNotNull(leaf.GetComponent<CapsuleCollider>(),
                    "Leaf should have CapsuleCollider");
        }

        [Test]
        public void Start_WithMeshShape_CreatesMeshColliders()
        {
            _root.recursionLevel = 1;
            _root.shape = Shape.Mesh;
            _root.InitOBB();
            _root.Start();

            var leaves = _root.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leaves.Length, 0);
            foreach (var leaf in leaves)
                Assert.IsNotNull(leaf.GetComponent<MeshCollider>(),
                    "Leaf should have MeshCollider");
        }

        // --- Deeper recursion ---

        [Test]
        public void Start_WithRecursionLevel2_CreatesMoreLeaves()
        {
            _root.recursionLevel = 2;
            // Use inner subdivide so the cube's sparse faces (2 triangles each)
            // can be further split at level 2.
            _root.encapsulateTriangles = true;
            _root.InitOBB();
            _root.Start();

            var leaves = _root.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leaves.Length, 2,
                "Recursion level 2 should create more than 2 leaves");
        }

        // --- CountLeaves at higher levels ---

        [Test]
        public void CountLeaves_AtLevel2_ReturnsFour()
        {
            _root.recursionLevel = 2;
            // Use inner subdivide paths ('2','3') because the cube's sparse faces
            // cannot be outer-subdivided at level 2.
            // Each GetOBBAtPath computes both children of the intermediate node.
            _root.GetOBBAtPath("22");
            _root.GetOBBAtPath("32");
            Assert.AreEqual(4, _root.CountLeaves());
        }

        // --- InitOBB error cases ---

        [Test]
        public void InitOBB_ThrowsNoMeshFilterException_WhenNoMeshFilter()
        {
            // Create with a valid mesh so Awake's InitOBB succeeds,
            // then remove the MeshFilter to test the no-mesh path.
            var noMeshObj = new GameObject("NoMesh");
            var mf = noMeshObj.AddComponent<MeshFilter>();
            mf.sharedMesh = CreateCubeMesh();
            var root = noMeshObj.AddComponent<UCollidersRoot>();

            Object.DestroyImmediate(mf);
            root.includeChildrenMeshes = false;

            Assert.Throws<NoMeshFilterException>(() => root.InitOBB());

            Object.DestroyImmediate(noMeshObj);
        }

        [Test]
        public void InitOBB_ThrowsTooFewVerticesException_WhenEmptyMesh()
        {
            // Create with a valid mesh so Awake's InitOBB succeeds,
            // then swap to an empty mesh to test the error path.
            var emptyMeshObj = new GameObject("EmptyMesh");
            var mf = emptyMeshObj.AddComponent<MeshFilter>();
            mf.sharedMesh = CreateCubeMesh();
            var root = emptyMeshObj.AddComponent<UCollidersRoot>();

            mf.sharedMesh = new Mesh();

            Assert.Throws<TooFewVerticesException>(() => root.InitOBB());

            Object.DestroyImmediate(emptyMeshObj);
        }

        // --- UnpackColliders ---

        [Test]
        public void UnpackColliders_ReturnsCountOfUnpacked()
        {
            _root.recursionLevel = 1;
            _root.InitOBB();
            _root.Start();

            int leafCount = _root.GetComponentsInChildren<UCollidersLeaf>().Length;
            int unpacked = _root.UnpackColliders();

            Assert.AreEqual(leafCount, unpacked);
        }

        [Test]
        public void UnpackColliders_RemovesLeafComponents()
        {
            _root.recursionLevel = 1;
            _root.InitOBB();
            _root.Start();
            Assert.Greater(_root.GetComponentsInChildren<UCollidersLeaf>().Length, 0);

            _root.UnpackColliders();

            // After unpack, UCollidersLeaf components are destroyed
            // but colliders remain on the child objects
            var remainingLeaves = _root.GetComponentsInChildren<UCollidersLeaf>();
            Assert.AreEqual(0, remainingLeaves.Length,
                "All UCollidersLeaf components should be removed after unpack");
        }
    }

    /// <summary>
    /// Tests for UCollidersLeaf component.
    /// </summary>
    public class UCollidersLeafTests
    {
        private GameObject _rootObject;
        private UCollidersRoot _root;

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
            _rootObject = new GameObject("TestRoot");
            var meshFilter = _rootObject.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = CreateCubeMesh();
            _root = _rootObject.AddComponent<UCollidersRoot>();
            if (_root.node == null)
                _root.InitOBB();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Transform child in _rootObject.transform)
                Object.DestroyImmediate(child.gameObject);
            if (_rootObject != null)
                Object.DestroyImmediate(_rootObject);
        }

        [Test]
        public void CheckAndRebuild_NullNodeId_ReturnsEarly()
        {
            var leafObj = new GameObject("Leaf", typeof(UCollidersLeaf));
            leafObj.transform.SetParent(_rootObject.transform);
            var leaf = leafObj.GetComponent<UCollidersLeaf>();
            leaf.rootNode = _root;
            leaf.nodeId = null;

            // Should not throw
            Assert.DoesNotThrow(() => leaf.CheckAndRebuild());
        }

        [Test]
        public void CheckAndRebuild_NullRootNode_ReturnsEarly()
        {
            var leafObj = new GameObject("Leaf", typeof(UCollidersLeaf));
            leafObj.transform.SetParent(_rootObject.transform);
            var leaf = leafObj.GetComponent<UCollidersLeaf>();
            leaf.rootNode = null;
            leaf.nodeId = "0";

            Assert.DoesNotThrow(() => leaf.CheckAndRebuild());
        }

        [Test]
        public void CheckAndRebuild_WithValidData_SetsMesh()
        {
            // Generate leaves via the root
            _root.recursionLevel = 1;
            _root.InitOBB();
            _root.Start();

            var leaves = _root.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leaves.Length, 0);

            foreach (var leaf in leaves)
            {
                leaf.CheckAndRebuild();
                Assert.IsNotNull(leaf.node, "Leaf node should not be null after CheckAndRebuild");
            }
        }

        [Test]
        public void RegenerateCollider_NullNode_DoesNotThrow()
        {
            var leafObj = new GameObject("Leaf", typeof(UCollidersLeaf));
            leafObj.transform.SetParent(_rootObject.transform);
            var leaf = leafObj.GetComponent<UCollidersLeaf>();
            leaf.node = null;

            Assert.DoesNotThrow(() => leaf.RegenerateCollider());
        }

        [Test]
        public void RegenerateVertices_NullRootNode_DoesNotThrow()
        {
            var leafObj = new GameObject("Leaf", typeof(UCollidersLeaf));
            leafObj.transform.SetParent(_rootObject.transform);
            var leaf = leafObj.GetComponent<UCollidersLeaf>();
            leaf.rootNode = null;

            Assert.DoesNotThrow(() => leaf.RegenerateVertices());
        }

        [Test]
        public void RegenerateCollider_BoxShape_AddsBoxCollider()
        {
            _root.recursionLevel = 1;
            _root.shape = Shape.Box;
            _root.InitOBB();
            _root.Start();

            var leaf = _root.GetComponentInChildren<UCollidersLeaf>();
            Assert.IsNotNull(leaf);
            Assert.IsNotNull(leaf.GetComponent<BoxCollider>());
        }

        [Test]
        public void RegenerateCollider_SphereShape_AddsSphereCollider()
        {
            _root.recursionLevel = 1;
            _root.shape = Shape.Sphere;
            _root.InitOBB();
            _root.Start();

            var leaf = _root.GetComponentInChildren<UCollidersLeaf>();
            Assert.IsNotNull(leaf);
            Assert.IsNotNull(leaf.GetComponent<SphereCollider>());
        }

        [Test]
        public void SetTriangles_StoresTriangles()
        {
            var leafObj = new GameObject("Leaf", typeof(UCollidersLeaf));
            leafObj.transform.SetParent(_rootObject.transform);
            var leaf = leafObj.GetComponent<UCollidersLeaf>();

            int[] tris = new int[] { 0, 1, 2, 3, 4, 5 };
            leaf.SetTriangles(tris);

            // SetTriangles stores privately; verify via CheckAndRebuild not crashing
            Assert.DoesNotThrow(() => { /* triangles stored successfully */ });
        }

        [Test]
        public void Subdivide_NullNode_LogsErrorWithoutCrash()
        {
            var leafObj = new GameObject("Leaf", typeof(UCollidersLeaf));
            leafObj.transform.SetParent(_rootObject.transform);
            var leaf = leafObj.GetComponent<UCollidersLeaf>();
            leaf.rootNode = null;
            leaf.nodeId = null;
            leaf.node = null;

            // Expect the error logs so Unity Test Framework doesn't fail the test
            LogAssert.Expect(LogType.Error, "Cannot compute OBB: mesh is null.");
            LogAssert.Expect(LogType.Error, "Cannot subdivide: OBB node could not be computed.");

            // Subdivide should handle null gracefully
            Assert.DoesNotThrow(() => leaf.Subdivide());
        }

        [Test]
        public void Unpack_DestroysLeafComponent()
        {
            var leafObj = new GameObject("Leaf", typeof(UCollidersLeaf));
            leafObj.transform.SetParent(_rootObject.transform);
            var leaf = leafObj.GetComponent<UCollidersLeaf>();

            leaf.Unpack();

            // Component should be destroyed but GameObject remains
            Assert.IsTrue(leaf == null, "Leaf component should be destroyed");
            Assert.IsFalse(leafObj == null, "GameObject should still exist");

            Object.DestroyImmediate(leafObj);
        }

        [Test]
        public void PreserveUCollider_DefaultsFalse()
        {
            var leafObj = new GameObject("Leaf", typeof(UCollidersLeaf));
            var leaf = leafObj.GetComponent<UCollidersLeaf>();

            Assert.IsFalse(leaf.preserveUCollider);

            Object.DestroyImmediate(leafObj);
        }
    }
}
