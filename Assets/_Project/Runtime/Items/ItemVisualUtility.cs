using UnityEngine;

namespace WaveByWave.Items
{
    public static class ItemVisualUtility
    {
        public const string PreferredChildName = "__PreferredVisual";

        public static GameObject InstantiatePresentation(GameObject prefab, Transform parent, string instanceName = null)
        {
            if (prefab == null) return null;
            var sourceFilter = prefab.GetComponentInChildren<MeshFilter>(true);
            var sourceRenderer = sourceFilter != null ? sourceFilter.GetComponent<MeshRenderer>() : null;
            if (sourceFilter == null || sourceFilter.sharedMesh == null || sourceRenderer == null)
            {
                Debug.LogError($"Item prefab '{prefab.name}' requires one MeshFilter and MeshRenderer.", prefab);
                return null;
            }

            // The item prefab itself is the single source of truth. Clone only its authored
            // mesh presentation; NetworkObject, collider and gameplay scripts never get nested.
            var presentation = new GameObject(string.IsNullOrWhiteSpace(instanceName)
                ? prefab.name + " Presentation" : instanceName, typeof(MeshFilter), typeof(MeshRenderer));
            presentation.transform.SetParent(parent, false);
            var authored = sourceFilter.transform.localToWorldMatrix;
            presentation.transform.localPosition = authored.GetColumn(3);
            presentation.transform.localRotation = authored.rotation;
            presentation.transform.localScale = authored.lossyScale;
            presentation.GetComponent<MeshFilter>().sharedMesh = sourceFilter.sharedMesh;
            var renderer = presentation.GetComponent<MeshRenderer>();
            renderer.sharedMaterials = sourceRenderer.sharedMaterials;
            renderer.shadowCastingMode = sourceRenderer.shadowCastingMode;
            renderer.receiveShadows = sourceRenderer.receiveShadows;
            renderer.lightProbeUsage = sourceRenderer.lightProbeUsage;
            renderer.reflectionProbeUsage = sourceRenderer.reflectionProbeUsage;
            return presentation;
        }

        public static Transform GetBoundsRoot(GameObject prefab)
        {
            if (prefab == null)
                return null;
            return prefab.transform;
        }
    }
}
