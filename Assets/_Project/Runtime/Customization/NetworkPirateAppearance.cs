using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using WaveByWave.Combat;
using WaveByWave.Player;

namespace WaveByWave.Customization
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject), typeof(PlayerAnimationSync))]
    public sealed class NetworkPirateAppearance : NetworkBehaviour
    {
        private const string SaveKey = "wave_by_wave_pirate_appearance_v1";

        [Header("Base pirate")]
        [SerializeField] private Transform visualRoot;
        [Tooltip("The pirate body authored directly inside the Player prefab.")]
        [SerializeField] private Transform pirateRoot;
        [SerializeField] private Material[] pirateMaterials;
        [Header("Head")]
        [SerializeField] private GameObject[] hairPrefabs;
        [SerializeField] private GameObject[] bandanaPrefabs;
        [SerializeField] private GameObject[] hatPrefabs;
        [Header("Clothing")]
        [SerializeField] private GameObject[] coatPrefabs;

        private readonly NetworkVariable<PirateAppearanceState> _state = new();
        private PlayerAnimationSync _animationSync;
        private NetworkHealth _health;
        private RuntimeAnimatorController _locomotionController;
        private Transform _appearanceRoot;
        private Transform _pirateRoot;
        private Transform _headSlot;
        private Animator _pirateAnimator;
        private Renderer[] _bodyRenderers = Array.Empty<Renderer>();
        private GameObject _hair;
        private GameObject _bandana;
        private GameObject _hat;
        private GameObject _coat;
        private PirateAppearanceState _displayedState;

        public event Action<PirateAppearanceState> Changed;
        public PirateAppearanceState State => IsOwner ? _displayedState : _state.Value;

        private void Awake()
        {
            _animationSync = GetComponent<PlayerAnimationSync>();
            _health = GetComponent<NetworkHealth>();
            visualRoot ??= transform.Find("Visual");
            if (visualRoot == null)
                visualRoot = transform;
            var oldAnimator = visualRoot.GetComponentInChildren<Animator>(true);
            _locomotionController = oldAnimator != null ? oldAnimator.runtimeAnimatorController : null;
            BuildBasePirate();
        }

        public override void OnNetworkSpawn()
        {
            _state.OnValueChanged += OnStateChanged;
            Apply(_state.Value);
            if (IsOwner)
                SubmitSavedState();
        }

        public override void OnNetworkDespawn()
        {
            _state.OnValueChanged -= OnStateChanged;
        }

        public int GetOptionCount(PirateCustomizationCategory category) => category switch
        {
            PirateCustomizationCategory.Pirate => Mathf.Max(1, pirateMaterials?.Length ?? 0),
            PirateCustomizationCategory.Hair => 1 + (hairPrefabs?.Length ?? 0),
            PirateCustomizationCategory.Bandana => 1 + (bandanaPrefabs?.Length ?? 0),
            PirateCustomizationCategory.Hat => 1 + (hatPrefabs?.Length ?? 0),
            PirateCustomizationCategory.Coat => 1 + (coatPrefabs?.Length ?? 0),
            _ => 1
        };

        public string GetOptionName(PirateCustomizationCategory category, int index)
        {
            if (category != PirateCustomizationCategory.Pirate && index == 0)
                return "Нет";
            return category switch
            {
                PirateCustomizationCategory.Pirate => $"Пират {index + 1}",
                PirateCustomizationCategory.Hair => $"Причёска {index}",
                PirateCustomizationCategory.Bandana => $"Бандана {index}",
                PirateCustomizationCategory.Hat => $"Шляпа {index}",
                PirateCustomizationCategory.Coat => $"Пальто {index}",
                _ => "—"
            };
        }

        public void Cycle(PirateCustomizationCategory category, int direction)
        {
            if (!IsOwner || direction == 0)
                return;
            var next = _displayedState;
            var count = GetOptionCount(category);
            var value = (next.Get(category) + Math.Sign(direction) + count) % count;
            next.Set(category, (byte)value);
            // Apply immediately for a responsive local preview; the server validates and
            // replicates the same state to every other client.
            Apply(next);
            Changed?.Invoke(next);
            SetAppearanceServerRpc(next);
        }

        public void SaveLocal()
        {
            if (!IsOwner)
                return;
            var state = _displayedState;
            PlayerPrefs.SetString(SaveKey,
                $"{state.Pirate},{state.Hair},{state.Bandana},{state.Hat},{state.Coat}");
            PlayerPrefs.Save();
        }

        [ServerRpc]
        private void SetAppearanceServerRpc(PirateAppearanceState requested)
        {
            _state.Value = Validate(requested);
        }

        private void SubmitSavedState()
        {
            var value = PlayerPrefs.GetString(SaveKey, string.Empty);
            var parts = value.Split(',');
            if (parts.Length != 5)
                return;
            var state = new PirateAppearanceState();
            if (!byte.TryParse(parts[0], out state.Pirate) || !byte.TryParse(parts[1], out state.Hair) ||
                !byte.TryParse(parts[2], out state.Bandana) || !byte.TryParse(parts[3], out state.Hat) ||
                !byte.TryParse(parts[4], out state.Coat))
                return;
            SetAppearanceServerRpc(state);
        }

        private PirateAppearanceState Validate(PirateAppearanceState state)
        {
            foreach (PirateCustomizationCategory category in Enum.GetValues(typeof(PirateCustomizationCategory)))
                state.Set(category, (byte)Mathf.Clamp(state.Get(category), 0, GetOptionCount(category) - 1));
            return state;
        }

        private void OnStateChanged(PirateAppearanceState previous, PirateAppearanceState current)
        {
            Apply(current);
            Changed?.Invoke(current);
        }

        private void BuildBasePirate()
        {
            if (_appearanceRoot != null)
                return;

            _appearanceRoot = visualRoot;
            _pirateRoot = pirateRoot != null ? pirateRoot : visualRoot.Find("Pirate Body");
            if (_pirateRoot == null)
            {
                Debug.LogError("Player prefab is missing its authored Pirate Body visual.", this);
                return;
            }

            _pirateAnimator = _pirateRoot.GetComponentInChildren<Animator>(true);
            if (_pirateAnimator != null && _locomotionController != null)
                _pirateAnimator.runtimeAnimatorController = _locomotionController;
            _animationSync?.SetAnimator(_pirateAnimator);
            _headSlot = FindDeep(_pirateRoot, "Head_slot") ??
                        (_pirateAnimator != null && _pirateAnimator.isHuman
                            ? _pirateAnimator.GetBoneTransform(HumanBodyBones.Head)
                            : _pirateRoot);
            _bodyRenderers = _pirateRoot.GetComponentsInChildren<Renderer>(true);
            SetOwnerHiddenLayer();
            _health?.RefreshPresentationRenderers();
        }

        private void Apply(PirateAppearanceState state)
        {
            _displayedState = state;
            BuildBasePirate();
            if (_pirateRoot == null)
                return;
            // End a hit flash before changing materials so its restore pass cannot
            // overwrite a customization selected during the short flash window.
            _health?.RefreshPresentationRenderers();

            if (pirateMaterials != null && pirateMaterials.Length > 0)
            {
                var material = pirateMaterials[Mathf.Clamp(state.Pirate, 0, pirateMaterials.Length - 1)];
                if (material != null)
                    foreach (var renderer in _bodyRenderers)
                    {
                        var materials = renderer.sharedMaterials;
                        for (var i = 0; i < materials.Length; i++)
                            materials[i] = material;
                        renderer.sharedMaterials = materials;
                    }
            }

            ReplaceStatic(ref _hair, hairPrefabs, state.Hair, _headSlot);
            ReplaceStatic(ref _bandana, bandanaPrefabs, state.Bandana, _headSlot);
            ReplaceStatic(ref _hat, hatPrefabs, state.Hat, _headSlot);
            ReplaceSkinned(ref _coat, coatPrefabs, state.Coat);
            SetOwnerHiddenLayer();
            _health?.RefreshPresentationRenderers();
        }

        private void ReplaceStatic(ref GameObject current, GameObject[] prefabs, int index, Transform parent)
        {
            if (current != null)
                Destroy(current);
            current = null;
            if (index <= 0 || prefabs == null || index > prefabs.Length || prefabs[index - 1] == null || parent == null)
                return;
            current = Instantiate(prefabs[index - 1], parent, false);
            current.name = prefabs[index - 1].name;
            current.transform.localPosition = new Vector3(-0.005f, 0f, 0f);
            current.transform.localRotation = new Quaternion(-0.5f, 0.5f, 0.5f, 0.5f);
            current.transform.localScale = Vector3.one;
            // The source pack authors all head-slot attachments in Blender axes. Hair
            // has one more corrected child, while hats and bandanas render on the root.
            if (current.GetComponent<Renderer>() != null)
                current.transform.localPosition = new Vector3(0f, 0.015f, 0.003f);
            DisablePhysics(current);
        }

        private void ReplaceSkinned(ref GameObject current, GameObject[] prefabs, int index)
        {
            if (current != null)
                Destroy(current);
            current = null;
            if (index <= 0 || prefabs == null || index > prefabs.Length || prefabs[index - 1] == null)
                return;
            current = Instantiate(prefabs[index - 1], _appearanceRoot, false);
            current.name = prefabs[index - 1].name;
            current.transform.localPosition = Vector3.zero;
            current.transform.localRotation = Quaternion.identity;
            current.transform.localScale = Vector3.one;
            DisablePhysics(current);
            RetargetSkinnedMeshes(current);
        }

        private void RetargetSkinnedMeshes(GameObject attachment)
        {
            var bones = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (var transformInBody in _pirateRoot.GetComponentsInChildren<Transform>(true))
                bones.TryAdd(transformInBody.name, transformInBody);
            foreach (var renderer in attachment.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mapped = new Transform[renderer.bones.Length];
                for (var i = 0; i < mapped.Length; i++)
                    mapped[i] = renderer.bones[i] != null && bones.TryGetValue(renderer.bones[i].name, out var bone)
                        ? bone : renderer.bones[i];
                renderer.bones = mapped;
                if (renderer.rootBone != null && bones.TryGetValue(renderer.rootBone.name, out var root))
                    renderer.rootBone = root;
            }
            foreach (var animator in attachment.GetComponentsInChildren<Animator>(true))
                animator.enabled = false;
        }

        private void SetOwnerHiddenLayer()
        {
            if (!IsSpawned || !IsOwner)
                return;
            var layer = LayerMask.NameToLayer(NetworkPlayerController.LocalBodyLayerName);
            if (layer < 0 || _appearanceRoot == null)
                return;
            foreach (var child in _appearanceRoot.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = layer;
        }

        private static void DisablePhysics(GameObject root)
        {
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
            foreach (var body in root.GetComponentsInChildren<Rigidbody>(true))
                body.isKinematic = true;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == name)
                    return child;
            return null;
        }
    }
}
