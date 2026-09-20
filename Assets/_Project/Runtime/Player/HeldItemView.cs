using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using WaveByWave.Items;

namespace WaveByWave.Player
{
    // Runs after the final player/ship presentation frame. Never changes KCC or the camera's pose.
    [DefaultExecutionOrder(9700)]
    public sealed class HeldItemView : MonoBehaviour
    {
        private PlayerEquipment _equipment;
        private NetworkPlayerController _player;
        private PlayerInventory _inventory;
        private Transform _rig, _motion, _itemPose, _item, _rightHand, _leftHand, _rightSleeve, _leftSleeve;
        private Transform _bodyVisual;
        private Animator _animator;
        private Transform _rightUpper, _rightLower, _rightBone, _leftUpper, _leftLower, _leftBone;
        private GameObject _hookVisual, _bucketWater;
        private LineRenderer _rope;
        private ItemDefinition _definition;
        private Camera _camera;
        private float _fov, _aimBlend, _blockBlend, _blockHitUntil;
        private bool _initialized;
        // Remote look direction arrives over the network at a throttled, non-interpolated rate;
        // smoothing it locally removes the visible stepping when the owner turns their camera.
        private Vector3 _smoothedLook;
        private bool _smoothedLookInitialized;
        // Remote playback is timed locally from the moment a new action state is observed,
        // so network latency / ServerTime offset cannot swallow short actions like a musket shot.
        private EquipmentMotionState _lastState;
        private bool _stateSeen;
        private float _localActionStart = float.NegativeInfinity;
        private readonly HashSet<EquipmentAction> _warnedMissing = new();

        public void Initialize(PlayerEquipment equipment, NetworkPlayerController player, PlayerInventory inventory)
        { _equipment = equipment; _player = player; _inventory = inventory; }

