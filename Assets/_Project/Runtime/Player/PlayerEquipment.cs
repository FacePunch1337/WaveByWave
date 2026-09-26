using System;
using System.Collections.Generic;
using StylizedWater3;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using WaveByWave.Combat;
using WaveByWave.Items;
using WaveByWave.Ships;
using WaveByWave.UI;

namespace WaveByWave.Player
{
    public interface IBucketWaterSource
    {
        float ScoopWaterServer(float litres, Vector3 point);
        void AddWaterServer(float litres, Vector3 point);
    }

    [Serializable] public sealed class BucketWaterEvent : UnityEvent<Vector3, float> { }

    [DefaultExecutionOrder(9600)]
    [RequireComponent(typeof(PlayerInventory), typeof(NetworkPlayerController), typeof(NetworkHealth))]
    public sealed class PlayerEquipment : NetworkBehaviour, IEquipmentDamageReceiver
    {
        [SerializeField] private EquipmentMotionSet motions;
        [SerializeField] private GameObject firstPersonHandsPrefab;
        [SerializeField] private WaveProfile waterWaveProfile;
        [SerializeField] private Material metalMaterial, effectMaterial, handMaterial, sleeveMaterial;
        [SerializeField] private GameObject waterSplashPrefab;
        [Header("Эффекты — только prefab")]
        [SerializeField] private GameObject musketProjectilePrefab;
        [SerializeField] private GameObject muzzleEffectPrefab;
        [SerializeField] private GameObject weaponImpactEffectPrefab;
        [SerializeField] private GameObject shovelDigEffectPrefab;
        [SerializeField] private GameObject bucketPourEffectPrefab;
        [SerializeField] private GameObject hookRopePrefab;
        [Header("Сабля и стамина")]
        [SerializeField, Min(1f)] private float maximumStamina = 100f;
        [SerializeField, Min(0f)] private float staminaRecovery = 18f, blockDrainPerSecond = 7f, swingStamina = 12f;
        [SerializeField, Min(0f)] private float successfulBlockStamina = 15f;
        [SerializeField, Min(0f)] private float sprintDrainPerSecond = 14f;
        [SerializeField, Min(0f)] private float swimDrainPerSecond = 8f;
        [SerializeField, Min(0f)] private float drowningDamagePerSecond = 12f;
        [SerializeField, Min(0.1f)] private float drowningDamageInterval = 1f;
        [SerializeField, Min(0.1f)] private float swordRange = 2.2f, swordSwingDuration = 0.55f;
        [Tooltip("Тестовый режим: одно нажатие ПКМ переключает блок мечом или прицеливание мушкетом.")]
        [SerializeField] private bool toggleSecondaryActionForTesting;
        [Header("Мушкет")]
        [SerializeField, Min(1f)] private float bulletSpeed = 95f, bulletGravity = 9.81f;
        [SerializeField, Min(0.1f)] private float reloadDuration = 2.5f;
        [SerializeField, Min(0.01f)] private float bulletRadius = 0.035f;
        [SerializeField, Min(1f)] private float bulletLifetime = 6f;
        [Header("Крюк")]
        [SerializeField, Min(0.1f)] private float hookChargeDuration = 1.5f;
        [SerializeField, Min(1f)] private float hookMinimumSpeed = 8f, hookMaximumSpeed = 22f;
        [SerializeField, Min(1f)] private float hookGravity = 12f, hookReelSpeed = 8f;
        [SerializeField, Min(1f), Tooltip("Скорость быстрого возврата крюка по воздуху на ПКМ.")]
        private float hookRecallSpeed = 32f;
        [SerializeField, Min(1f)] private float maximumRopeLength = 45f;
        [SerializeField, Min(0.05f)] private float hookPickupRadius = 0.65f;
        [SerializeField, Min(0.1f), Tooltip("На этой горизонтальной дистанции крюк перестаёт скользить по поверхности и поднимается прямо к руке.")]
        private float hookLiftHorizontalDistance = 0.9f;
        [SerializeField, Min(0.1f)] private float hookSurfaceHeightSpeed = 5f;
        [SerializeField, Min(0.1f)] private float hookReelAcceleration = 24f;
        [Header("Лопата")]
        [SerializeField, Min(0.25f), Tooltip("Максимальная дистанция копания от камеры точно по центру прицела.")]
        private float shovelDigDistance = 3.5f;
        [Header("Инструменты — события для дальнейшей игровой логики")]
        [SerializeField, Min(0.1f)] private float bucketLitres = 10f;
        [SerializeField] private BucketWaterEvent onWaterScooped = new(), onWaterPoured = new();
        [SerializeField] private UnityEvent<Vector3> onDig = new();
        [SerializeField] private LayerMask hitLayers = ~0;

        private readonly NetworkVariable<float> _stamina = new(100f);
        private readonly NetworkVariable<bool> _blocking = new(), _aiming = new(), _bucketFull = new();
        private readonly NetworkVariable<float> _bucketAmount = new();
        private double _lastRepairPulse;
        // Зарядка крюка видна всем клиентам: флаг и время начала (серверное время).
        private readonly NetworkVariable<bool> _charging = new();
        private readonly NetworkVariable<double> _chargeStartNet = new();
        private readonly NetworkVariable<double> _reloadEnd = new();
        private readonly NetworkVariable<float> _reloadLength = new(2.5f);
        [SerializeField, Min(0.02f)] private float burstShotInterval = 0.12f;
        private int _burstRemaining;
        private double _nextBurstShot;
        private Vector3 _burstEyeOffset;
        private float SwingDuration => swordSwingDuration / _player.AttackSpeedMultiplier;
        private float ShotDuration => 0.2f / _player.AttackSpeedMultiplier;
        private float ReloadDuration => reloadDuration / _player.ReloadSpeedMultiplier;
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
        private readonly List<int> _stressHookItems = new();
        private NetworkPlayerController _player;
        private PlayerInventory _inventory;
        private NetworkHealth _health;
        private HeldItemView _view;
        private EquipmentWaterQuery _water;
        private CapsuleCollider _combatHitbox;
        private double _cooldown, _recoverAfter, _inputHeartbeat, _serverHeartbeat, _chargeStarted = -1d;
        private double _hookSampleTime, _hookFlightStarted;
        private bool _localBlock, _localAim, _localReel, _localCharge, _sentCharge;
        private bool _secondaryActionLatched;
        private float _localChargeStarted;
        private int _localSlot = -1, _serverSlot = -1, _nextBullet;
        private ItemDefinition _serverItem;
        private ItemDefinition _localItem;
        private Vector3 _hookPosition;
        private Vector3 _hookReelVelocity;
        private Vector3 _renderedHook;
        private int _hookRenderFrame = -1;
        private HookPhase _renderPhase;
        private NetworkObjectReference _tetherReference;
        public NetworkObjectReference TetherReference => _tetherReference;
        private bool _blockExhausted;
        private bool _localMovementSprinting, _localMovementSwimming;
        private bool _serverMovementSprinting, _serverMovementSwimming;
        private float _nextMovementStateSend;
        private double _movementStateHeartbeat, _nextDrowningDamage;
        private double _swordHitAt;
        private float _swordDamage;
        private EquipmentMotionState _predictedMotion;
        private double _localActionNext;
        private double Now => NetworkManager.ServerTime.Time;
        public EquipmentMotionSet Motions => motions;
        public GameObject FirstPersonHandsPrefab => firstPersonHandsPrefab;
        public GameObject HookRopePrefab => hookRopePrefab;
        public WaveProfile WaterWaveProfile => waterWaveProfile;
        public Material MetalMaterial => metalMaterial;
        public Material EffectMaterial => effectMaterial;
        public Material HandMaterial => handMaterial;
        public Material SleeveMaterial => sleeveMaterial;
        public float Stamina => _stamina.Value;
        public float MaximumStamina => maximumStamina +
            (_player != null ? _player.RingValue(PlayerRingStat.Stamina) : 0f);
        public void ApplyRingStaminaBonusServer(float bonus)
            => ApplyRingStaminaChangeServer(bonus);

