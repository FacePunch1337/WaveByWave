using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using WaveByWave.Items;
using WaveByWave.Player;
using WaveByWave.Combat;

namespace WaveByWave.Ships
{
    public enum VoyagePhase : byte { Day, Sunset, Night, Sunrise, Victory, Defeat }

    [DefaultExecutionOrder(1250)]
    [RequireComponent(typeof(NetworkShipController))]
    public sealed class ShipCannonBattery : NetworkBehaviour
    {
        [SerializeField] private ShipCannon[] cannons;
        [SerializeField] private GameObject waterSplashPrefab;
        [SerializeField] private GameObject cannonProjectilePrefab;
        [SerializeField] private GameObject cannonMuzzleEffectPrefab;
        [SerializeField] private GameObject cannonImpactEffectPrefab;
        [SerializeField] private ShipTreasureChest treasureChest;
        [SerializeField, Min(1f)] private float maximumHealth = 400f;
        private NetworkList<CannonState> _states;
        private readonly NetworkVariable<float> _health = new(400f);
        private readonly NetworkVariable<float> _damageBonus = new();
        private readonly NetworkVariable<float> _armorBonus = new();
        private readonly NetworkVariable<int> _treasureExperience = new();
        private readonly NetworkVariable<int> _crewLevel = new(1);
        private readonly NetworkVariable<bool> _upgradePaused = new();
        private readonly NetworkVariable<byte> _voyagePhase = new();
        private readonly NetworkVariable<float> _voyageDayProgress = new();
        private readonly NetworkVariable<float> _voyageHour = new(10f);
        private readonly NetworkVariable<int> _voyageWave = new();
        private readonly NetworkVariable<double> _returnToPortAt = new();
        private bool _returnRequested;
        private ShipFlooding _flooding;
        private readonly HashSet<ulong> _pendingRingSelections = new();
        private PlayerRingCatalog _ringCatalog;
        private NetworkShipController _ship;
        private MovingPlatform _platform;
        private double[] _lastAimReceived;
        private int _nextBallId;
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public float Health => _flooding != null ? maximumHealth * (1f - _flooding.Fill) : _health.Value;
        public float MaximumHealth => maximumHealth;
        public int TreasureExperience => _treasureExperience.Value;
        public int CrewLevel => _crewLevel.Value;
        public bool UpgradePaused => _upgradePaused.Value;
        public VoyagePhase Phase => (VoyagePhase)_voyagePhase.Value;
        public bool VoyageEnded => Phase == VoyagePhase.Victory || Phase == VoyagePhase.Defeat;
        public float ReturnToPortIn => IsSpawned ? Mathf.Max(0f, (float)(_returnToPortAt.Value - NetworkManager.ServerTime.Time)) : 0f;
        public float DayProgress => _voyageDayProgress.Value;
        public float TimeOfDay => _voyageHour.Value;
        public int WaveNumber => _voyageWave.Value;
        public float LevelProgress
        {
            get
            {
                var catalog = _ringCatalog != null ? _ringCatalog :
                    (_ringCatalog = Resources.Load<PlayerRingCatalog>("PlayerRingCatalog"));
                if (catalog == null) return 0f;
                var previous = catalog.ExperienceForLevel(CrewLevel);
                var next = catalog.ExperienceForLevel(CrewLevel + 1);
                return Mathf.Clamp01((TreasureExperience - previous) /
                    (float)Mathf.Max(1, next - previous));
            }
        }
        public ShipCannon[] Cannons => cannons;

        private void Awake()
        {
            _states = new NetworkList<CannonState>();
            _ship = GetComponent<NetworkShipController>();
            _platform = GetComponent<MovingPlatform>();
            _flooding = GetComponent<ShipFlooding>();
            if (cannons == null || cannons.Length == 0)
                cannons = GetComponentsInChildren<ShipCannon>(true);
        }

