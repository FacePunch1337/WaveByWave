using System;
using System.Collections.Generic;
using StylizedWater3;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using WaveByWave.Items;
using WaveByWave.Ships;
using WaveByWave.UI;

namespace WaveByWave.Player
{
    public interface IBucketWaterSource
    {
        // Future flooded ship compartments can implement this without changing input or animations.
        bool TryScoopWaterServer(float litres, Vector3 point);
        void AddWaterServer(float litres, Vector3 point);
    }

    [Serializable] public sealed class BucketWaterEvent : UnityEvent<Vector3, float> { }

    [DefaultExecutionOrder(9600)]
    [RequireComponent(typeof(PlayerInventory), typeof(NetworkPlayerController))]
    public sealed class PlayerEquipment : NetworkBehaviour, IEquipmentDamageReceiver
    {
        [SerializeField] private EquipmentMotionSet motions;
        [SerializeField] private GameObject firstPersonHandsPrefab;
        [SerializeField] private WaveProfile waterWaveProfile;
        [SerializeField] private Material metalMaterial, effectMaterial, handMaterial, sleeveMaterial;
        [SerializeField] private GameObject waterSplashPrefab;
        [Header("Сабля и стамина")]
        [SerializeField, Min(1f)] private float maximumStamina = 100f;
        [SerializeField, Min(0f)] private float staminaRecovery = 18f, blockDrainPerSecond = 7f, swingStamina = 12f;
        [SerializeField, Min(0f)] private float successfulBlockStamina = 15f;
        [SerializeField, Min(0.1f)] private float swordRange = 2.2f, swordSwingDuration = 0.55f;
        [Header("Мушкет")]
        [SerializeField, Min(1f)] private float bulletSpeed = 95f, bulletGravity = 9.81f;
        [SerializeField, Min(0.1f)] private float reloadDuration = 2.5f;
        [SerializeField, Min(0.01f)] private float bulletRadius = 0.035f;
        [SerializeField, Min(1f)] private float bulletLifetime = 6f;
        [Header("Крюк")]
        [SerializeField, Min(0.1f)] private float hookChargeDuration = 1.5f;
        [SerializeField, Min(1f)] private float hookMinimumSpeed = 8f, hookMaximumSpeed = 22f;
        [SerializeField, Min(1f)] private float hookGravity = 12f, hookReelSpeed = 8f;
        [SerializeField, Min(1f)] private float maximumRopeLength = 45f;
        [SerializeField, Min(0.05f)] private float hookPickupRadius = 0.65f;
        [SerializeField, Range(1, 16)] private int hookItemCapacity = 6;
        [Header("Инструменты — события для дальнейшей игровой логики")]
        [SerializeField, Min(0.1f)] private float bucketLitres = 10f;
        [SerializeField] private BucketWaterEvent onWaterScooped = new(), onWaterPoured = new();
        [SerializeField] private UnityEvent<Vector3> onDig = new();
        [SerializeField] private LayerMask hitLayers = ~0;