        public void ApplyRingStaminaChangeServer(float difference)
        {
            if (!IsServer) return;
            _stamina.Value = Mathf.Min(MaximumStamina,
                difference > 0f ? _stamina.Value + difference : _stamina.Value);
        }
        public bool CanJump => true;
        public bool IsBlocking => _blocking.Value;
        public bool Available => _available.Value;
        public bool IsAiming => IsOwner ? _localAim : _aiming.Value;
        public bool BucketFull => _bucketFull.Value;
        public float BucketLitres => _bucketAmount.Value;
        public bool ChargingHook => IsOwner ? _localCharge : _charging.Value;
        public float HookCharge => IsOwner
            ? (_localCharge ? Mathf.Clamp01((Time.unscaledTime - _localChargeStarted) / hookChargeDuration) : 0f)
            : (_charging.Value ? Mathf.Clamp01((float)(Now - _chargeStartNet.Value) / hookChargeDuration) : 0f);
        public float ReloadProgress => Mathf.Clamp01(1f - (float)(_reloadEnd.Value - Now) / Mathf.Max(0.01f, _reloadLength.Value));
        public bool Reloading => IsSpawned && _reloadEnd.Value > Now;
        public EquipmentMotionState Motion => _motion.Value;
        public EquipmentMotionState DisplayMotion => IsOwner && _predictedMotion.Action != EquipmentAction.None &&
            Now < _predictedMotion.Started + _predictedMotion.Duration ? _predictedMotion : _motion.Value;
        private float DrinkDuration => motions != null && motions.drink != null
            ? Mathf.Max(0.1f, motions.drink.length) : 1.1f;
        public bool TryPredictDrink()
        {
            if (!IsOwner || !IsSpawned || !_available.Value || Now < _localActionNext) return false;
            var duration = DrinkDuration;
            _predictedMotion = new EquipmentMotionState
            { Action = EquipmentAction.Drink, Started = Now, Duration = duration };
            _localActionNext = Now + duration;
            return true;
        }
        public bool CanDrinkServer() => IsServer && Time.timeScale > 0f && CanActServer() && Now >= _cooldown;
        public void PlayDrinkServer()
        {
            if (IsServer) PlayServer(EquipmentAction.Drink, DrinkDuration);
        }
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
                var smooth = (_hook.Value.Phase == HookPhase.Flying && _hook.Value.Pulling &&
                              _renderPhase == HookPhase.Flying) ||
                             (_hook.Value.Phase == HookPhase.Reeling && _renderPhase == HookPhase.Reeling) ||
                    (_hook.Value.Phase == HookPhase.Returning && _renderPhase == HookPhase.Returning);
                _renderedHook = smooth
                    ? Vector3.Lerp(_renderedHook, target, 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime)) : target;
                _renderPhase = _hook.Value.Phase;
                return _renderedHook;
            }
        }
        public static bool InputCaptured => ShipFlooding.VoyageOver || NetworkPlayerController.RingChoiceOpen ||
            PlayerProgressionUI.MenuOpen || SessionMenuPresenter.InputCaptured || EquipmentAdminPanel.InputCaptured ||
            WaveByWave.Customization.CustomizationMenu.InputCaptured ||
            WaveByWave.Generation.OceanLoadingCurtain.InputCaptured;
        public event Action<float> SuccessfulBlock;

        private sealed class Bullet
        {
            public int Id;
            public Vector3 Origin, Velocity;
            public double Started, Simulated;
            public float Damage;
            public bool EnteredWater;
            public Vector3 Evaluate(double at, float gravity)
            { var t = (float)(at - Started); return Origin + Velocity * t + Vector3.down * (0.5f * gravity * t * t); }
        }

        private void Awake()
        {
            _player = GetComponent<NetworkPlayerController>();
            _inventory = GetComponent<PlayerInventory>();
            _health = GetComponent<NetworkHealth>();
        }
        public override void OnNetworkSpawn()
        {
            _tetherReference = new NetworkObjectReference(NetworkObject);
            if (motions == null) motions = Resources.Load<EquipmentMotionSet>("EquipmentMotions");
            if (IsServer)
            {
                _stamina.Value = MaximumStamina; _water = new EquipmentWaterQuery(waterWaveProfile);
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
            _localMovementSprinting = _localMovementSwimming = false;
            _serverMovementSprinting = _serverMovementSwimming = false;
            if (_combatHitbox != null) { _combatHitbox.enabled = false; Destroy(_combatHitbox.gameObject); }
            _water?.Dispose(); _water = null;
            foreach (var visual in _bulletVisuals.Values) if (visual != null) Destroy(visual.gameObject);
            _bulletVisuals.Clear(); _bullets.Clear();
            if (_view != null) Destroy(_view);
        }

        private void Update()
        {
            if (!IsSpawned) return;
            if (!IsServer || Time.timeScale <= 0f) return;
            if (_combatHitbox != null)
            {
                var feet = FeetServer(out var support);
                var rotation = support != null ? WorldItem.GetPhysicsFrame(support).rotation : transform.rotation;
                _combatHitbox.transform.SetPositionAndRotation(feet, rotation);
                _combatHitbox.enabled = _inventory.Health > 0f;
            }
            var selected = _inventory.ServerSelectedIndex;
            ItemDefinition item = null;
            if (!_inventory.IsCarryingChest) _inventory.TryGetDefinition(selected, out item);
            RefreshSelectedServer(selected, item);
            _available.Value = CanActServer();
            if (!_available.Value)
            {
                StopHeldServer();
                _swordHitAt = 0d;
                if (_hook.Value.Phase != HookPhase.Stowed) ResetHookServer();
            }
            if (!_available.Value || item == null || item.EquipmentKind != ItemEquipmentKind.Musket)
                _burstRemaining = 0;
            if (_burstRemaining > 0 && Now >= _nextBurstShot)
            {
                FireMusketServer(item, FeetServer(out _) + _burstEyeOffset, _look.Value, true);
                _burstRemaining--;
                _nextBurstShot = Now + burstShotInterval / _player.AttackSpeedMultiplier;
                // A slow frame delays subsequent shots instead of emitting the whole burst at once.
                _reloadEnd.Value = Now + _burstRemaining *
                    burstShotInterval / _player.AttackSpeedMultiplier + ShotDuration + ReloadDuration;
            }
            if (_swordHitAt > 0d && Now >= _swordHitAt)
            {
                _swordHitAt = 0d;
                SwordHitServer(FeetServer(out _) + Vector3.up * 1.25f, _look.Value, _swordDamage);
            }
            if (Now - _serverHeartbeat > 0.5d) StopHeldServer();
            if (Now - _movementStateHeartbeat > 0.75d)
                _serverMovementSprinting = _serverMovementSwimming = false;
            var dt = Mathf.Min(Time.deltaTime, 0.1f);
            var staminaDrain = 0f;
            if (_blocking.Value)
            {
                staminaDrain += blockDrainPerSecond;
            }
            if (_serverMovementSprinting) staminaDrain += sprintDrainPerSecond;
            if (_serverMovementSwimming) staminaDrain += swimDrainPerSecond;
            if (staminaDrain > 0f)
            {
                _stamina.Value = Mathf.Max(0f, _stamina.Value - staminaDrain * dt);
                _recoverAfter = Now + 0.6d;
                if (_blocking.Value && _stamina.Value <= 0f)
                {
                    _blocking.Value = false;
                    _blockExhausted = true;
                }
            }
            else if (Now >= _recoverAfter)
                _stamina.Value = Mathf.Min(MaximumStamina, _stamina.Value +
                    (staminaRecovery + _player.RingValue(PlayerRingStat.Regeneration)) * dt);
            if (_serverMovementSwimming && _stamina.Value <= 0f &&
                (_health == null || !_health.IsDead) && Now >= _nextDrowningDamage)
            {
                _nextDrowningDamage = Now + drowningDamageInterval;
                _health?.ApplyDamageServer(drowningDamagePerSecond * drowningDamageInterval,
                    transform.position + Vector3.down, 0f);
            }
            else if (!_serverMovementSwimming || _stamina.Value > 0f)
                _nextDrowningDamage = Now;
            SimulateBullets();
            SimulateHook();
        }

        public void SetMovementExertion(bool sprinting, bool swimming)
        {
            if (!IsOwner || !IsSpawned)
                return;
            var changed = sprinting != _localMovementSprinting || swimming != _localMovementSwimming;
            if (!changed && Time.unscaledTime < _nextMovementStateSend)
                return;
            _localMovementSprinting = sprinting;
            _localMovementSwimming = swimming;
            _nextMovementStateSend = Time.unscaledTime + 0.25f;
            SetMovementExertionServerRpc(sprinting, swimming);
        }

        public bool TryUseJumpStamina()
        {
            return IsOwner && IsSpawned && (_health == null || !_health.IsDead);
        }

        [ServerRpc]
        private void SetMovementExertionServerRpc(bool sprinting, bool swimming)
        {
            _serverMovementSprinting = sprinting && !swimming && (_health == null || !_health.IsDead);
            _serverMovementSwimming = swimming && (_health == null || !_health.IsDead);
            _movementStateHeartbeat = Now;
        }
        private void LateUpdate()
        {
            if (IsSpawned && IsOwner) ReadOwnerInput();
        }
        private void RefreshSelectedServer(int slot, ItemDefinition item)
        {
            if (_serverSlot == slot && _serverItem == item) return;
            StopHeldServer(); _swordHitAt = 0d; _burstRemaining = 0; _blockExhausted = false;
            if (_hook.Value.Phase != HookPhase.Stowed) ResetHookServer();
            if (_motion.Value.Action != EquipmentAction.Drink ||
                Now >= _motion.Value.Started + _motion.Value.Duration)
                _motion.Value = default;
            _serverSlot = slot; _serverItem = item;
        }

        private void ReadOwnerInput()
        {
            var enabledInput = !InputCaptured && !_player.IsAtControlStation && _inventory.Health > 0f;
            var mouse = Mouse.current;
            ItemDefinition item = null;
            if (!_inventory.IsCarryingChest) _inventory.TryGetDefinition(_inventory.SelectedIndex, out item);
            if (_localSlot != _inventory.SelectedIndex || _localItem != item || !enabledInput || mouse == null)
            {
                if (_localSlot != _inventory.SelectedIndex || _localItem != item || _localBlock || _localAim || _localReel || _localCharge)
                {
                    if (_localSlot != _inventory.SelectedIndex || _localItem != item || !enabledInput)
                        _secondaryActionLatched = false;
                    _localSlot = _inventory.SelectedIndex;
                    _localItem = item;
                    if (_predictedMotion.Action != EquipmentAction.Drink ||
                        Now >= _predictedMotion.Started + _predictedMotion.Duration)
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
            var hasSecondaryAction = item.EquipmentKind == ItemEquipmentKind.Sword ||
                                     item.EquipmentKind == ItemEquipmentKind.Musket;
            if (toggleSecondaryActionForTesting && hasSecondaryAction && mouse.rightButton.wasPressedThisFrame)
                _secondaryActionLatched = !_secondaryActionLatched;
            if (!toggleSecondaryActionForTesting)
                _secondaryActionLatched = false;
            var secondaryHeld = toggleSecondaryActionForTesting ? _secondaryActionLatched : mouse.rightButton.isPressed;
            var block = item.EquipmentKind == ItemEquipmentKind.Sword && secondaryHeld;
            var aim = item.EquipmentKind == ItemEquipmentKind.Musket && secondaryHeld;
            var reel = item.EquipmentKind == ItemEquipmentKind.Hook && _hook.Value.Phase != HookPhase.Stowed &&
                _hook.Value.Phase != HookPhase.Returning &&
                mouse.leftButton.isPressed;
            if (item.EquipmentKind == ItemEquipmentKind.Hook && mouse.rightButton.wasPressedThisFrame)
            {
                _localCharge = false;
                _localReel = false;
                reel = false;
                RecallHookServerRpc(_inventory.SelectedIndex, _inventory.SelectionRevision);
            }
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
                SendHeldInput(block, aim, reel, _localCharge,
                    item.SupplyKind == SupplyKind.Plank && mouse.leftButton.isPressed);
                _inputHeartbeat = Now + 0.1d;
            }
            if (mouse.leftButton.wasPressedThisFrame)
            {
                var action = item.EquipmentKind switch
                {
                    ItemEquipmentKind.Sword => EquipmentAction.SwordSwing,
                    ItemEquipmentKind.Musket => EquipmentAction.MusketShot,
                    ItemEquipmentKind.Bucket => BucketFull ? EquipmentAction.BucketSplash : EquipmentAction.BucketScoop,
                    ItemEquipmentKind.Shovel => EquipmentAction.ShovelDig,
                    _ => EquipmentAction.None
                };
                if (action != EquipmentAction.None) SendAction(action);
            }
        }
        private void SendHeldInput(bool block, bool aim, bool reel, bool charge, bool repair = false)
        {
            _sentCharge = charge;
            BuildAim(out var origin, out var direction, out var support);
            HeldInputServerRpc(_inventory.SelectedIndex, _inventory.SelectionRevision, block, aim, reel, charge, repair,
                origin, direction, support);
        }
        private void SendAction(EquipmentAction action)
        {
            if (_inventory.IsCarryingChest || Now < _localActionNext ||
                !_inventory.TryGetDefinition(_inventory.SelectedIndex, out _)) return;
            if (action == EquipmentAction.MusketShot && Reloading ||
                action == EquipmentAction.SwordSwing && (IsBlocking || Stamina < swingStamina) ||
                action == EquipmentAction.BucketScoop && BucketFull ||
                action == EquipmentAction.BucketSplash && !BucketFull) return;
            var duration = action switch
            {
                EquipmentAction.SwordSwing => SwingDuration,
                EquipmentAction.MusketShot => ShotDuration,
                EquipmentAction.HookThrow => 0.4f,
                EquipmentAction.BucketSplash => 0.65f,
                _ => 0.8f
            };
            _predictedMotion = new EquipmentMotionState { Action = action, Started = Now, Duration = duration };
            _localActionNext = Now + duration;
            BuildAim(out var origin, out var direction, out var support);
            if (action == EquipmentAction.MusketShot && _player.OwnerView != null)
                PlayMusketMuzzle(_player.OwnerView.position + _player.OwnerView.forward * 0.8f,
                    _player.OwnerView.forward);
            ActionServerRpc(action, _inventory.SelectedIndex, _inventory.SelectionRevision, origin, direction, support);
        }

        private void PlayMusketMuzzle(Vector3 fallbackPosition, Vector3 fallbackDirection)
        {
            if (_view != null && _view.TryGetMuzzlePose(out var muzzle))
            {
                fallbackPosition = muzzle.position;
                fallbackDirection = muzzle.rotation * Vector3.forward;
            }
            CannonEffects.Muzzle(fallbackPosition, fallbackDirection, muzzleEffectPrefab);
        }
        private void BuildAim(out Vector3 origin, out Vector3 direction, out NetworkObjectReference supportReference)
        {
            _player.GetItemDropPose(out var feet, out direction, out var support);
            var view = _player.OwnerView;
            if (view != null && view.TryGetComponent<Camera>(out var camera))
            {
                var ray = camera.ViewportPointToRay(CannonReloadProgress.AimViewportPoint);
                origin = ray.origin;
                direction = ray.direction;
            }
            else
            {
                origin = view != null ? view.position : feet + Vector3.up * 1.35f;
                direction = view != null ? view.forward : direction;
            }
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
                (!ship.TryGetComponent<ShipCannonBattery>(out var battery) ||
                    !battery.VoyageEnded && battery.GetOperatorCannon(OwnerClientId) < 0));
        }
        [ServerRpc]
        private void HeldInputServerRpc(int slot, uint revision, bool block, bool aim, bool reel, bool charge, bool repair,
            Vector3 requestedOrigin, Vector3 requestedDirection, NetworkObjectReference support)
        {
            if (Time.timeScale <= 0f || !_inventory.ApplySelectionServer(slot, revision) || !CanActServer() ||
                !_inventory.TryGetDefinition(slot, out var item) ||
                !ResolveAimServer(requestedOrigin, requestedDirection, support, out var origin, out var direction)) return;
            RefreshSelectedServer(slot, item);
            _serverHeartbeat = Now;
            _look.Value = direction;
            if (Now - _lastRepairPulse >= 0.08d)
            {
                var elapsed = Mathf.Min(0.15f, (float)(Now - _lastRepairPulse));
                _lastRepairPulse = Now;
                if (repair && item.SupplyKind == SupplyKind.Plank && Time.timeScale > 0f)
                    ShipFlooding.RepairTarget(origin, direction, out _)?.RepairServer(_player, _inventory, origin, direction, elapsed);
            }
            if (!block) _blockExhausted = false;
            _blocking.Value = block && !_blockExhausted && item.EquipmentKind == ItemEquipmentKind.Sword && _stamina.Value > 0f && Now >= _cooldown;
            _aiming.Value = aim && item.EquipmentKind == ItemEquipmentKind.Musket;
            _serverReeling = reel && item.EquipmentKind == ItemEquipmentKind.Hook;
            if (charge && item.EquipmentKind == ItemEquipmentKind.Hook && _hook.Value.Phase == HookPhase.Stowed)
            {
                if (_chargeStarted < 0d) { _chargeStarted = Now; _chargeStartNet.Value = Now; }
                _charging.Value = true;
            }
            else { _chargeStarted = -1d; _charging.Value = false; }
        }
        private bool _serverReeling;
        [ServerRpc]
        private void RecallHookServerRpc(int slot, uint revision)
        {
            if (!_inventory.ApplySelectionServer(slot, revision) || _inventory.Health <= 0f ||
                !_inventory.TryGetDefinition(slot, out var item) ||
                item.EquipmentKind != ItemEquipmentKind.Hook)
                return;

            RefreshSelectedServer(slot, item);
            _serverHeartbeat = Now;
            _charging.Value = false;
            _chargeStarted = -1d;
            _serverReeling = false;
            if (_hook.Value.Phase != HookPhase.Stowed)
                BeginHookRecallServer();
        }

        private void StopHeldServer()
        { _blocking.Value = _aiming.Value = false; _charging.Value = false; _serverReeling = false; _chargeStarted = -1d; }
        [ServerRpc]
        private void ActionServerRpc(EquipmentAction action, int slot, uint revision,
            Vector3 requestedOrigin, Vector3 requestedDirection, NetworkObjectReference support)
        {
            if (Time.timeScale <= 0f || !_inventory.ApplySelectionServer(slot, revision) || !CanActServer() ||
                !_inventory.TryGetDefinition(slot, out var item) || Now < _cooldown ||
                !ResolveAimServer(requestedOrigin, requestedDirection, support, out var origin, out var direction)) return;
            RefreshSelectedServer(slot, item);
            _look.Value = direction;
            switch (action)
            {
                case EquipmentAction.SwordSwing when item.EquipmentKind == ItemEquipmentKind.Sword:
                    if (_blocking.Value || _stamina.Value < swingStamina) return;
                    _stamina.Value -= swingStamina; _recoverAfter = Now + 0.7d;
                    PlayServer(action, SwingDuration);
                    _swordHitAt = Now + SwingDuration * 0.4f;
                    _swordDamage = _player.WeaponDamage(item);
                    break;
                case EquipmentAction.MusketShot when item.EquipmentKind == ItemEquipmentKind.Musket:
                    if (Reloading) return;
                    _burstRemaining = _player.ProjectileCount - 1;
                    _burstEyeOffset = origin - FeetServer(out _);
                    _nextBurstShot = Now + burstShotInterval / _player.AttackSpeedMultiplier;
                    _reloadLength.Value = ShotDuration + ReloadDuration + _burstRemaining * burstShotInterval / _player.AttackSpeedMultiplier;
                    _reloadEnd.Value = Now + _reloadLength.Value;
                    FireMusketServer(item, origin, direction, false);
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
                    _hook.Value = new EquipmentHookState
                    {
                        Phase = HookPhase.Flying,
                        Origin = _hookPosition,
                        Velocity = launch,
                        Started = Now,
                        FlightFacingVelocity = launch,
                        FlightFacingStarted = Now
                    };
                    _hookSampleTime = _hookFlightStarted = Now; _chargeStarted = -1d;
                    _charging.Value = false;
                    PlayServer(action, 0.4f);
                    break;
                case EquipmentAction.BucketScoop when item.EquipmentKind == ItemEquipmentKind.Bucket:
                    if (_bucketFull.Value) return;
                    var amount = ScoopBucketServer(origin, direction, out var scoopPoint);
                    PlayServer(action, 0.8f);
                    if (amount <= 0f) return;
                    _bucketAmount.Value = amount;
                    _bucketFull.Value = true; onWaterScooped.Invoke(scoopPoint, amount);
                    ToolEffectClientRpc(scoopPoint, true);
                    break;
                case EquipmentAction.BucketSplash when item.EquipmentKind == ItemEquipmentKind.Bucket:
                    if (!_bucketFull.Value) return;
                    _bucketFull.Value = false;
                    PlayServer(action, 0.65f);
                    var pourOrigin = origin + direction * 0.35f - Vector3.up * 0.2f;
                    var pourPoint = PourBucketServer(pourOrigin, direction, _bucketAmount.Value);
                    onWaterPoured.Invoke(pourPoint, _bucketAmount.Value);
                    _bucketAmount.Value = 0f;
                    PourClientRpc(pourOrigin, direction);
                    break;
                case EquipmentAction.ShovelDig when item.EquipmentKind == ItemEquipmentKind.Shovel:
                    // Unlike item throwing, digging must follow the reticle exactly. The old
                    // forced downward bias made the shovel excavate below the aimed point.
                    var end = origin + direction * shovelDigDistance;
                    if (SegmentHit(origin, end, 0.02f, null, out var digHit, out _))
                    {
                        if (digHit.collider.GetComponentInParent<WaveByWave.Generation.ProceduralIsland>() is { } island)
                            WaveByWave.Generation.OceanWorldDirector.Instance?.DigServer(
                                island, digHit.point, digHit.normal);
                        onDig.Invoke(digHit.point); ToolEffectClientRpc(digHit.point, false);
                    }
                    PlayServer(action, 0.8f);
                    break;
            }
        }

        private void FireMusketServer(ItemDefinition item, Vector3 origin, Vector3 direction, bool extra)
        {
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
            var bullet = new Bullet
            {
                Id = ++_nextBullet,
                Origin = muzzle,
                Velocity = (aimPoint - muzzle).normalized * bulletSpeed + inherited,
                Started = Now,
                Simulated = Now,
                Damage = _player.WeaponDamage(item)
            };
            _bullets.Add(bullet);
            PlayServer(EquipmentAction.MusketShot, ShotDuration);
            ShotClientRpc(bullet.Id, bullet.Origin, bullet.Velocity, bullet.Started, extra);
        }

        private void PlayServer(EquipmentAction action, float duration)
        {
            _cooldown = Now + duration;
            _motion.Value = new EquipmentMotionState
            {
                Action = action,
                Sequence = _motion.Value.Sequence + 1,
                Started = Now,
                Duration = duration
            };
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
        public void ReceiveEquipmentHitServer(float damage, Vector3 attackerPosition, bool canBlock = true, Vector3? impactPoint = null)
        {
            if (!IsServer || !Finite(damage) || damage <= 0f) return;
            if (!canBlock || !TryBlockHitServer(damage, attackerPosition))
                _health?.ApplyDamageServer(damage, attackerPosition, 1f, impactPoint);
        }
        private void SwordHitServer(Vector3 origin, Vector3 direction, float damage)
        {
            var range = swordRange * _player.MeleeAreaMultiplier;
            WaveByWave.Enemies.DotsEnemyRuntime.Instance?.Melee(origin, direction, range, damage);
            var count = Physics.OverlapSphereNonAlloc(origin, range, _targets, hitLayers, QueryTriggerInteraction.Collide);
            _swordTargets.Clear();
            for (var i = 0; i < count; i++)
            {
                var target = _targets[i];
                if (target.GetComponentInParent<NetworkPlayerController>() == _player) continue;
                if (!EquipmentDamageReceiverUtility.TryGet(target, out var receiver, out var component) ||
                    _swordTargets.Contains(component)) continue;
                Vector3 contact;
                if (target is MeshCollider mesh && !mesh.convex)
                {
                    // PhysX ClosestPoint does not support non-convex ship/terrain meshes.
                    // An actual surface ray avoids both the warning and bounds-only hits.
                    var aim = target.bounds.ClosestPoint(origin + direction * range * 0.7f) - origin;
                    if (aim.sqrMagnitude < 0.0001f) aim = direction;
                    if (!target.Raycast(new Ray(origin, aim.normalized), out var hit, range)) continue;
                    contact = hit.point;
                }
                else contact = target.ClosestPoint(origin + direction * range * 0.7f);
                var toward = contact - origin;
                if (toward.sqrMagnitude > range * range || Vector3.Dot(toward.normalized, direction) < 0.35f) continue;
                if (!HasSolidBetween(origin, contact, target) && _swordTargets.Add(component))
                    receiver.ReceiveEquipmentHitServer(damage, origin, true, contact);
            }
        }
        private float ScoopBucketServer(Vector3 origin, Vector3 direction, out Vector3 point)
        {
            if (ShipFlooding.RayWater(origin, direction, 2.8f, out var compartment, out point) && !HasSolidBetween(origin, point))
                return compartment.ScoopWaterServer(bucketLitres, point);
            var probe = FeetServer(out _) + Vector3.up * 0.35f + Vector3.ProjectOnPlane(direction, Vector3.up).normalized * 0.6f;
            point = probe;
            if (!_water.TryHeight(probe, out var height) || probe.y > height + 0.55f) return 0f;
            point.y = height;
            if (HasSolidBetween(origin, point)) return 0f;
            compartment = ShipFlooding.CompartmentAt(point);
            return compartment != null ? compartment.ScoopWaterServer(bucketLitres, point) : bucketLitres;
        }

        private Vector3 PourBucketServer(Vector3 origin, Vector3 direction, float litres)
        {
            // This matches the forward splash: a short ballistic stream, including
            // the first deck/bulkhead hit. Water thrown onto the deck drains into the hull.
            var velocity = direction * 4.5f + Vector3.up * 1.3f;
            var previous = origin;
            for (var step = 1; step <= 20; step++)
            {
                var t = step * 0.1f;
                var next = origin + velocity * t + Vector3.down * (4.905f * t * t);
                var delta = next - previous;
                var hitSolid = SegmentHit(previous, next, 0.025f, null, out var hit, out var fraction);
                if (ShipFlooding.RayWater(previous, delta.normalized, delta.magnitude * (hitSolid ? fraction : 1f),
                    out var waterShip, out var surfacePoint))
                { waterShip.AddWaterServer(litres, surfacePoint); return surfacePoint; }
                if (hitSolid)
                {
                    var ship = hit.collider.GetComponentInParent<ShipFlooding>();
                    if (ship != null && ship.WaterVolume != null && ship.WaterVolume.ContainsColumn(hit.point))
                        ship.AddWaterServer(litres, hit.point);
                    return hit.point;
                }
                if (_water.Crossing(previous, next, out var oceanPoint, out _))
                {
                    var ship = ShipFlooding.CompartmentAt(oceanPoint);
                    if (ship == null) return oceanPoint;
                    // The ocean is masked here: continue until the stream meets deck or internal water.
                }
                previous = next;
            }
            return previous;
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
                // A ship can contain nested networked modules (cannons, capstan, controls).
                // Component-in-parent would stop at such a child NetworkObject and fail to
                // recognize the collider as part of the ship we intentionally ignore.
                if (ignoredShip != null &&
                    (hit.collider.transform == ignoredShip.transform ||
                     hit.collider.transform.IsChildOf(ignoredShip.transform))) continue;
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
                    var waterPoint = default(Vector3);
                    var waterFraction = 1f;
                    var entersWater = !b.EnteredWater &&
                        _water.Crossing(from, to, out waterPoint, out waterFraction);
                    var enemies = WaveByWave.Enemies.DotsEnemyRuntime.Instance;
                    if (enemies != null && enemies.RayHit(from, to, bulletRadius, out var enemyId, out var enemyFraction) &&
                        (!solid || enemyFraction < solidFraction))
                    {
                        if (entersWater && waterFraction < enemyFraction)
                        {
                            b.EnteredWater = true;
                            BulletWaterEntryClientRpc(b.Id, waterPoint,
                                b.Simulated + step * waterFraction);
                        }
                        var enemyPoint = Vector3.Lerp(from, to, enemyFraction);
                        enemies.Damage(enemyId, b.Damage, b.Origin, enemyPoint);
                        BulletImpactClientRpc(b.Id, enemyPoint, (from - to).normalized, false, true,
                            b.Simulated + step * enemyFraction);
                        finished = true;
                        break;
                    }
                    if (entersWater && (!solid || waterFraction < solidFraction))
                    {
                        b.EnteredWater = true;
                        BulletWaterEntryClientRpc(b.Id, waterPoint,
                            b.Simulated + step * waterFraction);
                    }
                    if (solid || next - b.Started >= bulletLifetime)
                    {
                        var point = solid ? hit.point : to;
                        if (solid)
                            if (EquipmentDamageReceiverUtility.TryGet(hit.collider, out var receiver, out _))
                                receiver.ReceiveEquipmentHitServer(b.Damage, b.Origin, false, hit.point);
                        BulletImpactClientRpc(b.Id, point, solid ? hit.normal : Vector3.up,
                            false, solid, b.Simulated + step * (solid ? solidFraction : 1f));
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
            var feet = FeetServer(out var playerSupport);
            var hand = feet + Vector3.up * 1.1f;
            if (state.Phase == HookPhase.Flying)
            {
                const double step = 1d / 60d;
                var stateChanged = state.Pulling != _serverReeling;
                state.Pulling = _serverReeling;
                for (var n = 0; n < 8 && _hookSampleTime + step <= Now; n++)
                {
                    var next = _hookSampleTime + step;
                    var to = state.Evaluate(next, hookGravity);
                    var flightVelocity = Vector3.zero;
                    if (_serverReeling)
                    {
                        var elapsed = (float)(_hookSampleTime - state.Started);
                        var currentVelocity = state.Velocity + Vector3.down * (hookGravity * elapsed);
                        var horizontalVelocity = Vector3.ProjectOnPlane(currentVelocity, Vector3.up);
                        var horizontalToHand = Vector3.ProjectOnPlane(hand - _hookPosition, Vector3.up);
                        if (horizontalToHand.sqrMagnitude > 0.0001f)
                        {
                            var direction = horizontalToHand.normalized;
                            // Pull only along the water/ground plane; gravity keeps the hook falling.
                            var outwardSpeed = Vector3.Dot(horizontalVelocity, direction);
                            if (outwardSpeed < 0f) horizontalVelocity -= direction * outwardSpeed;
                            var flightReelSpeed = hookReelSpeed *
                                (1f + _player.RingValue(PlayerRingStat.HookRetrievalSpeed));
                            horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, direction * flightReelSpeed,
                                hookReelAcceleration * (float)step);
                        }
                        flightVelocity = horizontalVelocity + Vector3.up *
                            (currentVelocity.y - hookGravity * (float)step);
                        to = _hookPosition + (horizontalVelocity + Vector3.up *
                            (currentVelocity.y - 0.5f * hookGravity * (float)step)) * (float)step;
                    }
                    var solid = SegmentHit(_hookPosition, to, 0.09f, null,
                        out var hit, out var fraction);
                    var water = _water.Crossing(_hookPosition, to, out var waterPoint, out var waterFraction);
                    if (!solid && !water && _serverReeling &&
                        (to - hand).sqrMagnitude <= hookPickupRadius * hookPickupRadius)
                    {
                        CaptureItemsServer(_hookPosition, hand);
                        _hookPosition = hand;
                        ResetHookServer();
                        return;
                    }
                    if (solid || water || !_serverReeling && Vector3.Distance(to, hand) >= maximumRopeLength ||
                        next - _hookFlightStarted >= 6d)
                    {
                        water = water && (!solid || waterFraction < fraction);
                        var landedPosition = water ? waterPoint : solid ? hit.point + hit.normal * 0.06f : to;
                        CaptureItemsServer(_hookPosition, landedPosition);
                        _hookPosition = landedPosition;
                        var support = solid && !water ? hit.collider.GetComponentInParent<NetworkObject>() : null;
                        if (!solid && !water && !TryHookSurface(ref _hookPosition, out support))
                        { ResetHookServer(); return; }
                        LandHookServer(_hookPosition, support);
                        if (water) ToolEffectClientRpc(_hookPosition, true);
                        return;
                    }
                    CaptureItemsServer(_hookPosition, to);
                    _hookPosition = to; _hookSampleTime = next;
                    if (_serverReeling)
                    {
                        state.Origin = to;
                        state.Velocity = flightVelocity;
                        state.Started = next;
                        stateChanged = true;
                    }
                }
                if (stateChanged) _hook.Value = state;
                return;
            }
            if (state.Phase == HookPhase.Returning)
            {
                if (Now < _hookSampleTime + 1d / 60d) return;
                var recallDelta = Mathf.Min(0.05f, (float)(Now - _hookSampleTime));
                _hookSampleTime = Now;
                var recallPrevious = _hookPosition;
                var recalledPosition = Vector3.MoveTowards(recallPrevious, hand,
                    hookRecallSpeed * (1f + _player.RingValue(PlayerRingStat.HookRetrievalSpeed)) * recallDelta);
                _hookPosition = recalledPosition;
                _hook.Value = new EquipmentHookState
                {
                    Phase = HookPhase.Returning,
                    Origin = recallPrevious,
                    Velocity = (recalledPosition - recallPrevious) / Mathf.Max(recallDelta, 0.0001f),
                    Started = Now
                };
                if ((recalledPosition - hand).sqrMagnitude < 0.01f)
                    ResetHookServer();
                return;
            }
            if (state.Phase == HookPhase.Landed) _hookPosition = EvaluateHook(Now, false);
            if (Now < _hookSampleTime + 0.05d) return;
            var dt = Mathf.Min(0.1f, (float)(Now - _hookSampleTime)); _hookSampleTime = Now;
            if (!_serverReeling)
            {
                if (state.Phase == HookPhase.Reeling)
                {
                    SettleReleasedHook(hand, dt);
                }
                return;
            }
            var previous = _hookPosition;
            var toHand = hand - previous;
            var planarToHand = Vector3.ProjectOnPlane(toHand, Vector3.up);
            var reelSpeed = hookReelSpeed * (1f + _player.RingValue(PlayerRingStat.HookRetrievalSpeed));
            Vector3 desiredVelocity;
            if (planarToHand.magnitude > hookLiftHorizontalDistance)
            {
                var planarDirection = planarToHand.normalized;
                var surfaceProbe = previous + planarDirection * reelSpeed * dt;
                var surfaceY = previous.y;
                if (TryHookSurfaceAt(surfaceProbe, Mathf.Max(previous.y, hand.y) + 2f,
                    out var surfacePoint, out _))
                    surfaceY = surfacePoint.y;
                var verticalSpeed = Mathf.Clamp((surfaceY - previous.y) / Mathf.Max(dt, 0.0001f),
                    -hookSurfaceHeightSpeed, hookSurfaceHeightSpeed);
                desiredVelocity = planarDirection * reelSpeed + Vector3.up * verticalSpeed;
            }
            else
            {
                desiredVelocity = toHand.sqrMagnitude > 0.0001f
                    ? toHand.normalized * reelSpeed : Vector3.zero;
            }
            _hookReelVelocity = Vector3.MoveTowards(_hookReelVelocity, desiredVelocity,
                hookReelAcceleration * dt);
            var nextPosition = previous + _hookReelVelocity * dt;
            if (planarToHand.magnitude <= hookLiftHorizontalDistance &&
                Vector3.Dot(hand - previous, hand - nextPosition) <= 0f)
                nextPosition = hand;
            // Stop at intervening solid geometry, rather than pull loot through rocks or walls.
            // While reeling, the hook must cross the player's own hull/railings instead of being
            // pushed back by them. Surface probes still see the deck and lift the cargo aboard.
            if (SegmentHit(previous + Vector3.up * 0.12f, nextPosition + Vector3.up * 0.12f,
                    0.05f, playerSupport, out var obstacle, out _))
            {
                nextPosition = obstacle.point + obstacle.normal * 0.07f;
                _hookReelVelocity = Vector3.zero;
            }
            CaptureItemsServer(previous, nextPosition);
            _hookPosition = nextPosition;
            _hook.Value = new EquipmentHookState
            {
                Phase = HookPhase.Reeling,
                Origin = previous,
                Velocity = (nextPosition - previous) / Mathf.Max(dt, 0.0001f),
                Started = Now
            };
            if (Vector3.Distance(nextPosition, hand) < 0.45f)
                ResetHookServer();
        }
        private void LandHookServer(Vector3 position, NetworkObject support)
        {
            if (support != null && !support.IsSpawned) support = null;
            _hookReelVelocity = Vector3.zero;
            _hook.Value = new EquipmentHookState
            {
                Phase = HookPhase.Landed,
                Origin = support != null ? WorldItem.GetPhysicsFrame(support).inverse.MultiplyPoint3x4(position) : position,
                HasSupport = support != null,
                Support = support != null ? new NetworkObjectReference(support) : default,
                Started = Now
            };
            _hookSampleTime = Now;
        }
        private bool TryHookSurface(ref Vector3 position, out NetworkObject support)
        {
            if (!TryHookSurfaceAt(position, position.y + 0.3f, out var surfacePoint, out support))
                return false;
            position = surfacePoint;
            return true;
        }

        private bool TryHookSurfaceAt(Vector3 horizontalPosition, float probeTopY, out Vector3 point,
            out NetworkObject support)
        {
            support = null;
            point = horizontalPosition;
            var origin = new Vector3(horizontalPosition.x, probeTopY, horizontalPosition.z);
            var solid = SegmentHit(origin, origin + Vector3.down * 128f, 0.04f, null,
                out var surface, out _);
            var waterHeight = float.NegativeInfinity;
            var water = _water != null && _water.TryHeight(horizontalPosition, out waterHeight) &&
                        waterHeight <= probeTopY + 0.05f;
            if (!solid && !water)
                return false;
            if (solid && (!water || surface.point.y >= waterHeight))
            {
                point = surface.point + surface.normal * 0.07f;
                support = surface.collider.GetComponentInParent<NetworkObject>();
                if (support != null && !support.IsSpawned) support = null;
            }
            else
                point = new Vector3(horizontalPosition.x, waterHeight, horizontalPosition.z);
            return true;
        }

        private void SettleReleasedHook(Vector3 hand, float dt)
        {
            var previous = _hookPosition;
            var planarVelocity = Vector3.ProjectOnPlane(_hookReelVelocity, Vector3.up);
            planarVelocity = Vector3.MoveTowards(planarVelocity, Vector3.zero, hookReelAcceleration * dt);
            var targetY = previous.y;
            NetworkObject support = null;
            var hasSurface = TryHookSurfaceAt(previous, Mathf.Max(previous.y, hand.y) + 2f,
                out var surfacePoint, out support);
            if (hasSurface) targetY = surfacePoint.y;
            var verticalVelocity = Mathf.Clamp((targetY - previous.y) / Mathf.Max(dt, 0.0001f),
                -hookSurfaceHeightSpeed, hookSurfaceHeightSpeed);
            _hookReelVelocity = planarVelocity + Vector3.up * verticalVelocity;
            var next = previous + _hookReelVelocity * dt;
            if (Mathf.Abs(next.y - targetY) <= hookSurfaceHeightSpeed * dt)
                next.y = targetY;
            CaptureItemsServer(previous, next);
            _hookPosition = next;

            if (planarVelocity.sqrMagnitude < 0.0025f && (!hasSurface || Mathf.Abs(next.y - targetY) < 0.01f))
            {
                LandHookServer(next, support);
                return;
            }
            _hook.Value = new EquipmentHookState
            {
                Phase = HookPhase.Reeling,
                Origin = previous,
                Velocity = (next - previous) / Mathf.Max(dt, 0.0001f),
                Started = Now
            };
        }
        private void CaptureItemsServer(Vector3 from, Vector3 to)
        {
            _hookItems.RemoveAll(item => item == null || !item.IsSpawned || !item.IsTetheredTo(this));
            var delta = to - from;
            foreach (var item in WorldItem.ActiveItems)
            {
                if (item == null || !item.IsSpawned || item.IsTetheredTo(this) || _claimedThisThrow.Contains(item)) continue;
                var position = item.GetServerPosition();
                var t = delta.sqrMagnitude > 0.00001f ? Mathf.Clamp01(Vector3.Dot(position - from, delta) / delta.sqrMagnitude) : 0f;
                var nearest = from + delta * t;
                if (Vector3.Distance(position, nearest) > hookPickupRadius || HasSolidBetween(nearest + Vector3.up * 0.2f, position)) continue;
                item.CaptureWithHookServer(this, HookCargoOffset(_hookItems.Count + _stressHookItems.Count));
                // A losing hook must not recapture the same item every tick while ropes overlap.
                _claimedThisThrow.Add(item);
                _hookItems.Add(item);
            }
            LootStressTest.CaptureWithHookServer(this, from, to, hookPickupRadius,
                _hookItems.Count, _stressHookItems);
        }

        public static Vector3 HookCargoOffset(int index)
        {
            if (index <= 0) return Vector3.up * 0.1f;
            var radius = 0.11f * Mathf.Sqrt(index);
            var angle = index * 137.50776f * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(angle) * radius, 0.1f, Mathf.Sin(angle) * radius);
        }

        private void BeginHookRecallServer()
        {
            var state = _hook.Value;
            _hookPosition = EvaluateHook(Now, false);
            ReleaseHookCargoServer(_hookPosition);
            _hookReelVelocity = Vector3.zero;
            _hookSampleTime = Now;
            _hook.Value = new EquipmentHookState
            {
                Phase = HookPhase.Returning,
                Origin = _hookPosition,
                Velocity = Vector3.zero,
                Started = Now
            };
        }

        private void ReleaseHookCargoServer(Vector3 position)
        {
            for (var i = 0; i < _hookItems.Count; i++)
            {
                var item = _hookItems[i];
                if (item == null || !item.IsSpawned || !item.IsTetheredTo(this)) continue;
                item.ReleaseFromHookServer(this, position + HookCargoOffset(i), null);
            }
            for (var i = 0; i < _stressHookItems.Count; i++)
            {
                var cargoIndex = _hookItems.Count + i;
                LootStressTest.ReleaseFromHookServer(_stressHookItems[i], this,
                    position + HookCargoOffset(cargoIndex), Quaternion.identity);
            }
            _stressHookItems.Clear();
            _hookItems.Clear();
            _claimedThisThrow.Clear();
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
                    release += frame.MultiplyVector(HookCargoOffset(i) + Vector3.forward * 0.35f);
                }
                item.ReleaseFromHookServer(this, release, releaseSupport, water);
            }
            for (var i = 0; i < _stressHookItems.Count; i++)
            {
                var cargoIndex = _hookItems.Count + i;
                var release = retrieved
                    ? feet + WorldItem.GetPhysicsFrame(support).MultiplyVector(
                        HookCargoOffset(cargoIndex) + Vector3.forward * 0.35f)
                    : _hookPosition + HookCargoOffset(cargoIndex);
                LootStressTest.ReleaseFromHookServer(_stressHookItems[i], this, release, Quaternion.identity);
            }
            _stressHookItems.Clear();
            _hookItems.Clear(); _claimedThisThrow.Clear(); _hook.Value = default; _serverReeling = false;
            _hookReelVelocity = Vector3.zero; _chargeStarted = -1d;
            _charging.Value = false;
        }

        [ClientRpc]
        private void ShotClientRpc(int id, Vector3 origin, Vector3 velocity, double started, bool extra)
        {
            var visual = EquipmentProjectileVisual.Create(this, origin, velocity, bulletGravity, started,
                bulletLifetime, musketProjectilePrefab);
            if (visual != null) _bulletVisuals[id] = visual;
            if (!IsOwner || extra)
                PlayMusketMuzzle(origin, velocity.normalized);
        }
        [ClientRpc]
        private void BulletImpactClientRpc(int id, Vector3 point, Vector3 normal, bool water, bool show, double at)
        {
            if (!_bulletVisuals.TryGetValue(id, out var visual)) return;
            if (visual != null) visual.SetImpact(point, normal, water, show, at, waterSplashPrefab, weaponImpactEffectPrefab);
            _bulletVisuals.Remove(id);
        }
        [ClientRpc]
        private void BulletWaterEntryClientRpc(int id, Vector3 point, double at)
        {
            if (_bulletVisuals.TryGetValue(id, out var visual) && visual != null)
                visual.SetWaterEntry(point, at, waterSplashPrefab);
        }
        [ClientRpc]
        private void ToolEffectClientRpc(Vector3 position, bool water)
        { CannonEffects.Hit(position, Vector3.up, water, waterSplashPrefab, shovelDigEffectPrefab); }
        [ClientRpc]
        private void PourClientRpc(Vector3 origin, Vector3 direction)
        { EquipmentProjectileVisual.CreateWaterPour(origin, direction, bucketPourEffectPrefab); }
        [ClientRpc]
        private void BlockClientRpc()
        { if (_view != null) _view.BlockImpact(); }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z);
    }
}
