using UnityEngine;

namespace WaveByWave.Items
{
    public static class ItemVisualUtility
    {
        public const string PreferredChildName = "__PreferredVisual";

        public static void SelectPreferredChild(GameObject instance)
        {
            if (instance == null)
                return;
            var preferred = instance.transform.Find(PreferredChildName);
            if (preferred == null)
                return;
            foreach (Transform child in instance.transform)
                child.gameObject.SetActive(child == preferred);
        }

        public static Transform GetBoundsRoot(GameObject prefab)
        {
            if (prefab == null)
                return null;
            return prefab.transform.Find(PreferredChildName) ?? prefab.transform;
        }
    }
}