        private readonly NetworkVariable<float> _stamina = new(100f);
        private readonly NetworkVariable<bool> _blocking = new(), _aiming = new(), _bucketFull = new();
        private readonly NetworkVariable<double> _reloadEnd = new();
        private readonly NetworkVariable<EquipmentMotionState> _motion = new();
        private readonly NetworkVariable<EquipmentHookState> _hook = new();
        private readonly NetworkVariable<Vector3> _look = new(Vector3.forward);
        private readonly NetworkVariable<bool> _available = new(true);
        private readonly RaycastHit[] _hits = new RaycastHit[96];
        private readonly Collider[] _targets = new Collider[48];
        private readonly HashSet<MonoBehaviour> _swordTargets = new();
        private readonly List<WorldItem> _hookItems = new();
        private readonly HashSet<WorldItem> _claimedThisThrow = new();
        private readonly List<Bullet> _bullets = new();
        private readonly Dictionary<int, EquipmentProjectileVisual> _bulletVisuals = new();
        private NetworkPlayerController _player;
        private PlayerInventory _inventory;
        private HeldItemView _view;
        private EquipmentWaterQuery _water;
        private CapsuleCollider _combatHitbox;
        private double _cooldown, _recoverAfter, _inputHeartbeat, _serverHeartbeat, _chargeStarted = -1d;
        private double _hookSampleTime;
        private bool _localBlock, _localAim, _localReel, _localCharge, _sentCharge;
        private float _localChargeStarted;
        private int _localSlot = -1, _serverSlot = -1, _nextBullet;
        private ItemDefinition _serverItem;
        private ItemDefinition _localItem;
        private Vector3 _hookPosition;
        private Vector3 _renderedHook;
        private int _hookRenderFrame = -1;
        private HookPhase _renderPhase;
        private NetworkObjectReference _tetherReference;
        public NetworkObjectReference TetherReference => _tetherReference;
        private bool _blockExhausted;
        private double _swordHitAt;
        private float _swordDamage;
        private EquipmentMotionState _predictedMotion;
        private double _localActionNext;
        private double Now => NetworkManager.ServerTime.Time;
        public EquipmentMotionSet Motions => motions;
        public GameObject FirstPersonHandsPrefab => firstPersonHandsPrefab;
        public Material MetalMaterial => metalMaterial;
        public Material EffectMaterial => effectMaterial;
        public Material HandMaterial => handMaterial;
        public Material SleeveMaterial => sleeveMaterial;
        public float Stamina => _stamina.Value;
        public float MaximumStamina => maximumStamina;
        public bool IsBlocking => _blocking.Value;
        public bool Available => _available.Value;
        public bool IsAiming => IsOwner ? _localAim : _aiming.Value;
        public bool BucketFull => _bucketFull.Value;
        public bool ChargingHook => IsOwner && _localCharge;
        public float HookCharge => ChargingHook ? Mathf.Clamp01((Time.unscaledTime - _localChargeStarted) / hookChargeDuration) : 0f;
        public float ReloadProgress => Mathf.Clamp01(1f - (float)(_reloadEnd.Value - Now) / reloadDuration);
        public bool Reloading => IsSpawned && _reloadEnd.Value > Now;
        public EquipmentMotionState Motion => _motion.Value;
        public EquipmentMotionState DisplayMotion => IsOwner && _predictedMotion.Action != EquipmentAction.None &&
            Now < _predictedMotion.Started + _predictedMotion.Duration ? _predictedMotion : _motion.Value;
        public EquipmentHookState Hook => _hook.Value;
        public float HookGravity => hookGravity;
        public Vector3 LookDirection => _look.Value;
        public Vector3 ServerHookPosition => _hookPosition;
        public Vector3 RenderedHookPosition
        {
            get
            {
                if (_hookRenderFrame == Time.frameCount) return _renderedHook;
                _hookRenderFrame = Time.frameCount;
                var target = EvaluateHook(Now, true);
                _renderedHook = _hook.Value.Phase == HookPhase.Reeling && _renderPhase == HookPhase.Reeling
                    ? Vector3.Lerp(_renderedHook, target, 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime)) : target;
                _renderPhase = _hook.Value.Phase;
                return _renderedHook;
            }
        }
        public static bool InputCaptured => SessionMenuPresenter.InputCaptured || EquipmentAdminPanel.InputCaptured;
        public event Action<float> SuccessfulBlock;

        private sealed class Bullet
        {
            public int Id;
            public Vector3 Origin, Velocity;
            public double Started, Simulated;
            public float Damage;
            public Vector3 Evaluate(double at, float gravity)
            { var t = (float)(at - Started); return Origin + Velocity * t + Vector3.down * (0.5f * gravity * t * t); }
        }

        private void Awake()
        {
            _player = GetComponent<NetworkPlayerController>();
            _inventory = GetComponent<PlayerInventory>();
        }
        public override void OnNetworkSpawn()
        {
            _tetherReference = new NetworkObjectReference(NetworkObject);
            if (motions == null) motions = Resources.Load<EquipmentMotionSet>("EquipmentMotions");
            if (IsServer)
            {
                _stamina.Value = maximumStamina; _water = new EquipmentWaterQuery(waterWaveProfile);
                var hitbox = new GameObject("Server equipment hitbox"); hitbox.transform.SetParent(transform, false);
                hitbox.AddComponent<EquipmentHitbox>();
                _combatHitbox = hitbox.AddComponent<CapsuleCollider>(); _combatHitbox.isTrigger = true;
                _combatHitbox.radius = 0.35f; _combatHitbox.height = 1.7f; _combatHitbox.center = Vector3.up * 0.8f;
            }
            if (IsClient)
            {
                _view = gameObject.AddComponent<HeldItemView>();
                _view.Initialize(this, _player, _inventory);
            }
            if (IsOwner)
            {
                gameObject.AddComponent<EquipmentHud>().Initialize(this, _inventory);
                gameObject.AddComponent<EquipmentAdminPanel>().Initialize(_inventory);
            }
        }
        public override void OnNetworkDespawn()
        {
            if (IsServer) ResetHookServer();
            if (_combatHitbox != null) { _combatHitbox.enabled = false; Destroy(_combatHitbox.gameObject); }
            _water?.Dispose(); _water = null;
            foreach (var visual in _bulletVisuals.Values) if (visual != null) Destroy(visual.gameObject);
            _bulletVisuals.Clear(); _bullets.Clear();
            if (_view != null) Destroy(_view);
        }

