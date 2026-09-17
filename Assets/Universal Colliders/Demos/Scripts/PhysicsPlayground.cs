using UnityEngine;
using UColliders;

namespace UColliders.Demos
{
    /// <summary>
    /// Spawns concave Rigidbodies that fall and interact with physics.
    /// Demonstrates that non-convex colliders work on dynamic Rigidbodies.
    /// Attach to an empty GameObject in the scene.
    /// </summary>
    public class PhysicsPlayground : MonoBehaviour
    {
        [Header("Prefabs (must have MeshFilter + MeshRenderer)")]
        [Tooltip("Mesh prefabs to spawn. They do NOT need collider components — they will be added at runtime.")]
        public GameObject[] prefabs;

        [Header("Spawn Settings")]
        [Tooltip("How many objects to spawn at start.")]
        public int initialCount = 10;

        [Tooltip("Seconds between additional spawns. 0 = no continuous spawning.")]
        public float spawnInterval = 3f;

        [Tooltip("Maximum number of objects alive at once.")]
        public int maxObjects = 30;

        [Tooltip("Height above origin to spawn objects.")]
        public float spawnHeight = 10f;

        [Tooltip("Horizontal spread radius.")]
        public float spawnRadius = 3f;

        [Header("Collider Settings")]
        [Tooltip("Use convex decomposition (CoACD) instead of OBB tree.")]
        public bool useConvexDecomposition = false;
        public int obbRecursionLevel = 5;

        int aliveCount;

        void Start()
        {
            if (prefabs == null || prefabs.Length == 0) {
                Debug.LogWarning("PhysicsPlayground: No prefabs assigned.");
                return;
            }

            for (int i = 0; i < initialCount; i++)
                SpawnObject();

            if (spawnInterval > 0)
                InvokeRepeating(nameof(SpawnNext), spawnInterval, spawnInterval);
        }

        void SpawnNext()
        {
            if (aliveCount < maxObjects)
                SpawnObject();
        }

        void SpawnObject()
        {
            GameObject prefab = prefabs[Random.Range(0, prefabs.Length)];
            Vector2 offset = Random.insideUnitCircle * spawnRadius;
            Vector3 position = new Vector3(offset.x, spawnHeight, offset.y);
            Quaternion rotation = Random.rotation;

            GameObject go = Instantiate(prefab, position, rotation);
            go.name = prefab.name + " (Physics)";

            // Add Rigidbody if missing
            if (go.GetComponent<Rigidbody>() == null)
                go.AddComponent<Rigidbody>();

            // Generate colliders
            if (useConvexDecomposition) {
                ConvexDecomposer cd = go.GetComponent<ConvexDecomposer>();
                if (cd == null)
                    cd = go.AddComponent<ConvexDecomposer>();
                cd.previewColor = ConvexDecomposer.PreviewMode.None;
                cd.RegenerateColliders();
            } else {
                UCollidersRoot root = go.GetComponent<UCollidersRoot>();
                if (root == null)
                    root = go.AddComponent<UCollidersRoot>();
                root.recursionLevel = obbRecursionLevel;
                root.previewColor = UCollidersRoot.CollidersPreviewColor.None;
                root.RegenerateColliders();
            }

            aliveCount++;

            // Destroy objects that fall too far
            Destroy(go, 30f);
            Invoke(nameof(DecrementCount), 30f);
        }

        void DecrementCount() => aliveCount--;
    }
}
