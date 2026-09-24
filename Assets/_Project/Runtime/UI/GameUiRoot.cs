using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace WaveByWave.UI
{
    // HUD and menus are the actual scene objects. Only repeated rows and world bars are cloned.
    [DefaultExecutionOrder(-10000)]
    [DisallowMultipleComponent]
    public sealed class GameUiRoot : MonoBehaviour
    {
        private static GameUiRoot _instance;
        private readonly Dictionary<GameObject, Object> _owners = new();
        private bool _initialized;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetInstance() => _instance = null;

        private void Awake()
        {
            if (!Application.IsPlaying(gameObject)) return;
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            Initialize();
        }

        private void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            _instance = this;
            transform.SetParent(null, false);
            DontDestroyOnLoad(gameObject);
            foreach (Transform group in transform)
            {
                foreach (Transform view in group) view.gameObject.SetActive(false);
                group.gameObject.SetActive(true);
            }
        }

        public static GameUiRoot EnsureInstance()
        {
            if (_instance != null) return _instance;
            foreach (var candidate in FindObjectsByType<GameUiRoot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!Application.IsPlaying(candidate.gameObject)) continue;
                candidate.Initialize();
                candidate.gameObject.SetActive(true);
                return candidate;
            }
            var prefab = Resources.Load<GameObject>("UI/GameUI");
            if (prefab == null) return null;
            var root = Instantiate(prefab).GetComponent<GameUiRoot>();
            if (root != null) { root.Initialize(); root.gameObject.SetActive(true); }
            return root;
        }

        public GameObject FindView(string key) => transform.Find(key)?.gameObject;

        public GameObject CreateView(string key, Transform parent = null, Object owner = null)
        {
            var view = FindView(key);
            if (view == null) return null;
            if (key.StartsWith("Elements/") || key.StartsWith("World/") && key != "World/ItemTooltip")
            {
                var copy = Instantiate(view, parent, false);
                copy.name = view.name;
                copy.SetActive(true);
                return copy;
            }
            if (!_owners.TryGetValue(view, out var previousOwner) || previousOwner != owner)
                ClearBindings(view);
            _owners[view] = owner;
            view.transform.parent.gameObject.SetActive(true);
            view.SetActive(true);
            return view;
        }

        public bool Owns(GameObject view, Object owner) =>
            _owners.TryGetValue(view, out var current) && current == owner;

        public void ReleaseView(GameObject view, Object owner)
        {
            // A departing scene/player must not hide a view already rebound to its replacement.
            if (!Owns(view, owner)) return;
            _owners.Remove(view);
            ClearBindings(view);
            view.SetActive(false);
        }

        private static void ClearBindings(GameObject view)
        {
            foreach (var button in view.GetComponentsInChildren<Button>(true)) button.onClick.RemoveAllListeners();
            foreach (var slider in view.GetComponentsInChildren<Slider>(true)) slider.onValueChanged.RemoveAllListeners();
            // RemoveAllListeners preserves callbacks authored in the prefab Inspector.
        }

        private void OnDestroy() { if (_instance == this) _instance = null; }
    }
}