        private void Update()
        {
            if (!IsSpawned) return;
            if (!IsServer) return;
            if (_combatHitbox != null)
            {
                var feet = FeetServer(out var support);
                var rotation = support != null ? WorldItem.GetPhysicsFrame(support).rotation : transform.rotation;
                _combatHitbox.transform.SetPositionAndRotation(feet, rotation);
                _combatHitbox.enabled = _inventory.Health > 0f;
            }
            var selected = _inventory.ServerSelectedIndex;
            _inventory.TryGetDefinition(selected, out var item);
            RefreshSelectedServer(selected, item);
            _available.Value = CanActServer();
            if (!_available.Value)
            {
                StopHeldServer();
                _swordHitAt = 0d;
                if (_hook.Value.Phase != HookPhase.Stowed) ResetHookServer();
            }
            if (_swordHitAt > 0d && Now >= _swordHitAt)
            {
                _swordHitAt = 0d;
                SwordHitServer(FeetServer(out _) + Vector3.up * 1.25f, _look.Value, _swordDamage);
            }
            if (Now - _serverHeartbeat > 0.5d) StopHeldServer();
            var dt = Mathf.Min(Time.deltaTime, 0.1f);
            if (_blocking.Value)
            {
                _stamina.Value = Mathf.Max(0f, _stamina.Value - blockDrainPerSecond * dt);
                _recoverAfter = Now + 0.6d;
                if (_stamina.Value <= 0f) { _blocking.Value = false; _blockExhausted = true; }
            }
            else if (Now >= _recoverAfter)
                _stamina.Value = Mathf.Min(maximumStamina, _stamina.Value + staminaRecovery * dt);
            SimulateBullets();
            SimulateHook();
        }
        private void LateUpdate()
        {
            if (IsSpawned && IsOwner) ReadOwnerInput();
        }
        private void RefreshSelectedServer(int slot, ItemDefinition item)
        {
            if (_serverSlot == slot && _serverItem == item) return;
            StopHeldServer(); _swordHitAt = 0d; _blockExhausted = false;
            if (_hook.Value.Phase != HookPhase.Stowed) ResetHookServer();
            _motion.Value = default;
            _serverSlot = slot; _serverItem = item;
        }