        private bool EnsureRig()
        {
            if (_initialized) return true;
            if (_equipment.IsOwner && _player.OwnerView == null) return false;
            _bodyVisual = transform.Find("Presentation Root/Visual");
            if (_bodyVisual == null) _bodyVisual = transform.Find("Visual");
            _rig = new GameObject(_equipment.IsOwner ? "First person equipment" : "Third person equipment").transform;
            if (_equipment.IsOwner)
            {
                _rig.SetParent(_player.OwnerView, false);
                _camera = _player.OwnerView.GetComponent<Camera>(); _fov = _camera != null ? _camera.fieldOfView : 75f;
            }
            _motion = _equipment.IsOwner && _equipment.FirstPersonHandsPrefab != null
                ? Instantiate(_equipment.FirstPersonHandsPrefab, _rig).transform : new GameObject("Motion").transform;
            _motion.name = "Motion"; _motion.SetParent(_rig, false);
            if (_equipment.IsOwner)
            {
                // Hands and sleeves must come from the first-person hands prefab; no procedural fallback is generated.
                _rightHand = _motion.Find("Right hand");
                _leftHand = _motion.Find("Left hand");
                _rightSleeve = _motion.Find("Right sleeve");
                _leftSleeve = _motion.Find("Left sleeve");
                if (_rightHand == null || _leftHand == null || _rightSleeve == null || _leftSleeve == null)
                    Debug.LogWarning("HeldItemView: first-person hands prefab is missing one of 'Right hand', 'Left hand', 'Right sleeve', 'Left sleeve'. Those parts will not be shown.");
                foreach (var collider in _motion.GetComponentsInChildren<Collider>()) { collider.enabled = false; Destroy(collider); }
                foreach (var body in _motion.GetComponentsInChildren<Rigidbody>()) Destroy(body);
                // The controller exposes clips to Animation Window. Runtime motion is sampled below.
                foreach (var animator in _motion.GetComponentsInChildren<Animator>()) animator.enabled = false;
            }
            else
            {
                _rig.SetParent(transform, false);
                // Owner's Motion comes from the hands prefab, which has an Animator (disabled at runtime).
                // A bare GameObject has none, and AnimationClip.SampleAnimation on non-legacy clips
                // may silently do nothing without one. Add the same disabled Animator here.
                if (_motion.GetComponent<Animator>() == null)
                    _motion.gameObject.AddComponent<Animator>().enabled = false;
                _animator = _bodyVisual != null ? _bodyVisual.GetComponentInChildren<Animator>() : null;
                if (_animator != null && _animator.isHuman)
                {
                    _rightUpper = _animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                    _rightLower = _animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                    _rightBone = _animator.GetBoneTransform(HumanBodyBones.RightHand);
                    _leftUpper = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                    _leftLower = _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                    _leftBone = _animator.GetBoneTransform(HumanBodyBones.LeftHand);
                }
            }
            if (_equipment.HookRopePrefab != null)
            {
                var ropeObject = Instantiate(_equipment.HookRopePrefab, _rig, false);
                _rope = ropeObject.GetComponentInChildren<LineRenderer>(true);
            }
            if (_rope == null)
                Debug.LogError("PlayerEquipment requires a hook rope prefab with LineRenderer.", _equipment);
            else _rope.enabled = false;
            _initialized = true; return true;
        }
        private void SetItem(ItemDefinition definition)
        {
            _definition = definition;
            if (_itemPose != null) Destroy(_itemPose.gameObject);
            if (_hookVisual != null) Destroy(_hookVisual);
            _itemPose = null; _item = null; _bucketWater = null; _hookVisual = null;
            if (definition == null || definition.WorldVisualPrefab == null) return;
            _itemPose = new GameObject("Held " + definition.Id).transform;
            _itemPose.SetParent(_motion, false);
            _item = ItemVisualUtility.InstantiatePresentation(definition.WorldVisualPrefab, _itemPose,
                definition.DisplayName).transform;
            foreach (var collider in _item.GetComponentsInChildren<Collider>()) { collider.enabled = false; Destroy(collider); }
            foreach (var body in _item.GetComponentsInChildren<Rigidbody>()) Destroy(body);
            foreach (var renderer in _item.GetComponentsInChildren<Renderer>())
                if (_equipment.IsOwner) renderer.shadowCastingMode = ShadowCastingMode.Off;
            if (definition.EquipmentKind == ItemEquipmentKind.Bucket)
            {
                var water = FindDescendant(_item, "Bucket contents");
                _bucketWater = water != null ? water.gameObject : null;
                if (_bucketWater != null) _bucketWater.SetActive(false);
            }
        }
        private static Transform FindDescendant(Transform root, string childName)
        {
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == childName) return child;
            return null;
        }
        public void BlockImpact() => _blockHitUntil = Time.unscaledTime + 0.2f;
        public Vector3 MuzzlePosition(Vector3 fallback) => _item != null && _definition != null &&
            _definition.EquipmentKind == ItemEquipmentKind.Musket ? _item.TransformPoint(new Vector3(0f, 0.8f, 0.04f)) : fallback;