        public override void OnNetworkSpawn()
        {
            _ringCatalog = Resources.Load<PlayerRingCatalog>("PlayerRingCatalog");
            _upgradePaused.OnValueChanged += OnUpgradePauseChanged;
            OnUpgradePauseChanged(false, _upgradePaused.Value);
            if (!IsServer) return;
            _crewLevel.Value = 1;
            _treasureExperience.Value = 0;
            _states.Clear();
            _lastAimReceived = new double[cannons.Length];
            foreach (var cannon in cannons)
                _states.Add(new CannonState { Operator = NetworkShipController.NoHelmsman });
            _health.Value = maximumHealth;
            NetworkManager.OnClientDisconnectCallback += OnDisconnected;
        }

        public override void OnNetworkDespawn()
        {
            _upgradePaused.OnValueChanged -= OnUpgradePauseChanged;
            Time.timeScale = 1f;
            _pendingRingSelections.Clear();
            if (NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= OnDisconnected;
            CannonEffects.ClearShots(this);
        }

        public override void OnDestroy()
        {
            if (_upgradePaused.Value) Time.timeScale = 1f;
            CannonEffects.ClearShots(this);
            base.OnDestroy();
        }

        public int GetCannonIndex(ShipCannon cannon) => System.Array.IndexOf(cannons, cannon);

        private static void OnUpgradePauseChanged(bool previous, bool paused) =>
            Time.timeScale = paused ? 0f : 1f;

        private void CheckTreasureLevel()
        {
            if (!IsServer || VoyageEnded || _upgradePaused.Value || _ringCatalog == null ||
                TreasureExperience < _ringCatalog.ExperienceForLevel(_crewLevel.Value + 1)) return;
            _crewLevel.Value++;
            _pendingRingSelections.Clear();
            _upgradePaused.Value = true;
            foreach (var client in NetworkManager.ConnectedClientsList)
            {
                if (client.PlayerObject == null ||
                    !client.PlayerObject.TryGetComponent<NetworkPlayerController>(out var player)) continue;
                _pendingRingSelections.Add(client.ClientId);
                player.BeginRingChoiceServer(_crewLevel.Value,
                    unchecked((uint)_crewLevel.Value * 7919u + (uint)client.ClientId * 104729u));
            }
            if (_pendingRingSelections.Count == 0) _upgradePaused.Value = false;
        }

        internal void MarkRingChoiceComplete(ulong clientId)
        {
            if (!IsServer || !_pendingRingSelections.Remove(clientId) ||
                _pendingRingSelections.Count != 0) return;
            _upgradePaused.Value = false;
            CheckTreasureLevel();
        }

        internal void SetVoyageClockServer(VoyagePhase phase, float dayProgress, float hour)
        {
            if (!IsServer || VoyageEnded) return;
            _voyagePhase.Value = (byte)phase;
            _voyageDayProgress.Value = Mathf.Clamp01(dayProgress);
            _voyageHour.Value = Mathf.Repeat(hour, 24f);
        }
        internal void SetVoyageWaveServer(int waveNumber)
        {
            if (IsServer) _voyageWave.Value = waveNumber;
        }
        internal void SetVoyageVictoryServer(float displayDuration = 5f) => FinishVoyageServer(true, displayDuration);

        public void FinishVoyageServer(bool won, float displayDuration)
        {
            if (!IsServer || !IsSpawned || VoyageEnded) return;
            _voyagePhase.Value = (byte)(won ? VoyagePhase.Victory : VoyagePhase.Defeat);
            _returnToPortAt.Value = NetworkManager.ServerTime.Time + Mathf.Max(1f, displayDuration);
            _upgradePaused.Value = false;
            _pendingRingSelections.Clear();
            FindFirstObjectByType<WaveByWave.Generation.VoyageDayNightController>()?.PauseClockServer();
        }

        private void Update()
        {
            if (!IsSpawned || !IsServer || !VoyageEnded || _returnRequested || ReturnToPortIn > 0f) return;
            var session = WaveByWave.Networking.NetworkSessionCoordinator.Instance;
            if (session == null) return;
            _returnRequested = session.TryReturnToPort();
            if (!_returnRequested) _returnToPortAt.Value = NetworkManager.ServerTime.Time + 2d;
        }
        public CannonState GetState(int index) => index >= 0 && index < _states.Count ? _states[index] :
            new CannonState { Operator = NetworkShipController.NoHelmsman };
        public int GetOperatorCannon(ulong clientId)
        {
            for (var i = 0; i < _states.Count; i++) if (_states[i].Operator == clientId) return i;
            return -1;
        }

        private bool TryPlayer(ulong clientId, out NetworkPlayerController player)
        {
            player = null;
            return NetworkManager.ConnectedClients.TryGetValue(clientId, out var client) &&
                client.PlayerObject != null && client.PlayerObject.TryGetComponent(out player);
        }
        private bool Near(ulong clientId, Transform point, float range = 4f)
        {
            if (VoyageEnded) return false;
            if (!TryPlayer(clientId, out var player)) return false;
            var position = player.TryGetPositionOnPlatform(NetworkObject, out var onShip)
                ? onShip : player.transform.position;
            return (position - point.position).sqrMagnitude <= range * range;
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestCannonServerRpc(int index, ServerRpcParams rpc = default)
        {
            var sender = rpc.Receive.SenderClientId;
            var accepted = index >= 0 && index < _states.Count && cannons[index] != null &&
                GetOperatorCannon(sender) < 0 && !_ship.IsClientOperatingNavigationStation(sender) &&
                _states[index].Operator == NetworkShipController.NoHelmsman && Near(sender, cannons[index].Station);
            if (accepted)
            {
                var state = _states[index];
                state.Operator = sender;
                _states[index] = state;
                _lastAimReceived[index] = NetworkManager.ServerTime.Time;
            }
            CannonAssignmentClientRpc(sender, accepted ? index : -1);
        }

        [ClientRpc]
        private void CannonAssignmentClientRpc(ulong clientId, int index)
        {
            if (NetworkManager.LocalClientId == clientId)
                NetworkManager.LocalClient.PlayerObject?.GetComponent<NetworkPlayerController>()
                    ?.HandleCannonAssignment(this, index >= 0 ? cannons[index] : null);
        }

        [ServerRpc(RequireOwnership = false)]
        public void ReleaseCannonServerRpc(ServerRpcParams rpc = default) => ReleaseOperator(rpc.Receive.SenderClientId);
        private void ReleaseOperator(ulong sender)
        {
            var index = GetOperatorCannon(sender);
            if (index < 0) return;
            var state = _states[index];
            CancelReload(ref state);
            state.Operator = NetworkShipController.NoHelmsman;
            _states[index] = state;
        }
        private void OnDisconnected(ulong sender)
        {
            ReleaseOperator(sender);
            MarkRingChoiceComplete(sender);
        }

        private static bool HasSelectedAmmo(PlayerInventory inventory) => inventory != null &&
            inventory.TryGetDefinition(inventory.ServerSelectedIndex, out var ammo) &&
            ammo.Category == ItemCategory.Supply && ammo.SupplyKind == SupplyKind.Cannonball;

        private static void CancelReload(ref CannonState state)
        {
            if (state.ReloadEnd <= 0d) return;
            state.ReloadEnd = 0d;
            state.AmmoId = default;
            state.Damage = 0f;
        }

        public void NotifyInventorySelectionServer(ulong clientId)
        {
            if (!IsServer || !IsSpawned) return;
            var index = GetOperatorCannon(clientId);
            if (index < 0 || _states[index].ReloadEnd <= 0d) return;
            if (TryPlayer(clientId, out var player) && HasSelectedAmmo(player.Inventory)) return;
            var state = _states[index];
            CancelReload(ref state);
            _states[index] = state;
        }

        private bool CompleteReload(ref CannonState state)
        {
            if (!TryPlayer(state.Operator, out var player) || !HasSelectedAmmo(player.Inventory) ||
                !player.Inventory.TryConsumeServer(player.Inventory.ServerSelectedIndex, 1, out var ammo))
            {
                CancelReload(ref state);
                return false;
            }
            state.AmmoId = new Unity.Collections.FixedString64Bytes(ammo.Id);
            state.Damage = ammo.Potency;
            state.ReloadEnd = 0d;
            state.Loaded = true;
            return true;
        }

        [ServerRpc(RequireOwnership = false, Delivery = RpcDelivery.Unreliable)]
        public void SubmitAimServerRpc(float yaw, float elevation, ServerRpcParams rpc = default)
        {
            var index = GetOperatorCannon(rpc.Receive.SenderClientId);
            ApplyAim(index, yaw, elevation);
        }

        private void ApplyAim(int index, float yaw, float elevation)
        {
            if (index < 0 || !IsFinite(yaw) || !IsFinite(elevation)) return;
            var now = NetworkManager.ServerTime.Time;
            var maxStep = cannons[index].AimSpeed * Mathf.Clamp((float)(now - _lastAimReceived[index]), 0.02f, 0.2f);
            var target = cannons[index].ClampAim(new Vector2(yaw, elevation));
            var state = _states[index];
            state.Yaw = Mathf.MoveTowards(state.Yaw, target.x, maxStep);
            state.Elevation = Mathf.MoveTowards(state.Elevation, target.y, maxStep);
            _lastAimReceived[index] = now;
            if (!state.Equals(_states[index])) _states[index] = state;
        }

        [ServerRpc(RequireOwnership = false)]
        public void FireOrReloadServerRpc(int selectedSlot, uint selectionRevision, float yaw, float elevation, ServerRpcParams rpc = default)
        {
            var sender = rpc.Receive.SenderClientId;
            var index = GetOperatorCannon(sender);
            if (index < 0 || !Near(sender, cannons[index].Station)) return;
            // Reliable messages on the player and ship may arrive in different
            // orders. A revision makes the slot change part of this action too.
            if (!TryPlayer(sender, out var operatorPlayer) ||
                !operatorPlayer.Inventory.ApplySelectionServer(selectedSlot, selectionRevision)) return;
            NotifyInventorySelectionServer(sender);
            if (!IsFinite(yaw) || !IsFinite(elevation)) return;
            ApplyAim(index, yaw, elevation);
            var state = _states[index];
            if (state.ReloadEnd > 0d)
            {
                if (NetworkManager.ServerTime.Time < state.ReloadEnd) return;
                if (!CompleteReload(ref state)) { _states[index] = state; return; }
            }
            if (state.Loaded)
            {
                var cannon = cannons[index];
                cannon.GetMuzzlePose(new Vector2(state.Yaw, state.Elevation), out var origin, out var forward);
                var velocity = forward * cannon.MuzzleSpeed + _platform.GetPointVelocity(origin);
                var id = ++_nextBallId;
                var started = NetworkManager.ServerTime.Time;
                if (!DotsCannonProjectileSystem.Spawn(new DotsCannonProjectile
                    {
                        Position = origin, Previous = origin, Origin = origin, Velocity = velocity,
                        Gravity = cannon.Gravity, Started = (float)started,
                        Radius = cannon.BallRadius, Lifetime = cannon.ProjectileLifetime,
                        Damage = state.Damage * (1f + _damageBonus.Value),
                        ShooterClientId = sender, PlayerShipNetworkId = NetworkObjectId,
                        ShotId = id
                    })) { _states[index] = state; return; }
                ShotClientRpc(index, id, origin, velocity, cannon.Gravity, started, cannon.BallRadius, cannon.ProjectileLifetime);
                state.Loaded = false;
                state.AmmoId = default;
            }
            else
            {
                if (!TryPlayer(sender, out var player) || !player.Inventory.TryGetDefinition(selectedSlot, out var ammo) ||
                    ammo.Category != ItemCategory.Supply || ammo.SupplyKind != SupplyKind.Cannonball ||
                    selectedSlot != player.Inventory.ServerSelectedIndex) return;
                state.AmmoId = new Unity.Collections.FixedString64Bytes(ammo.Id);
                state.Damage = ammo.Potency;
                state.ReloadEnd = NetworkManager.ServerTime.Time + cannons[index].ReloadDuration;
            }
            _states[index] = state;
        }

        private void FixedUpdate()
        {
            if (!IsServer || !IsSpawned) return;
            var now = NetworkManager.ServerTime.Time;
            for (var i = 0; i < _states.Count; i++)
            {
                var state = _states[i];
                var changed = false;
                if (state.Operator != NetworkShipController.NoHelmsman && !Near(state.Operator, cannons[i].Station, 5f))
                { CancelReload(ref state); state.Operator = NetworkShipController.NoHelmsman; changed = true; }
                if (state.ReloadEnd > 0d && (!TryPlayer(state.Operator, out var player) || !HasSelectedAmmo(player.Inventory)))
                { CancelReload(ref state); changed = true; }
                if (state.ReloadEnd > 0d && now >= state.ReloadEnd)
                { CompleteReload(ref state); changed = true; }
                if (changed) _states[i] = state;
            }
        }

        [ClientRpc]
        private void ShotClientRpc(int index, int id, Vector3 origin, Vector3 velocity, Vector3 gravity, double started, float radius, float lifetime)
            => CannonEffects.Shot(this, cannons[index], id, origin, velocity, gravity, started, lifetime,
                cannonProjectilePrefab, cannonMuzzleEffectPrefab);
        [ClientRpc]
        private void ImpactClientRpc(int id, Vector3 point, Vector3 normal, bool water, bool show, double at)
            => CannonEffects.Impact(this, id, point, normal, water, show, at, waterSplashPrefab, cannonImpactEffectPrefab);

        internal void ReportProjectileImpact(int id, Vector3 point, Vector3 normal,
            bool water, bool show, double at) => ImpactClientRpc(id, point, normal, water, show, at);

        public void ApplyDamageServer(float amount)
            => ApplyDamageServer(amount, transform.position);

        public void ApplyDamageServer(float amount, Vector3 hitPoint)
        {
            if (!IsServer || VoyageEnded || !IsFinite(amount) || amount <= 0f) return;
            amount /= 1f + _armorBonus.Value;
            if (_flooding != null) _flooding.HitServer(amount, hitPoint);
            else _health.Value = Mathf.Max(0f, _health.Value - amount);
        }
        public void ApplyUpgradeServer(ItemDefinition upgrade)
        {
            if (!IsServer || upgrade.Category != ItemCategory.ShipUpgrade) return;
            switch (upgrade.UpgradeStat)
            {
                case ShipUpgradeStat.CannonDamage: _damageBonus.Value = Mathf.Min(2f, _damageBonus.Value + upgrade.UpgradeBonus); break;
                case ShipUpgradeStat.Armor: _armorBonus.Value = Mathf.Min(2f, _armorBonus.Value + upgrade.UpgradeBonus); break;
                default: _ship.ApplySailingUpgradeServer(upgrade.UpgradeStat, upgrade.UpgradeBonus); break;
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void DepositTreasureServerRpc(int selectedSlot, ServerRpcParams rpc = default)
        {
            var sender = rpc.Receive.SenderClientId;
            if (treasureChest == null || !Near(sender, treasureChest.transform, 3f) || !TryPlayer(sender, out var player) ||
                !player.Inventory.TryGetDefinition(selectedSlot, out var treasure) || treasure.Category != ItemCategory.Treasure ||
                !player.Inventory.TryConsumeServer(selectedSlot, 1, out _)) return;
            _treasureExperience.Value = (int)System.Math.Min(int.MaxValue,
                (long)_treasureExperience.Value + treasure.TreasureExperience);
            CheckTreasureLevel();
        }
    }
}