        private void ReadOwnerInput()
        {
            var enabledInput = !InputCaptured && !_player.IsAtControlStation && _inventory.Health > 0f;
            var mouse = Mouse.current;
            _inventory.TryGetDefinition(_inventory.SelectedIndex, out var item);
            if (_localSlot != _inventory.SelectedIndex || _localItem != item || !enabledInput || mouse == null)
            {
                if (_localSlot != _inventory.SelectedIndex || _localItem != item || _localBlock || _localAim || _localReel || _localCharge)
                {
                    _localSlot = _inventory.SelectedIndex;
                    _localItem = item;
                    _predictedMotion = default;
                    _localBlock = _localAim = _localReel = _localCharge = false;
                    SendHeldInput(false, false, false, false);
                }
                if (!enabledInput || mouse == null) return;
            }
            if (item == null)
            {
                if (_localBlock || _localAim || _localReel || _localCharge)
                {
                    _localBlock = _localAim = _localReel = _localCharge = false;
                    SendHeldInput(false, false, false, false);
                }
                return;
            }
            var block = item.EquipmentKind == ItemEquipmentKind.Sword && mouse.rightButton.isPressed;
            var aim = item.EquipmentKind == ItemEquipmentKind.Musket && mouse.rightButton.isPressed;
            var reel = item.EquipmentKind == ItemEquipmentKind.Hook && _hook.Value.Phase != HookPhase.Stowed &&
                _hook.Value.Phase != HookPhase.Flying && mouse.leftButton.isPressed;
            if (item.EquipmentKind == ItemEquipmentKind.Hook && _hook.Value.Phase == HookPhase.Stowed)
            {
                if (mouse.leftButton.wasPressedThisFrame) { _localCharge = true; _localChargeStarted = Time.unscaledTime; }
                if (_localCharge && mouse.leftButton.wasReleasedThisFrame)
                {
                    SendAction(EquipmentAction.HookThrow);
                    _localCharge = false;
                }
            }
            else _localCharge = false;
            if (block != _localBlock || aim != _localAim || reel != _localReel || _localCharge != _sentCharge || Now >= _inputHeartbeat)
            {
                _localBlock = block; _localAim = aim; _localReel = reel;
                SendHeldInput(block, aim, reel, _localCharge);
                _inputHeartbeat = Now + 0.1d;
            }
            if (mouse.leftButton.wasPressedThisFrame)
            {
                var action = item.EquipmentKind switch
                {
                    ItemEquipmentKind.Sword => EquipmentAction.SwordSwing,
                    ItemEquipmentKind.Musket => EquipmentAction.MusketShot,
                    ItemEquipmentKind.Bucket => EquipmentAction.BucketScoop,
                    ItemEquipmentKind.Shovel => EquipmentAction.ShovelDig, _ => EquipmentAction.None
                };
                if (action != EquipmentAction.None) SendAction(action);
            }
            if (item.EquipmentKind == ItemEquipmentKind.Bucket && mouse.rightButton.wasPressedThisFrame)
                SendAction(EquipmentAction.BucketSplash);
        }
        private void SendHeldInput(bool block, bool aim, bool reel, bool charge)
        {
            _sentCharge = charge;
            BuildAim(out var origin, out var direction, out var support);
            HeldInputServerRpc(_inventory.SelectedIndex, _inventory.SelectionRevision, block, aim, reel, charge,
                origin, direction, support);
        }
        private void SendAction(EquipmentAction action)
        {
            if (Now < _localActionNext || !_inventory.TryGetDefinition(_inventory.SelectedIndex, out _)) return;
            if (action == EquipmentAction.MusketShot && Reloading ||
                action == EquipmentAction.SwordSwing && (IsBlocking || Stamina < swingStamina) ||
                action == EquipmentAction.BucketScoop && BucketFull ||
                action == EquipmentAction.BucketSplash && !BucketFull) return;
            var duration = action switch
            {
                EquipmentAction.SwordSwing => swordSwingDuration, EquipmentAction.MusketShot => 0.2f,
                EquipmentAction.HookThrow => 0.4f, EquipmentAction.BucketSplash => 0.65f, _ => 0.8f
            };
            _predictedMotion = new EquipmentMotionState { Action = action, Started = Now, Duration = duration };
            _localActionNext = Now + duration;
            BuildAim(out var origin, out var direction, out var support);
            if (action == EquipmentAction.MusketShot && _player.OwnerView != null)
                CannonEffects.Muzzle(_view != null ? _view.MuzzlePosition(_player.OwnerView.position + _player.OwnerView.forward * 0.8f)
                    : _player.OwnerView.position + _player.OwnerView.forward * 0.8f, _player.OwnerView.forward, effectMaterial);
            ActionServerRpc(action, _inventory.SelectedIndex, _inventory.SelectionRevision, origin, direction, support);
        }
        private void BuildAim(out Vector3 origin, out Vector3 direction, out NetworkObjectReference supportReference)
        {
            _player.GetItemDropPose(out var feet, out direction, out var support);
            var view = _player.OwnerView;
            origin = view != null ? view.position : feet + Vector3.up * 1.35f;
            direction = view != null ? view.forward : direction;
            supportReference = new NetworkObjectReference(support);
            if (support != null)
            {
                origin = support.transform.InverseTransformPoint(origin);
                direction = support.transform.InverseTransformDirection(direction);
            }
        }
        private bool ResolveAimServer(Vector3 requestedOrigin, Vector3 requestedDirection, NetworkObjectReference reference,
            out Vector3 origin, out Vector3 direction)
        {
            origin = requestedOrigin; direction = requestedDirection;
            if (!Finite(origin) || !Finite(direction) || direction.sqrMagnitude < 0.01f) return false;
            if (reference.TryGet(out var support, NetworkManager))
            {
                if (!_player.TryGetPositionOnPlatform(support, out var feet)) return false;
                var localFeet = support.transform.InverseTransformPoint(feet);
                if (Vector3.Distance(localFeet, origin) > 2.8f) return false;
                var frame = WorldItem.GetPhysicsFrame(support);
                origin = frame.MultiplyPoint3x4(origin);
                direction = frame.MultiplyVector(direction);
            }
            else if (Vector3.Distance(transform.position, origin) > 2.8f) return false;
            direction.Normalize();
            return true;
        }
        private bool CanActServer()
        {
            if (_inventory.Health <= 0f) return false;
            var ship = _player.GetSupportingShipOnServer();
            return ship == null || (!ship.IsClientOperatingNavigationStation(OwnerClientId) &&
                (!ship.TryGetComponent<ShipCannonBattery>(out var battery) || battery.GetOperatorCannon(OwnerClientId) < 0));
        }
        [ServerRpc]
        private void HeldInputServerRpc(int slot, uint revision, bool block, bool aim, bool reel, bool charge,
            Vector3 requestedOrigin, Vector3 requestedDirection, NetworkObjectReference support)
        {
            if (!_inventory.ApplySelectionServer(slot, revision) || !CanActServer() ||
                !_inventory.TryGetDefinition(slot, out var item) ||
                !ResolveAimServer(requestedOrigin, requestedDirection, support, out _, out var direction)) return;
            RefreshSelectedServer(slot, item);
            _serverHeartbeat = Now;
            _look.Value = direction;
            if (!block) _blockExhausted = false;
            _blocking.Value = block && !_blockExhausted && item.EquipmentKind == ItemEquipmentKind.Sword && _stamina.Value > 0f && Now >= _cooldown;
            _aiming.Value = aim && item.EquipmentKind == ItemEquipmentKind.Musket;
            _serverReeling = reel && item.EquipmentKind == ItemEquipmentKind.Hook;
            if (charge && item.EquipmentKind == ItemEquipmentKind.Hook && _hook.Value.Phase == HookPhase.Stowed)
            { if (_chargeStarted < 0d) _chargeStarted = Now; }
            else _chargeStarted = -1d;
        }
        private bool _serverReeling;
        private void StopHeldServer()
        { _blocking.Value = _aiming.Value = false; _serverReeling = false; _chargeStarted = -1d; }
        [ServerRpc]
        private void ActionServerRpc(EquipmentAction action, int slot, uint revision,
            Vector3 requestedOrigin, Vector3 requestedDirection, NetworkObjectReference support)
        {
            if (!_inventory.ApplySelectionServer(slot, revision) || !CanActServer() ||
                !_inventory.TryGetDefinition(slot, out var item) || Now < _cooldown ||
                !ResolveAimServer(requestedOrigin, requestedDirection, support, out var origin, out var direction)) return;
            RefreshSelectedServer(slot, item);
            _look.Value = direction;
            switch (action)
            {
                case EquipmentAction.SwordSwing when item.EquipmentKind == ItemEquipmentKind.Sword:
                    if (_blocking.Value || _stamina.Value < swingStamina) return;
                    _stamina.Value -= swingStamina; _recoverAfter = Now + 0.7d;
                    PlayServer(action, swordSwingDuration);
                    _swordHitAt = Now + swordSwingDuration * 0.4f; _swordDamage = item.Potency;
                    break;
                case EquipmentAction.MusketShot when item.EquipmentKind == ItemEquipmentKind.Musket:
                    if (Reloading) return;
                    var ship = _player.GetSupportingShipOnServer();
                    var inherited = ship != null && ship.TryGetComponent<MovingPlatform>(out var platform)
                        ? platform.GetPointVelocity(origin) : Vector3.zero;
                    var aimPoint = SegmentHit(origin, origin + direction * 150f, 0.005f, null, out var aimHit, out _, true)
                        ? aimHit.point : origin + direction * 150f;
                    var right = Vector3.Cross(Vector3.up, direction).normalized;
                    if (right.sqrMagnitude < 0.01f) right = transform.right;
                    var muzzle = origin + direction * 0.8f + right * (_aiming.Value ? 0f : 0.17f) - Vector3.up * 0.18f;
                    // Keep the barrel on this side of an obstacle at point-blank range.
                    if (SegmentHit(origin, muzzle, 0.02f, null, out var muzzleHit, out _))
                        muzzle = muzzleHit.point + muzzleHit.normal * 0.05f;
                    var bullet = new Bullet { Id = ++_nextBullet, Origin = muzzle,
                        Velocity = (aimPoint - muzzle).normalized * bulletSpeed + inherited, Started = Now, Simulated = Now,
                        Damage = item.Potency };
                    _bullets.Add(bullet);
                    _reloadEnd.Value = Now + reloadDuration;
                    PlayServer(action, 0.2f);
                    ShotClientRpc(bullet.Id, bullet.Origin, bullet.Velocity, bullet.Started);
                    break;
                case EquipmentAction.HookThrow when item.EquipmentKind == ItemEquipmentKind.Hook:
                    if (_hook.Value.Phase != HookPhase.Stowed) return;
                    var charge = _chargeStarted < 0d ? 0f : Mathf.Clamp01((float)(Now - _chargeStarted) / hookChargeDuration);
                    var carrier = _player.GetSupportingShipOnServer();
                    var carrierVelocity = carrier != null && carrier.TryGetComponent<MovingPlatform>(out var carrierPlatform)
                        ? carrierPlatform.GetPointVelocity(origin) : Vector3.zero;
                    var launch = (direction + Vector3.up * 0.35f).normalized * Mathf.Lerp(hookMinimumSpeed, hookMaximumSpeed, charge)
                        + carrierVelocity;
                    _hookPosition = origin + direction * 0.3f;
                    _hook.Value = new EquipmentHookState { Phase = HookPhase.Flying, Origin = _hookPosition,
                        Velocity = launch, Started = Now };
                    _hookSampleTime = Now; _chargeStarted = -1d;
                    PlayServer(action, 0.4f);
                    break;
                case EquipmentAction.BucketScoop when item.EquipmentKind == ItemEquipmentKind.Bucket:
                    if (_bucketFull.Value) return;
                    var filled = TryBucketSource(origin, direction, out var source, out var scoopPoint) &&
                        source.TryScoopWaterServer(bucketLitres, scoopPoint);
                    if (!filled)
                    {
                        var probe = FeetServer(out _) + Vector3.up * 0.35f + direction * 0.9f;
                        filled = _water.TryHeight(probe, out var height) && probe.y <= height + 0.55f &&
                            !HasSolidBetween(origin, new Vector3(probe.x, height, probe.z));
                        scoopPoint = new Vector3(probe.x, height, probe.z);
                    }
                    if (!filled) return;
                    _bucketFull.Value = true; onWaterScooped.Invoke(scoopPoint, bucketLitres);
                    PlayServer(action, 0.8f);
                    ToolEffectClientRpc(scoopPoint, true);
                    break;
                case EquipmentAction.BucketSplash when item.EquipmentKind == ItemEquipmentKind.Bucket:
                    if (!_bucketFull.Value) return;
                    _bucketFull.Value = false;
                    var pourPoint = origin + direction * 1.2f;
                    if (TryBucketSource(origin, direction, out var destination, out var targetPoint))
                    { destination.AddWaterServer(bucketLitres, targetPoint); pourPoint = targetPoint; }
                    onWaterPoured.Invoke(pourPoint, bucketLitres);
                    PlayServer(action, 0.65f);
                    PourClientRpc(origin + direction * 0.4f, direction);
                    break;
                case EquipmentAction.ShovelDig when item.EquipmentKind == ItemEquipmentKind.Shovel:
                    var end = origin + (direction + Vector3.down * 0.6f).normalized * 2.5f;
                    if (SegmentHit(origin, end, 0.03f, null, out var digHit, out _))
                    { onDig.Invoke(digHit.point); ToolEffectClientRpc(digHit.point, false); }
                    PlayServer(action, 0.8f);
                    break;
            }
        }

