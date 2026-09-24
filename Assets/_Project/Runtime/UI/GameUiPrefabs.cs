using UnityEngine;

namespace WaveByWave.UI
{
    // Use the authored scene hierarchy; repeated world widgets and rows get separate instances.
    public static class GameUiPrefabs
    {
        public static GameObject Create(string key, Transform parent = null, Object owner = null)
        {
            if (Application.isPlaying)
            {
                var layout = GameUiRoot.EnsureInstance();
                var view = layout != null ? layout.CreateView(key, parent, owner) : null;
                if (view != null) return view;
            }
            var prefab = Resources.Load<GameObject>("UI/" + key);
            return prefab != null ? Object.Instantiate(prefab, parent, false) : null;
        }
        public static bool IsOwnedBy(GameObject view, Object owner)
        {
            if (view == null) return false;
            var layout = view.GetComponentInParent<GameUiRoot>(true);
            return layout == null || layout.Owns(view, owner);
        }
        public static void Release(GameObject view, Object owner = null)
        {
            if (view == null) return;
            var layout = view.GetComponentInParent<GameUiRoot>(true);
            if (layout != null) layout.ReleaseView(view, owner);
            else Object.Destroy(view);
        }
        public static void Persist(GameObject view)
        {
            if (!Application.isPlaying || view == null || view.GetComponentInParent<GameUiRoot>() != null) return;
            Object.DontDestroyOnLoad(view);
        }
        public static T Find<T>(GameObject root, string path) where T : Component =>
            root.transform.Find(path).GetComponent<T>();
    }
}
