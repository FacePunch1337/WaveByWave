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
        [Header("Default appearance")]
        [Tooltip("Used when this client has no locally saved wardrobe selection.")]
        [SerializeField] private PirateAppearanceState defaultAppearance;
        [SerializeField] private bool loadSavedAppearance = true;
        [Header("Head")]
        [SerializeField] private GameObject[] hairPrefabs;
        [SerializeField] private GameObject[] bandanaPrefabs;
        [SerializeField] private GameObject[] hatPrefabs;
        [SerializeField] private GameObject[] eyePatchPrefabs;
        [SerializeField] private GameObject[] earringPrefabs;
        [Header("Clothing")]
        [SerializeField] private GameObject[] coatPrefabs;
        [SerializeField] private GameObject[] gloveLeftPrefabs;
        [SerializeField] private GameObject[] gloveRightPrefabs;
        [SerializeField] private GameObject[] bootLeftPrefabs;
        [SerializeField] private GameObject[] bootRightPrefabs;
        [SerializeField] private GameObject hookLeftPrefab;
        [SerializeField] private GameObject hookRightPrefab;
        [SerializeField] private GameObject woodenLegLeftPrefab;
        [SerializeField] private GameObject woodenLegRightPrefab;

        private readonly NetworkVariable<PirateAppearanceState> _state = new();
        private PlayerAnimationSync _animationSync;
        private NetworkHealth _health;
        private RuntimeAnimatorController _locomotionController;
        private Transform _appearanceRoot;
        private Transform _pirateRoot;
        private Transform _headSlot;
        private Transform _eyePatchSlot;
        private Transform _earringSlot;
        private Animator _pirateAnimator;
        private readonly Dictionary<string, Transform> _bodyBones = new(StringComparer.Ordinal);
        private Renderer[] _bodyRenderers = Array.Empty<Renderer>();
        private Renderer _baseLeftHand;
        private Renderer _baseRightHand;
        private Renderer _baseLeftLeg;
        private Renderer _baseRightLeg;
        private GameObject _hair;
        private GameObject _bandana;
        private GameObject _hat;
        private GameObject _coat;
        private GameObject _gloveLeft;
        private GameObject _gloveRight;
        private GameObject _hookLeft;
        private GameObject _hookRight;
        private GameObject _eyePatch;
        private GameObject _earring;
        private GameObject _bootLeft;
        private GameObject _bootRight;
        private GameObject _woodenLegLeft;
        private GameObject _woodenLegRight;
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
            if (IsServer)
                _state.Value = Validate(defaultAppearance);
            Apply(_state.Value);
            if (IsOwner && loadSavedAppearance)
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
            PirateCustomizationCategory.Gloves => 4 + GetGlovePairCount(),
            PirateCustomizationCategory.EyePatch => 1 + (eyePatchPrefabs?.Length ?? 0),
            PirateCustomizationCategory.Earrings => 1 + (earringPrefabs?.Length ?? 0),
            PirateCustomizationCategory.Boots => 4 + GetBootPairCount(),
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
                PirateCustomizationCategory.Gloves => GetHandOptionName(index),
                PirateCustomizationCategory.EyePatch => "Повязка",
                PirateCustomizationCategory.Earrings => $"Серьги {index}",
                PirateCustomizationCategory.Boots => GetLegOptionName(index),
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
                $"{state.Pirate},{state.Hair},{state.Bandana},{state.Hat},{state.Coat}," +
                $"{state.Gloves},0,{state.EyePatch},{state.Earrings},{state.Boots}");
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
            // Keep accepting every previously shipped local format so existing selections survive
            // as new categories are appended to the save payload.
            if (parts.Length != 5 && parts.Length != 9 && parts.Length != 10 && parts.Length != 11)
                return;
            var state = new PirateAppearanceState();
            if (!byte.TryParse(parts[0], out state.Pirate) || !byte.TryParse(parts[1], out state.Hair) ||
                !byte.TryParse(parts[2], out state.Bandana) || !byte.TryParse(parts[3], out state.Hat) ||
                !byte.TryParse(parts[4], out state.Coat))
                return;
            byte savedHooks = 0;
            if (parts.Length == 9)
            {
                if (!byte.TryParse(parts[5], out state.Gloves) || !byte.TryParse(parts[6], out savedHooks) ||
                    !byte.TryParse(parts[7], out state.EyePatch) || !byte.TryParse(parts[8], out state.Earrings))
                    return;
            }
            if (parts.Length >= 10 &&
                (!byte.TryParse(parts[5], out state.Gloves) || !byte.TryParse(parts[6], out savedHooks) ||
                 !byte.TryParse(parts[7], out state.EyePatch) || !byte.TryParse(parts[8], out state.Earrings) ||
                 !byte.TryParse(parts[9], out state.Boots)))
                return;
            if (savedHooks > 0)
                state.Gloves = (byte)(GetGlovePairCount() + Mathf.Clamp(savedHooks, 1, 3));
            if (parts.Length == 11)
            {
                if (!byte.TryParse(parts[10], out var woodenLegs))
                    return;
                if (woodenLegs > 0)
                    state.Boots = (byte)(GetBootPairCount() + Mathf.Clamp(woodenLegs, 1, 3));
            }
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
            _eyePatchSlot = FindDeep(_pirateRoot, "EyePatch_slot") ?? _headSlot;
            _earringSlot = FindDeep(_pirateRoot, "Earring_slot") ?? _headSlot;
            _bodyBones.Clear();
            foreach (var bodyTransform in _pirateRoot.GetComponentsInChildren<Transform>(true))
                _bodyBones.TryAdd(bodyTransform.name, bodyTransform);
            _bodyRenderers = _pirateRoot.GetComponentsInChildren<Renderer>(true);
            _baseLeftHand = FindDeep(_pirateRoot, "Hand_Left")?.GetComponent<Renderer>();
            _baseRightHand = FindDeep(_pirateRoot, "Hand_Right")?.GetComponent<Renderer>();
            _baseLeftLeg = FindDeep(_pirateRoot, "Leg_Left")?.GetComponent<Renderer>();
            _baseRightLeg = FindDeep(_pirateRoot, "Leg_Right")?.GetComponent<Renderer>();
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
            ReplaceStaticAtSlot(ref _eyePatch, eyePatchPrefabs, state.EyePatch, _eyePatchSlot);
            ReplaceStaticAtSlot(ref _earring, earringPrefabs, state.Earrings, _earringSlot);
            ReplaceSkinned(ref _coat, coatPrefabs, state.Coat);
            var bootPairCount = GetBootPairCount();
            var selectedBoot = state.Boots <= bootPairCount ? state.Boots : 0;
            var woodenLegs = state.Boots > bootPairCount ? state.Boots - bootPairCount : 0;
            var glovePairCount = GetGlovePairCount();
            var selectedGlove = state.Gloves <= glovePairCount ? state.Gloves : 0;
            var hooks = state.Gloves > glovePairCount ? state.Gloves - glovePairCount : 0;
            var hasLeftHook = hooks == 1 || hooks == 3;
            var hasRightHook = hooks == 2 || hooks == 3;
            var hasLeftWoodenLeg = woodenLegs == 1 || woodenLegs == 3;
            var hasRightWoodenLeg = woodenLegs == 2 || woodenLegs == 3;
            // A hook physically replaces the glove on the same hand, so never render both meshes
            // on top of each other. Removing the hook restores the selected glove automatically.
            ReplaceBodyPart(ref _gloveLeft, gloveLeftPrefabs, selectedGlove);
            ReplaceBodyPart(ref _gloveRight, gloveRightPrefabs, selectedGlove);
            ReplaceBodyPart(ref _hookLeft, hookLeftPrefab, hasLeftHook);
            ReplaceBodyPart(ref _hookRight, hookRightPrefab, hasRightHook);
            ReplaceBodyPart(ref _bootLeft, bootLeftPrefabs, selectedBoot);
            ReplaceBodyPart(ref _bootRight, bootRightPrefabs, selectedBoot);
            ReplaceBodyPart(ref _woodenLegLeft, woodenLegLeftPrefab, hasLeftWoodenLeg);
            ReplaceBodyPart(ref _woodenLegRight, woodenLegRightPrefab, hasRightWoodenLeg);
            // The asset pack models gloves, hooks, boots and wooden legs as replacements for its original
            // Hand_Left/Hand_Right/Leg_Left/Leg_Right meshes, not as overlays.
            if (_baseLeftHand != null) _baseLeftHand.enabled = selectedGlove == 0 && !hasLeftHook;
            if (_baseRightHand != null) _baseRightHand.enabled = selectedGlove == 0 && !hasRightHook;
            if (_baseLeftLeg != null) _baseLeftLeg.enabled = selectedBoot == 0 && !hasLeftWoodenLeg;
            if (_baseRightLeg != null) _baseRightLeg.enabled = selectedBoot == 0 && !hasRightWoodenLeg;
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

        private int GetBootPairCount() => Mathf.Min(bootLeftPrefabs?.Length ?? 0,
            bootRightPrefabs?.Length ?? 0);

        private int GetGlovePairCount() => Mathf.Min(gloveLeftPrefabs?.Length ?? 0,
            gloveRightPrefabs?.Length ?? 0);

        private string GetHandOptionName(int index)
        {
            var gloveCount = GetGlovePairCount();
            if (index <= gloveCount)
                return $"Перчатки {index}";
            return (index - gloveCount) switch
            {
                1 => "Крюк слева",
                2 => "Крюк справа",
                3 => "Крюки на обеих",
                _ => "Нет"
            };
        }

        private string GetLegOptionName(int index)
        {
            var bootCount = GetBootPairCount();
            if (index <= bootCount)
                return $"Ботинки {index}";
            return (index - bootCount) switch
            {
                1 => "Деревянная левая",
                2 => "Деревянная правая",
                3 => "Деревянные обе",
                _ => "Нет"
            };
        }

        private void ReplaceStaticAtSlot(ref GameObject current, GameObject[] prefabs, int index, Transform slot)
        {
            if (current != null)
                Destroy(current);
            current = null;
            if (index <= 0 || prefabs == null || index > prefabs.Length || prefabs[index - 1] == null || slot == null)
                return;
            current = Instantiate(prefabs[index - 1], slot, false);
            current.name = prefabs[index - 1].name;
            current.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            current.transform.localScale = Vector3.one;
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

        private void ReplaceBodyPart(ref GameObject current, GameObject[] prefabs, int index)
        {
            var prefab = index > 0 && prefabs != null && index <= prefabs.Length ? prefabs[index - 1] : null;
            ReplaceBodyPart(ref current, prefab, prefab != null);
        }

        private void ReplaceBodyPart(ref GameObject current, GameObject prefab, bool enabled)
        {
            if (current != null)
                Destroy(current);
            current = null;
            if (!enabled || prefab == null || _pirateRoot == null)
                return;
            // Body-part prefabs use the exact same bind pose as the pirate rig. Keeping their
            // renderer root inside the scaled Pirate Body reproduces the layout authored by the
            // asset pack and avoids a second, mismatched presentation coordinate space.
            current = Instantiate(prefab, _pirateRoot, false);
            current.name = prefab.name;
            current.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            current.transform.localScale = Vector3.one;
            DisablePhysics(current);
            RetargetSkinnedMeshes(current);
        }

        private void RetargetSkinnedMeshes(GameObject attachment)
        {
            foreach (var renderer in attachment.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mapped = new Transform[renderer.bones.Length];
                for (var i = 0; i < mapped.Length; i++)
                    mapped[i] = renderer.bones[i] != null && _bodyBones.TryGetValue(renderer.bones[i].name, out var bone)
                        ? bone : renderer.bones[i];
                renderer.bones = mapped;
                if (renderer.rootBone != null && _bodyBones.TryGetValue(renderer.rootBone.name, out var root))
                    renderer.rootBone = root;
                // These small meshes live far from their instantiated renderer root and the
                // player's rig is scaled inside Pirate Body. Let Unity update the bounds from the
                // retargeted bones; otherwise gloves, hooks, boots and wooden legs are incorrectly culled.
                renderer.updateWhenOffscreen = true;
                renderer.localBounds = new Bounds(Vector3.up * 0.8f, Vector3.one * 4f);
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