        private void PlayServer(EquipmentAction action, float duration)
        {
            _cooldown = Now + duration;
            _motion.Value = new EquipmentMotionState { Action = action, Sequence = _motion.Value.Sequence + 1,
                Started = Now, Duration = duration };
        }
        public bool TryBlockHitServer(float damage, Vector3 attackerPosition)
        {
            if (!IsServer || !_blocking.Value || !Finite(attackerPosition) || !Finite(damage) || damage < 0f) return false;
            var toward = Vector3.ProjectOnPlane(attackerPosition - FeetServer(out _), Vector3.up).normalized;
            var facing = Vector3.ProjectOnPlane(_look.Value, Vector3.up).normalized;
            if (Vector3.Dot(toward, facing) < 0.25f) return false;
            var cost = successfulBlockStamina + damage * 0.15f;
            if (_stamina.Value < cost)
            { _stamina.Value = 0f; _blocking.Value = false; _blockExhausted = true; _recoverAfter = Now + 1d; return false; }
            _stamina.Value -= cost; _recoverAfter = Now + 0.8d;
            if (_stamina.Value <= 0f) { _blocking.Value = false; _blockExhausted = true; }
            SuccessfulBlock?.Invoke(damage);
            BlockClientRpc();
            return true;
        }
        public void ReceiveEquipmentHitServer(float damage, Vector3 attackerPosition, bool canBlock = true)
        {
            if (!IsServer || !Finite(damage) || damage <= 0f) return;
            if (!canBlock || !TryBlockHitServer(damage, attackerPosition)) _inventory.ApplyDamageServer(damage);
        }
        private void SwordHitServer(Vector3 origin, Vector3 direction, float damage)
        {
            var count = Physics.OverlapSphereNonAlloc(origin, swordRange, _targets, hitLayers, QueryTriggerInteraction.Collide);
            _swordTargets.Clear();
            for (var i = 0; i < count; i++)
            {
                var target = _targets[i];
                if (target.GetComponentInParent<NetworkPlayerController>() == _player) continue;
                var toward = target.ClosestPoint(origin + direction * swordRange * 0.7f) - origin;
                if (toward.sqrMagnitude > swordRange * swordRange || Vector3.Dot(toward.normalized, direction) < 0.35f) continue;
                foreach (var component in target.GetComponentsInParent<MonoBehaviour>())
                {
                    if (component is not IEquipmentDamageReceiver receiver || !_swordTargets.Add(component)) continue;
                    if (!HasSolidBetween(origin, target.bounds.center, target))
                        receiver.ReceiveEquipmentHitServer(damage, origin);
                }
            }
        }
        private bool TryBucketSource(Vector3 origin, Vector3 direction, out IBucketWaterSource source, out Vector3 point)
        {
            source = null; point = origin;
            if (!SegmentHit(origin, origin + direction * 2.5f, 0.03f, null, out var hit, out _)) return false;
            point = hit.point;
            foreach (var component in hit.collider.GetComponentsInParent<MonoBehaviour>())
                if (component is IBucketWaterSource candidate) { source = candidate; return true; }
            return false;
        }
        private bool HasSolidBetween(Vector3 from, Vector3 to, Collider target = null)
        {
            if (!SegmentHit(from, to, 0.01f, null, out var hit, out _, target != null) || hit.collider == target) return false;
            if (target == null) return true;
            var identity = target.GetComponentInParent<NetworkObject>();
            return identity == null || hit.collider.GetComponentInParent<NetworkObject>() != identity;
        }

