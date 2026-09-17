using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UColliders;

namespace UColliders.Demos
{
    /// <summary>
    /// Demonstrates runtime collider generation: sync, async, and the completion callback.
    /// Spawns objects on a conveyor belt / timed spawner pattern.
    /// Attach to an empty GameObject in the scene.
    /// </summary>
    public class RuntimeGenerationDemo : MonoBehaviour
    {
        [Header("Prefab")]
        [Tooltip("Mesh prefab to spawn (must have MeshFilter + MeshRenderer).")]
        public GameObject prefab;

        [Header("UI")]
        [Tooltip("Label showing generation status.")]
        public Text statusLabel;

        [Header("Settings")]
        [Tooltip("Seconds between spawns.")]
        public float spawnInterval = 4f;

        [Tooltip("Height to spawn objects.")]
        public float spawnHeight = 5f;

        [Tooltip("Horizontal spacing between sync and async columns.")]
        public float columnSpacing = 4f;

        int syncCount;
        int asyncCount;
        bool spawnSync = true;

        void Start()
        {
            if (prefab == null) {
                Debug.LogWarning("RuntimeGenerationDemo: No prefab assigned.");
                return;
            }
            InvokeRepeating(nameof(SpawnNext), 1f, spawnInterval);
            UpdateStatus("Ready. Spawning objects...");
        }

        void SpawnNext()
        {
            if (spawnSync)
                SpawnSync();
            else
                SpawnAsync();
            spawnSync = !spawnSync;
        }

        void SpawnSync()
        {
            float x = -columnSpacing / 2f;
            Vector3 pos = new Vector3(x, spawnHeight, syncCount * 2f);
            GameObject go = Instantiate(prefab, pos, Quaternion.identity);
            go.name = $"Sync #{syncCount}";
            go.AddComponent<Rigidbody>();

            ConvexDecomposer cd = go.AddComponent<ConvexDecomposer>();
            cd.previewColor = ConvexDecomposer.PreviewMode.None;

            float startTime = Time.realtimeSinceStartup;
            cd.OnCollidersGenerated += (d) => {
                float elapsed = (Time.realtimeSinceStartup - startTime) * 1000f;
                UpdateStatus($"Sync: {d.name} done in {elapsed:F1}ms ({d.CountHulls()} hulls)");
            };
            cd.RegenerateColliders();

            syncCount++;
            Destroy(go, 20f);
        }

        void SpawnAsync()
        {
            StartCoroutine(SpawnAsyncCoroutine());
        }

        IEnumerator SpawnAsyncCoroutine()
        {
            float x = columnSpacing / 2f;
            Vector3 pos = new Vector3(x, spawnHeight, asyncCount * 2f);
            GameObject go = Instantiate(prefab, pos, Quaternion.identity);
            go.name = $"Async #{asyncCount}";

            // Add Rigidbody as kinematic until generation completes
            Rigidbody rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;

            ConvexDecomposer cd = go.AddComponent<ConvexDecomposer>();
            cd.previewColor = ConvexDecomposer.PreviewMode.None;

            float startTime = Time.realtimeSinceStartup;
            UpdateStatus($"Async: generating {go.name}...");

            cd.OnCollidersGenerated += (d) => {
                float elapsed = (Time.realtimeSinceStartup - startTime) * 1000f;
                UpdateStatus($"Async: {d.name} done in {elapsed:F1}ms ({d.CountHulls()} hulls)");
                // Enable physics after generation
                rb.isKinematic = false;
            };

            yield return StartCoroutine(cd.GenerateCollidersAsync());

            asyncCount++;
            Destroy(go, 20f);
        }

        void UpdateStatus(string message)
        {
            Debug.Log("RuntimeDemo: " + message);
            if (statusLabel != null)
                statusLabel.text = message;
        }
    }
}
