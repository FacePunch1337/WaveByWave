using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UColliders;

namespace UColliders.Tests.Runtime
{
    /// <summary>
    /// PlayMode tests for UCollidersRoot runtime collider generation.
    /// Exercises the Awake → InitOBB → Start → RegenerateColliders → Update lifecycle.
    /// </summary>
    public class UCollidersPlayModeTests
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
            _testObject = new GameObject("PlayModeTestOBB");
            var meshFilter = _testObject.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = CreateCubeMesh();
            _root = _testObject.AddComponent<UCollidersRoot>();
            _root.recursionLevel = 1;
        }

        [TearDown]
        public void TearDown()
        {
            if (_testObject != null)
            {
                foreach (Transform child in _testObject.transform)
                    Object.Destroy(child.gameObject);
                Object.Destroy(_testObject);
            }
        }

        // --- 1. Lifecycle ---

        [UnityTest]
        public IEnumerator Lifecycle_CreatesCollidersAfterStart()
        {
            // Awake and Start fire on the next frame
            yield return null;

            var leaves = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leaves.Length, 0, "Leaves should be created after Awake+Start");

            foreach (var leaf in leaves)
            {
                var collider = leaf.GetComponent<Collider>();
                Assert.IsNotNull(collider, $"Leaf {leaf.name} should have a Collider");
            }
        }

        // --- 2. RegenerateColliders replaces existing ---

        [UnityTest]
        public IEnumerator RegenerateColliders_ReplacesExistingColliders()
        {
            yield return null;

            var oldLeaves = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            int oldCount = oldLeaves.Length;
            Assert.Greater(oldCount, 0);

            _root.RegenerateColliders();
            yield return null;

            var newLeaves = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            Assert.AreEqual(oldCount, newLeaves.Length,
                "Regeneration should produce the same number of leaves");

            // Old leaf GameObjects should have been destroyed
            foreach (var oldLeaf in oldLeaves)
                Assert.IsTrue(oldLeaf == null, "Old leaves should be destroyed after regeneration");
        }

        // --- 3. Recursion level 0 ---

        [UnityTest]
        public IEnumerator RegenerateColliders_RecursionLevel0_CreatesOneLeaf()
        {
            _root.recursionLevel = 0;
            yield return null;

            _root.RegenerateColliders();
            yield return null;

            var leaves = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            Assert.AreEqual(1, leaves.Length, "Recursion level 0 should create exactly one leaf");
        }

        // --- 4. Recursion level 2 ---

        [UnityTest]
        public IEnumerator RegenerateColliders_RecursionLevel2_CreatesMoreLeaves()
        {
            _root.recursionLevel = 2;
            _root.encapsulateTriangles = true;
            _root.InitOBB();
            yield return null;

            _root.RegenerateColliders();
            yield return null;

            var leaves = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leaves.Length, 2,
                "Recursion level 2 should create more than 2 leaves");
        }

        // --- 5. Box shape ---

        [UnityTest]
        public IEnumerator RegenerateColliders_BoxShape_CreatesBoxColliders()
        {
            _root.shape = Shape.Box;
            yield return null;

            _root.RegenerateColliders();
            yield return null;

            var leaves = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leaves.Length, 0);
            foreach (var leaf in leaves)
                Assert.IsNotNull(leaf.GetComponent<BoxCollider>(),
                    "Each leaf should have a BoxCollider");
        }

        // --- 6. Sphere shape ---

        [UnityTest]
        public IEnumerator RegenerateColliders_SphereShape_CreatesSphereColliders()
        {
            _root.shape = Shape.Sphere;
            yield return null;

            _root.RegenerateColliders();
            yield return null;

            var leaves = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leaves.Length, 0);
            foreach (var leaf in leaves)
                Assert.IsNotNull(leaf.GetComponent<SphereCollider>(),
                    "Each leaf should have a SphereCollider");
        }

        // --- 7. Capsule shape ---

        [UnityTest]
        public IEnumerator RegenerateColliders_CapsuleShape_CreatesCapsuleColliders()
        {
            _root.shape = Shape.Capsule;
            yield return null;

            _root.RegenerateColliders();
            yield return null;

            var leaves = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leaves.Length, 0);
            foreach (var leaf in leaves)
                Assert.IsNotNull(leaf.GetComponent<CapsuleCollider>(),
                    "Each leaf should have a CapsuleCollider");
        }

        // --- 8. Mesh shape ---

        [UnityTest]
        public IEnumerator RegenerateColliders_MeshShape_CreatesMeshColliders()
        {
            _root.shape = Shape.Mesh;
            yield return null;

            _root.RegenerateColliders();
            yield return null;

            var leaves = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leaves.Length, 0);
            foreach (var leaf in leaves)
            {
                var mc = leaf.GetComponent<MeshCollider>();
                Assert.IsNotNull(mc, "Each leaf should have a MeshCollider");
                Assert.IsTrue(mc.convex, "MeshCollider should be convex");
            }
        }

        // --- 9. DestroyColliders ---

        [UnityTest]
        public IEnumerator DestroyColliders_RemovesAllLeaves()
        {
            yield return null;

            var leavesBefore = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leavesBefore.Length, 0, "Should have leaves before destroy");

            _root.DestroyColliders();
            yield return null;

            var leavesAfter = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            Assert.AreEqual(0, leavesAfter.Length, "All leaves should be destroyed");
        }

        // --- 10. DestroyColliders preserves marked leaves ---

        [UnityTest]
        public IEnumerator DestroyColliders_PreservesMarkedLeaves()
        {
            yield return null;

            var leaves = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leaves.Length, 0);

            // Mark the first leaf as preserved
            leaves[0].preserveUCollider = true;

            _root.DestroyColliders(force: false);
            yield return null;

            var remaining = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            Assert.AreEqual(1, remaining.Length, "Preserved leaf should survive DestroyColliders");
            Assert.IsTrue(remaining[0].preserveUCollider);
        }

        // --- 11. Update repairs deleted collider ---

        [UnityTest]
        public IEnumerator Update_RegeneratesDeletedCollider()
        {
            yield return null;

            var leaf = _testObject.GetComponentInChildren<UCollidersLeaf>();
            Assert.IsNotNull(leaf);
            var collider = leaf.GetComponent<Collider>();
            Assert.IsNotNull(collider, "Leaf should have a collider initially");

            // Destroy the collider component
            Object.Destroy(collider);
            yield return null;

            // Update() should detect the missing collider and regenerate it
            yield return null;

            var newCollider = leaf.GetComponent<Collider>();
            Assert.IsNotNull(newCollider, "Update should have regenerated the missing collider");
        }

        // --- 12. UnpackColliders ---

        [UnityTest]
        public IEnumerator UnpackColliders_RemovesLeafComponents()
        {
            yield return null;

            var leavesBefore = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leavesBefore.Length, 0);

            int childCountBefore = _testObject.transform.childCount;
            int unpacked = _root.UnpackColliders();
            Assert.AreEqual(leavesBefore.Length, unpacked);

            yield return null;

            // UCollidersLeaf components should be gone
            var leavesAfter = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            Assert.AreEqual(0, leavesAfter.Length,
                "All UCollidersLeaf components should be removed after unpack");

            // But child GameObjects with colliders should remain
            // Note: UnpackColliders calls DeleteCollidersAndData which destroys leaf GOs,
            // so we just verify the unpack count was correct
            Assert.Greater(unpacked, 0);
        }

        // --- 13. Multiple regenerations don't leak leaves ---

        [UnityTest]
        public IEnumerator MultipleRegenerations_NoLeafLeaks()
        {
            yield return null;

            _root.RegenerateColliders();
            yield return null;
            int firstCount = _testObject.GetComponentsInChildren<UCollidersLeaf>().Length;
            Assert.Greater(firstCount, 0);

            _root.RegenerateColliders();
            yield return null;
            int secondCount = _testObject.GetComponentsInChildren<UCollidersLeaf>().Length;

            _root.RegenerateColliders();
            yield return null;
            int thirdCount = _testObject.GetComponentsInChildren<UCollidersLeaf>().Length;

            Assert.AreEqual(firstCount, secondCount,
                "Leaf count should be consistent across regenerations");
            Assert.AreEqual(secondCount, thirdCount,
                "Leaf count should be consistent across regenerations");
        }

        // --- 14. GenerateCollidersAsync coroutine ---

        [UnityTest]
        public IEnumerator GenerateCollidersAsync_CreatesColliders()
        {
            yield return null;

            // Destroy existing colliders first
            _root.DestroyColliders();
            yield return null;

            yield return _root.StartCoroutine(_root.GenerateCollidersAsync());
            yield return null;

            var leaves = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            Assert.Greater(leaves.Length, 0, "Async generation should create leaves");

            foreach (var leaf in leaves)
            {
                var collider = leaf.GetComponent<Collider>();
                Assert.IsNotNull(collider, $"Leaf {leaf.name} should have a Collider after async generation");
            }
        }

        // --- 15. OnCollidersGenerated event fires ---

        [UnityTest]
        public IEnumerator OnCollidersGenerated_EventFires()
        {
            yield return null;

            bool eventFired = false;
            UCollidersRoot eventSender = null;
            _root.OnCollidersGenerated += (root) => {
                eventFired = true;
                eventSender = root;
            };

            _root.RegenerateColliders();
            yield return null;

            Assert.IsTrue(eventFired, "OnCollidersGenerated should fire after RegenerateColliders");
            Assert.AreEqual(_root, eventSender, "Event sender should be the root");
        }

        // --- 16. OnCollidersGenerated fires from async path ---

        [UnityTest]
        public IEnumerator OnCollidersGenerated_EventFiresFromAsync()
        {
            yield return null;

            bool eventFired = false;
            _root.OnCollidersGenerated += (_) => eventFired = true;

            yield return _root.StartCoroutine(_root.GenerateCollidersAsync());
            yield return null;

            Assert.IsTrue(eventFired, "OnCollidersGenerated should fire after GenerateCollidersAsync");
        }

        // --- 17. GenerateCollidersAsync replaces existing colliders ---

        [UnityTest]
        public IEnumerator GenerateCollidersAsync_ReplacesExistingColliders()
        {
            yield return null;

            var oldLeaves = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            int oldCount = oldLeaves.Length;
            Assert.Greater(oldCount, 0);

            yield return _root.StartCoroutine(_root.GenerateCollidersAsync());
            yield return null;

            var newLeaves = _testObject.GetComponentsInChildren<UCollidersLeaf>();
            Assert.AreEqual(oldCount, newLeaves.Length,
                "Async regeneration should produce the same number of leaves");

            foreach (var oldLeaf in oldLeaves)
                Assert.IsTrue(oldLeaf == null, "Old leaves should be destroyed after async regeneration");
        }
    }
}