        private bool SegmentHit(Vector3 from, Vector3 to, float radius, NetworkObject ignoredShip, out RaycastHit result,
            out float fraction, bool includeCombat = false)
        {
            result = default; fraction = 1f;
            var delta = to - from; var distance = delta.magnitude;
            if (distance < 0.0001f) return false;
            var count = Physics.SphereCastNonAlloc(from, radius, delta / distance, _hits, distance, hitLayers, QueryTriggerInteraction.Collide);
            var best = float.PositiveInfinity;
            for (var i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (hit.collider.isTrigger && hit.collider.GetComponent<EquipmentHitbox>() == null) continue;
                var hitPlayer = hit.collider.GetComponentInParent<NetworkPlayerController>();
                if (hitPlayer == _player || !includeCombat && hitPlayer != null ||
                    hit.collider.GetComponentInParent<WorldItem>() != null || hit.collider.GetComponentInParent<WaterObject>() != null) continue;
                if (ignoredShip != null && hit.collider.GetComponentInParent<NetworkObject>() == ignoredShip) continue;
                if (hit.distance >= best) continue;
                best = hit.distance; result = hit;
            }
            if (float.IsPositiveInfinity(best)) return false;
            fraction = best / distance;
            return true;
        }
        private void SimulateBullets()
        {
            const double step = 1d / 60d;
            for (var i = _bullets.Count - 1; i >= 0; i--)
            {
                var b = _bullets[i]; var finished = false;
                // Bound catch-up work; never replace a backlog with a long tunnelling segment.
                for (var n = 0; n < 8 && b.Simulated + step <= Now; n++)
                {
                    var next = b.Simulated + step;
                    var from = b.Evaluate(b.Simulated, bulletGravity); var to = b.Evaluate(next, bulletGravity);
                    var solid = SegmentHit(from, to, bulletRadius, null, out var hit, out var solidFraction, true);
                    var water = _water.Crossing(from, to, out var waterPoint, out var waterFraction);
                    if (solid || water || next - b.Started >= bulletLifetime)
                    {
                        water = water && (!solid || waterFraction < solidFraction);
                        var point = water ? waterPoint : solid ? hit.point : to;
                        if (solid && !water)
                            foreach (var component in hit.collider.GetComponentsInParent<MonoBehaviour>())
                                if (component is IEquipmentDamageReceiver receiver)
                                { receiver.ReceiveEquipmentHitServer(b.Damage, b.Origin, false); break; }
                        BulletImpactClientRpc(b.Id, point, water ? Vector3.up : solid ? hit.normal : Vector3.up,
                            water, solid || water, b.Simulated + step * (water ? waterFraction : solid ? solidFraction : 1f));
                        finished = true; break;
                    }
                    b.Simulated = next;
                }
                if (finished) _bullets.RemoveAt(i);
            }
        }

