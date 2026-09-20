using UnityEngine;

namespace WaveByWave.Items
{
    public static class ItemVisualUtility
    {
        public const string PreferredChildName = "__PreferredVisual";

        public static GameObject InstantiatePresentation(GameObject prefab, Transform parent, string instanceName = null)
        {
            if (prefab == null) return null;

            // ItemDefinition points at the complete network item prefab. Only its explicitly
            // authored visual is cloned, so held items never contain nested NetworkObjects.
            if (prefab.TryGetComponent<WorldItem>(out var worldItem) && worldItem.AuthoredVisual != null)
            {
                var presentation = new GameObject(string.IsNullOrWhiteSpace(instanceName)
                    ? prefab.name + " Presentation" : instanceName);
                presentation.transform.SetParent(parent, false);
                presentation.transform.localPosition = prefab.transform.localPosition;
                presentation.transform.localRotation = prefab.transform.localRotation;
                presentation.transform.localScale = prefab.transform.localScale;

                var visual = Object.Instantiate(worldItem.AuthoredVisual, presentation.transform, false);
                visual.name = worldItem.AuthoredVisual.name;
                visual.SetActive(true);
                return presentation;
            }

            var instance = Object.Instantiate(prefab, parent, false);
            if (!string.IsNullOrWhiteSpace(instanceName)) instance.name = instanceName;
            return instance;
        }

        public static Transform GetBoundsRoot(GameObject prefab)
        {
            if (prefab == null)
                return null;
            if (prefab.TryGetComponent<WorldItem>(out var worldItem) && worldItem.AuthoredVisual != null)
                return worldItem.AuthoredVisual.transform;
            return prefab.transform;
        }
    }
}