        private void LateUpdate()
        {
            if (_equipment == null || !_equipment.IsSpawned || !EnsureRig()) return;
            _inventory.TryGetDefinition(_inventory.EquippedIndex, out var definition);
            if (_definition != definition) SetItem(definition);
            var visible = definition != null && _equipment.Available && !_player.IsAtControlStation &&
                !_player.IsCustomizing &&
                (!_equipment.IsOwner || (_player.OwnerView.gameObject.activeInHierarchy && !PlayerEquipment.InputCaptured));
            _rig.gameObject.SetActive(visible);
            if (_equipment.IsOwner && _camera != null)
                _camera.fieldOfView = Mathf.Lerp(_camera.fieldOfView,
                    visible && _equipment.IsAiming && definition.EquipmentKind == ItemEquipmentKind.Musket ? 48f : _fov,
                    1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
            if (!visible || _item == null)
            {
                if (_hookVisual != null) _hookVisual.SetActive(false);
                if (!visible) _smoothedLookInitialized = false; // re-snap next time it becomes visible, avoid a stale slerp source
                return;
            }
            if (!_equipment.IsOwner)
            {
                var source = _bodyVisual != null ? _bodyVisual : transform;
                var target = _equipment.LookDirection;
                if (target.sqrMagnitude < 0.0001f) target = source.forward;
                if (!_smoothedLookInitialized) { _smoothedLook = target; _smoothedLookInitialized = true; }
                else _smoothedLook = Vector3.Slerp(_smoothedLook, target, 1f - Mathf.Exp(-15f * Time.deltaTime));
                _rig.SetPositionAndRotation(source.position + source.up * 1.35f,
                    Quaternion.LookRotation(_smoothedLook, source.up));
            }
            _aimBlend = Mathf.MoveTowards(_aimBlend, _equipment.IsAiming ? 1f : 0f, Time.unscaledDeltaTime * 7f);
            _itemPose.localPosition = definition.EquipmentKind == ItemEquipmentKind.Musket
                ? Vector3.Lerp(definition.HeldPosition, new Vector3(0f, -0.13f, 0.7f), _aimBlend) : definition.HeldPosition;
            _itemPose.localRotation = Quaternion.Euler(definition.HeldEulerAngles);
            _itemPose.localScale = Vector3.one * definition.HeldScale;
            _motion.localPosition = Vector3.zero; _motion.localRotation = Quaternion.identity; _motion.localScale = Vector3.one;
            _blockBlend = Mathf.MoveTowards(_blockBlend, _equipment.IsBlocking ? 1f : 0f, Time.unscaledDeltaTime * 7f);
            SampleMotion(definition);
            if (_blockHitUntil > Time.unscaledTime)
                _motion.localPosition += Vector3.back * (0.08f * Mathf.Sin((_blockHitUntil - Time.unscaledTime) / 0.2f * Mathf.PI));
            var right = _item.TransformPoint(Grip(definition.EquipmentKind));
            var left = definition.EquipmentKind == ItemEquipmentKind.Musket
                ? _item.TransformPoint(new Vector3(0f, 0.2f, 0.02f)) :
                definition.EquipmentKind == ItemEquipmentKind.Shovel ? _item.TransformPoint(new Vector3(0f, -0.15f, 0f)) :
                _item.TransformPoint(new Vector3(-0.25f, 0f, 0f));
            var twoHanded = definition.EquipmentKind == ItemEquipmentKind.Musket ||
                definition.EquipmentKind == ItemEquipmentKind.Shovel || definition.EquipmentKind == ItemEquipmentKind.Carry;
            if (_equipment.Reloading && definition.EquipmentKind == ItemEquipmentKind.Musket)
                left += _rig.up * (Mathf.Sin(_equipment.ReloadProgress * Mathf.PI * 4f) * 0.12f);
            if (_equipment.IsOwner)
            {
                // Hands/sleeves are optional now: only driven if the prefab actually provided them.
                if (_rightHand != null)
                { _rightHand.position = right; _rightHand.rotation = _item.rotation * Quaternion.Euler(0f, 0f, 10f); }
                if (_leftHand != null) _leftHand.gameObject.SetActive(twoHanded);
                if (_leftSleeve != null) _leftSleeve.gameObject.SetActive(twoHanded);
                if (_leftHand != null) { _leftHand.position = left; _leftHand.rotation = _item.rotation; }
                if (_rightSleeve != null) Sleeve(_rightSleeve, _motion.TransformPoint(new Vector3(0.42f, -0.65f, 0.1f)), right);
                if (twoHanded && _leftSleeve != null) Sleeve(_leftSleeve, _motion.TransformPoint(new Vector3(-0.4f, -0.65f, 0.1f)), left);
            }
            else
            {
                // Предмет — дочерний объект Motion, а клип уже сэмплирован в SampleMotion,
                // поэтому right/left уже содержат анимацию. Просто тянем руки к ним.
                SolveArm(_rightUpper, _rightLower, _rightBone, right, _rig.right - _rig.up);
                if (twoHanded) SolveArm(_leftUpper, _leftLower, _leftBone, left, -_rig.right - _rig.up);
            }
            if (_bucketWater != null) _bucketWater.SetActive(_equipment.BucketFull);
            UpdateHook(definition, right);
        }
        private void SampleMotion(ItemDefinition definition)
        {
            var state = _equipment.DisplayMotion;
            var elapsed = (float)(_equipment.NetworkManager.ServerTime.Time - state.Started);
            if (!_equipment.IsOwner) elapsed = RemoteElapsed(state, elapsed);
            var action = elapsed >= 0f && elapsed < state.Duration ? state.Action : EquipmentAction.None;
            if (action == EquipmentAction.None)
            {
                if (_blockBlend > 0f && definition.EquipmentKind == ItemEquipmentKind.Sword) action = EquipmentAction.SwordBlock;
                else if (_equipment.Reloading && definition.EquipmentKind == ItemEquipmentKind.Musket)
                { action = EquipmentAction.MusketReload; elapsed = _equipment.ReloadProgress; }
                else if (_equipment.ChargingHook) { action = EquipmentAction.HookCharge; elapsed = _equipment.HookCharge; }
                else if (_equipment.Hook.Phase == HookPhase.Reeling) { action = EquipmentAction.HookReel; elapsed = Time.time % 0.5f; }
                else if (_equipment.IsAiming && definition.EquipmentKind == ItemEquipmentKind.Musket) action = EquipmentAction.MusketAim;
            }
            var clip = _equipment.Motions != null ? _equipment.Motions.Get(action) : null;
            if (clip == null)
            {
                if (action != EquipmentAction.None && _warnedMissing.Add(action))
                    Debug.LogWarning($"HeldItemView: no clip for {action} (EquipmentMotionSet is " +
                        $"{(_equipment.Motions == null ? "MISSING" : "assigned")}, owner={_equipment.IsOwner}).");
                return;
            }
            var hold = action == EquipmentAction.SwordBlock || action == EquipmentAction.MusketAim;
            var time = hold ? action == EquipmentAction.SwordBlock ? _blockBlend * clip.length : clip.length :
                action == EquipmentAction.MusketReload || action == EquipmentAction.HookCharge
                ? Mathf.Clamp01(elapsed) * clip.length : action == EquipmentAction.HookReel ? elapsed % Mathf.Max(0.01f, clip.length)
                : Mathf.Clamp01(elapsed / Mathf.Max(0.01f, state.Duration)) * clip.length;
            clip.SampleAnimation(_motion.gameObject, time);
        }
        // For other players' actions: detect a new action state and time it from the local clock.
        // Actions that are already stale when first seen (join, re-equip) are not replayed.
        private float RemoteElapsed(EquipmentMotionState state, float serverElapsed)
        {
            var changed = !_stateSeen || state.Sequence != _lastState.Sequence ||
                state.Started != _lastState.Started || state.Action != _lastState.Action;
            if (changed)
            {
                _stateSeen = true; _lastState = state;
                var recent = state.Action != EquipmentAction.None && serverElapsed < state.Duration + 0.5f;
                _localActionStart = recent ? Time.unscaledTime : float.NegativeInfinity;
            }
            return Time.unscaledTime - _localActionStart;
        }
        private static Vector3 Grip(ItemEquipmentKind kind) => kind switch
        {
            ItemEquipmentKind.Sword => new Vector3(0f, -0.64f, 0f),
            ItemEquipmentKind.Musket => new Vector3(0.05f, -0.32f, 0.03f),
            ItemEquipmentKind.Bucket => new Vector3(0f, 0.53f, 0f),
            ItemEquipmentKind.Shovel => new Vector3(0f, 0.55f, 0f),
            ItemEquipmentKind.Hook => new Vector3(0f, -0.3f, 0f),
            _ => new Vector3(0.12f, -0.1f, 0f)
        };
        private static void Sleeve(Transform sleeve, Vector3 from, Vector3 to)
        {
            var delta = to - from; sleeve.position = (from + to) * 0.5f;
            sleeve.rotation = Quaternion.FromToRotation(Vector3.up, delta.normalized);
            sleeve.localScale = new Vector3(0.09f, delta.magnitude * 0.5f, 0.09f);
        }
        private static void SolveArm(Transform upper, Transform lower, Transform hand, Vector3 target, Vector3 pole)
        {
            if (upper == null || lower == null || hand == null) return;
            var a = Vector3.Distance(upper.position, lower.position); var b = Vector3.Distance(lower.position, hand.position);
            var delta = target - upper.position; var distance = Mathf.Clamp(delta.magnitude, Mathf.Abs(a - b) + 0.001f, a + b - 0.001f);
            if (a < 0.001f || b < 0.001f || delta.sqrMagnitude < 0.0001f) return;
            var direction = delta.normalized;
            var along = (a * a + distance * distance - b * b) / (2f * distance);
            var perpendicular = Vector3.ProjectOnPlane(pole, direction).normalized;
            var elbow = upper.position + direction * along + perpendicular * Mathf.Sqrt(Mathf.Max(0f, a * a - along * along));
            upper.rotation = Quaternion.FromToRotation(lower.position - upper.position, elbow - upper.position) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, target - lower.position) * lower.rotation;
        }
        private void UpdateHook(ItemDefinition definition, Vector3 hand)
        {
            var active = definition.EquipmentKind == ItemEquipmentKind.Hook && _equipment.Hook.Phase != HookPhase.Stowed;
            _item.gameObject.SetActive(!active);
            if (_rope != null) _rope.enabled = active;
            if (!active) { if (_hookVisual != null) _hookVisual.SetActive(false); return; }
            if (_hookVisual == null)
            {
                _hookVisual = new GameObject("Thrown hook");
                ItemVisualUtility.InstantiatePresentation(definition.WorldVisualPrefab, _hookVisual.transform,
                    definition.DisplayName);
                foreach (var collider in _hookVisual.GetComponentsInChildren<Collider>()) { collider.enabled = false; Destroy(collider); }
                foreach (var body in _hookVisual.GetComponentsInChildren<Rigidbody>()) Destroy(body);
            }
            _hookVisual.SetActive(true); var end = _equipment.RenderedHookPosition;
            _hookVisual.transform.position = end;
            var state = _equipment.Hook;
            var velocity = state.Phase == HookPhase.Flying ? state.Velocity + Vector3.down *
                (_equipment.HookGravity * Mathf.Max(0f, (float)(_equipment.NetworkManager.ServerTime.Time - state.Started)))
                : state.Phase == HookPhase.Reeling ? hand - end : Vector3.zero;
            _hookVisual.transform.rotation = velocity.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(velocity) * Quaternion.Euler(90f, 0f, 0f) : Quaternion.Euler(90f, 0f, 0f);
            var distance = Vector3.Distance(hand, end);
            if (_rope == null) return;
            for (var i = 0; i < _rope.positionCount; i++)
            {
                var t = i / (float)(_rope.positionCount - 1);
                _rope.SetPosition(i, Vector3.Lerp(hand, end, t) + Vector3.down *
                    (4f * Mathf.Min(0.45f, distance * 0.018f) * t * (1f - t)));
            }
        }
        private void OnDestroy()
        {
            if (_camera != null) _camera.fieldOfView = _fov;
            if (_rig != null) Destroy(_rig.gameObject);
            if (_hookVisual != null) Destroy(_hookVisual);
        }
    }
}