        private Vector3 FeetServer(out NetworkObject support)
        {
            var ship = _player.GetSupportingShipOnServer();
            support = ship != null ? ship.NetworkObject : null;
            if (support != null && _player.TryGetPositionOnPlatform(support, out var position))
                return WorldItem.GetPhysicsFrame(support).MultiplyPoint3x4(support.transform.InverseTransformPoint(position));
            return transform.position;
        }
        public Vector3 EvaluateHook(double time, bool presentation)
        {
            var state = _hook.Value;
            var point = state.Evaluate(time, hookGravity);
            if (state.HasSupport && state.Support.TryGet(out var support, NetworkManager))
                point = presentation ? support.transform.TransformPoint(point) : WorldItem.GetPhysicsFrame(support).MultiplyPoint3x4(point);
            return point;
        }
        private void SimulateHook()
        {
            var state = _hook.Value;
            if (state.Phase == HookPhase.Stowed) return;
            var feet = FeetServer(out _);
            var hand = feet + Vector3.up * 1.1f;
            if (state.Phase == HookPhase.Flying)
            {
                const double step = 1d / 60d;
                for (var n = 0; n < 8 && _hookSampleTime + step <= Now; n++)
                {
                    var next = _hookSampleTime + step;
                    var to = state.Evaluate(next, hookGravity);
                    var solid = SegmentHit(_hookPosition, to, 0.09f, null,
                        out var hit, out var fraction);
                    var water = _water.Crossing(_hookPosition, to, out var waterPoint, out var waterFraction);
                    if (solid || water || Vector3.Distance(to, hand) >= maximumRopeLength || next - state.Started >= 6d)
                    {
                        water = water && (!solid || waterFraction < fraction);
                        _hookPosition = water ? waterPoint : solid ? hit.point + hit.normal * 0.06f : to;
                        var support = solid && !water ? hit.collider.GetComponentInParent<NetworkObject>() : null;
                        if (!solid && !water && !TryHookSurface(ref _hookPosition, out support))
                        { ResetHookServer(); break; }
                        LandHookServer(_hookPosition, support);
                        if (water) ToolEffectClientRpc(_hookPosition, true);
                        break;
                    }
                    _hookPosition = to; _hookSampleTime = next;
                }
                return;
            }
            if (state.Phase == HookPhase.Landed) _hookPosition = EvaluateHook(Now, false);
            if (Now < _hookSampleTime + 0.05d) return;
            var dt = Mathf.Min(0.1f, (float)(Now - _hookSampleTime)); _hookSampleTime = Now;
            if (!_serverReeling)
            {
                if (state.Phase == HookPhase.Reeling)
                {
                    var landed = _hookPosition;
                    TryHookSurface(ref landed, out var landingSupport);
                    _hookPosition = landed;
                    LandHookServer(landed, landingSupport);
                }
                return;
            }
            var previous = _hookPosition;
            var nextPosition = Vector3.MoveTowards(previous, hand, hookReelSpeed * dt);
            // Stop at intervening solid geometry, rather than pull loot through rocks or walls.
            if (SegmentHit(previous + Vector3.up * 0.12f, nextPosition + Vector3.up * 0.12f, 0.05f, null, out var obstacle, out _))
                nextPosition = obstacle.point + obstacle.normal * 0.07f;
            CaptureItemsServer(previous, nextPosition);
            _hookPosition = nextPosition;
            _hook.Value = new EquipmentHookState { Phase = HookPhase.Reeling, Origin = previous,
                Velocity = (nextPosition - previous) / 0.05f, Started = Now };
            if (Vector3.Distance(nextPosition, hand) < 0.45f)
                ResetHookServer();
        }
        private void LandHookServer(Vector3 position, NetworkObject support)
        {
            if (support != null && !support.IsSpawned) support = null;
            _hook.Value = new EquipmentHookState { Phase = HookPhase.Landed,
                Origin = support != null ? WorldItem.GetPhysicsFrame(support).inverse.MultiplyPoint3x4(position) : position,
                HasSupport = support != null, Support = support != null ? new NetworkObjectReference(support) : default, Started = Now };
            _hookSampleTime = Now;
        }
        private bool TryHookSurface(ref Vector3 position, out NetworkObject support)
        {
            support = null;
            var solid = SegmentHit(position + Vector3.up * 0.3f, position + Vector3.down * 128f, 0.04f, null,
                out var surface, out _);
            var water = _water.TryHeight(position, out var height);
            if (!solid && !water) return false;
            if (solid && (!water || surface.point.y >= height))
            {
                position = surface.point + surface.normal * 0.07f;
                support = surface.collider.GetComponentInParent<NetworkObject>();
            }
            else position.y = height;
            return true;
        }
        private void CaptureItemsServer(Vector3 from, Vector3 to)
        {
            _hookItems.RemoveAll(item => item == null || !item.IsSpawned || !item.IsTetheredTo(this));
            var delta = to - from;
            foreach (var item in WorldItem.ActiveItems)
            {
                if (_hookItems.Count >= hookItemCapacity) break;
                if (item == null || !item.IsSpawned || item.IsTetheredTo(this) || _claimedThisThrow.Contains(item)) continue;
                var position = item.GetServerPosition();
                var t = delta.sqrMagnitude > 0.00001f ? Mathf.Clamp01(Vector3.Dot(position - from, delta) / delta.sqrMagnitude) : 0f;
                var nearest = from + delta * t;
                if (Vector3.Distance(position, nearest) > hookPickupRadius || HasSolidBetween(nearest + Vector3.up * 0.2f, position)) continue;
                item.CaptureWithHookServer(this, Vector3.up * 0.1f + Vector3.right * ((_hookItems.Count % 3) - 1) * 0.15f);
                // A losing hook must not recapture the same item every tick while ropes overlap.
                _claimedThisThrow.Add(item);
                _hookItems.Add(item);
            }
        }
        private void ResetHookServer()
        {
            var feet = FeetServer(out var support);
            var retrieved = Vector3.Distance(_hookPosition, feet + Vector3.up * 1.1f) < 0.65f;
            for (var i = 0; i < _hookItems.Count; i++)
            {
                var item = _hookItems[i];
                if (item == null || !item.IsSpawned || !item.IsTetheredTo(this)) continue;
                var release = retrieved ? feet : IsSpawned ? item.GetServerPosition() : item.transform.position;
                var releaseSupport = retrieved ? support : null;
                var water = false;
                if (!retrieved && TryHookSurface(ref release, out releaseSupport))
                    water = releaseSupport == null && _water != null && _water.TryHeight(release, out var height) &&
                        Mathf.Abs(release.y - height) < 0.08f;
                if (retrieved)
                {
                    var frame = WorldItem.GetPhysicsFrame(support);
                    release += frame.MultiplyVector(Vector3.right) * ((i % 3 - 1) * 0.2f) +
                        frame.MultiplyVector(Vector3.forward) * (0.35f + i / 3 * 0.2f);
                }
                item.ReleaseFromHookServer(this, release, releaseSupport, water);
            }
            _hookItems.Clear(); _claimedThisThrow.Clear(); _hook.Value = default; _serverReeling = false; _chargeStarted = -1d;
        }

        [ClientRpc] private void ShotClientRpc(int id, Vector3 origin, Vector3 velocity, double started)
        {
            var visual = EquipmentProjectileVisual.Create(this, origin, velocity, bulletGravity, started,
                bulletLifetime, bulletRadius, metalMaterial, effectMaterial);
            _bulletVisuals[id] = visual;
            if (!IsOwner)
                CannonEffects.Muzzle(_view != null ? _view.MuzzlePosition(origin) : origin, velocity.normalized, effectMaterial);
        }
        [ClientRpc] private void BulletImpactClientRpc(int id, Vector3 point, Vector3 normal, bool water, bool show, double at)
        {
            if (!_bulletVisuals.TryGetValue(id, out var visual)) return;
            if (visual != null) visual.SetImpact(point, normal, water, show, at, waterSplashPrefab, effectMaterial, metalMaterial);
            _bulletVisuals.Remove(id);
        }
        [ClientRpc] private void ToolEffectClientRpc(Vector3 position, bool water)
        { CannonEffects.Hit(position, Vector3.up, water, water ? waterSplashPrefab : null, effectMaterial, metalMaterial); }
        [ClientRpc] private void PourClientRpc(Vector3 origin, Vector3 direction)
        { EquipmentProjectileVisual.CreateWaterPour(origin, direction, effectMaterial); }
        [ClientRpc] private void BlockClientRpc()
        { if (_view != null) _view.BlockImpact(); }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z);
    }
}
