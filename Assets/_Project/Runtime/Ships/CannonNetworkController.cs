using System.Collections.Generic;
using System.Threading;
using Unity.Netcode;
using UnityEngine;
using WaveByWave.Combat;
using WaveByWave.Items;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    /// <summary>
    /// Generic multiplayer transport for one or more cannons. It deliberately has
    /// no ship health, voyage or upgrade state, so the same controller can be used
    /// for a fort, an island or any other NetworkObject.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CannonNetworkController : NetworkBehaviour
    {
        [SerializeField] private Cannon[] cannons;
        private static readonly HashSet<CannonNetworkController> ActiveControllers = new();
        private static int _shotSequence;
        private NetworkList<CannonState> _states;
        private ShipCannonBattery _battery;
        private NetworkShipController _ship;
        private MovingPlatform _platform;
        private double[] _lastAimReceived;

        public Cannon[] Cannons => cannons;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            ActiveControllers.Clear();
            _shotSequence = 0;
        }

        private void Awake()
        {
            _states = new NetworkList<CannonState>();
            _battery = GetComponent<ShipCannonBattery>();
            _ship = GetComponent<NetworkShipController>();
            _platform = GetComponent<MovingPlatform>();
            if (cannons == null || cannons.Length == 0)
                cannons = GetComponentsInChildren<Cannon>(true);
        }

        public override void OnNetworkSpawn()
        {
            ActiveControllers.Add(this);
            if (!IsServer) return;
            _states.Clear();
            _lastAimReceived = new double[cannons.Length];
            foreach (var cannon in cannons)
                _states.Add(new CannonState { Operator = NetworkShipController.NoHelmsman });
            NetworkManager.OnClientDisconnectCallback += OnDisconnected;
        }

        public override void OnNetworkDespawn()
        {
            ActiveControllers.Remove(this);
            if (NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= OnDisconnected;
            if (IsClient) CannonEffects.ClearShots(this);
        }

        public override void OnDestroy()
        {
            ActiveControllers.Remove(this);
            base.OnDestroy();
        }

        public int GetCannonIndex(Cannon cannon) => System.Array.IndexOf(cannons, cannon);
        public CannonState GetState(int index) => index >= 0 && index < _states.Count ? _states[index] :
            new CannonState { Operator = NetworkShipController.NoHelmsman };

        public int GetOperatorCannon(ulong clientId)
        {
            for (var i = 0; i < _states.Count; i++)
                if (_states[i].Operator == clientId) return i;
            return -1;
        }

        private bool TryPlayer(ulong clientId, out NetworkPlayerController player)
        {
            player = null;
            return NetworkManager.ConnectedClients.TryGetValue(clientId, out var client) &&
                client.PlayerObject != null && client.PlayerObject.TryGetComponent(out player);
        }

        private bool VoyageUnavailable => _battery != null && (_battery.UpgradePaused || _battery.VoyageEnded);

        private bool Near(ulong clientId, Transform point, float range = 4f)
        {
            if (VoyageUnavailable || !TryPlayer(clientId, out var player)) return false;
            var position = player.TryGetPositionOnPlatform(NetworkObject, out var onPlatform)
                ? onPlatform : player.transform.position;
            return (position - point.position).sqrMagnitude <= range * range;
        }

        private static bool IsOperatingAnotherCannon(ulong clientId, CannonNetworkController except)
        {
            foreach (var controller in ActiveControllers)
                if (controller != null && controller != except && controller.IsServer && controller.IsSpawned &&
                    controller.GetOperatorCannon(clientId) >= 0) return true;
            return false;
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestCannonServerRpc(int index, ServerRpcParams rpc = default)
        {
            var sender = rpc.Receive.SenderClientId;
            var accepted = index >= 0 && index < _states.Count && cannons[index] != null &&
                GetOperatorCannon(sender) < 0 && !IsOperatingAnotherCannon(sender, this) &&
                (_ship == null || !_ship.IsClientOperatingNavigationStation(sender)) &&
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
            if (NetworkManager.LocalClientId != clientId) return;
            NetworkManager.LocalClient.PlayerObject?.GetComponent<NetworkPlayerController>()
                ?.HandleCannonAssignment(this, index >= 0 ? cannons[index] : null);
        }

        [ServerRpc(RequireOwnership = false)]
        public void ReleaseCannonServerRpc(ServerRpcParams rpc = default) =>
            ReleaseOperator(rpc.Receive.SenderClientId);

        private void ReleaseOperator(ulong clientId)
        {
            var index = GetOperatorCannon(clientId);
            if (index < 0) return;
            var state = _states[index];
            CancelReload(ref state);
            state.Operator = NetworkShipController.NoHelmsman;
            _states[index] = state;
        }

        private void OnDisconnected(ulong clientId) => ReleaseOperator(clientId);

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

        public static void NotifyInventorySelectionFor(ulong clientId)
        {
            foreach (var controller in ActiveControllers)
                if (controller != null && controller.IsServer && controller.IsSpawned)
                    controller.NotifyInventorySelectionServer(clientId);
        }

        private void NotifyInventorySelectionServer(ulong clientId)
        {
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
            state.Damage = ammo.CannonDamageModifier;
            state.ReloadEnd = 0d;
            state.Loaded = true;
            return true;
        }

        [ServerRpc(RequireOwnership = false, Delivery = RpcDelivery.Unreliable)]
        public void SubmitAimServerRpc(float yaw, float elevation, ServerRpcParams rpc = default)
        {
            ApplyAim(GetOperatorCannon(rpc.Receive.SenderClientId), yaw, elevation);
        }

        private void ApplyAim(int index, float yaw, float elevation)
        {
            if (index < 0 || !float.IsFinite(yaw) || !float.IsFinite(elevation)) return;
            var now = NetworkManager.ServerTime.Time;
            var maxStep = cannons[index].AimSpeed *
                Mathf.Clamp((float)(now - _lastAimReceived[index]), 0.02f, 0.2f);
            var target = cannons[index].ClampAim(new Vector2(yaw, elevation));
            var state = _states[index];
            state.Yaw = Mathf.MoveTowards(state.Yaw, target.x, maxStep);
            state.Elevation = Mathf.MoveTowards(state.Elevation, target.y, maxStep);
            _lastAimReceived[index] = now;
            if (!state.Equals(_states[index])) _states[index] = state;
        }

        [ServerRpc(RequireOwnership = false)]
        public void FireOrReloadServerRpc(int selectedSlot, uint selectionRevision, float yaw, float elevation,
            ServerRpcParams rpc = default)
        {
            if (VoyageUnavailable) return;
            var sender = rpc.Receive.SenderClientId;
            var index = GetOperatorCannon(sender);
            if (index < 0 || !Near(sender, cannons[index].Station)) return;
            if (!TryPlayer(sender, out var player) ||
                !player.Inventory.ApplySelectionServer(selectedSlot, selectionRevision)) return;
            NotifyInventorySelectionServer(sender);
            if (!float.IsFinite(yaw) || !float.IsFinite(elevation)) return;
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
                var velocity = forward * cannon.MuzzleSpeed +
                    (_platform != null ? _platform.GetPointVelocity(origin) : Vector3.zero);
                var id = Interlocked.Increment(ref _shotSequence);
                var started = NetworkManager.ServerTime.Time;
                if (!DotsCannonProjectileSystem.Spawn(new DotsCannonProjectile
                    {
                        Position = origin, Previous = origin, Origin = origin, Velocity = velocity,
                        Gravity = cannon.Gravity, Started = (float)started,
                        Radius = cannon.BallRadius, Lifetime = cannon.ProjectileLifetime,
                        Damage = cannon.DamageWithModifiers(
                            _battery != null ? _battery.CannonDamageModifier : 0f,
                            state.Damage),
                        ShooterClientId = sender, SourceNetworkObjectId = NetworkObjectId,
                        ShotId = id
                    })) { _states[index] = state; return; }
                ShotClientRpc(index, id, origin, velocity, cannon.Gravity, started,
                    cannon.BallRadius, cannon.ProjectileLifetime);
                state.Loaded = false;
                state.AmmoId = default;
            }
            else
            {
                if (!player.Inventory.TryGetDefinition(selectedSlot, out var ammo) ||
                    ammo.Category != ItemCategory.Supply || ammo.SupplyKind != SupplyKind.Cannonball ||
                    selectedSlot != player.Inventory.ServerSelectedIndex) return;
                state.AmmoId = new Unity.Collections.FixedString64Bytes(ammo.Id);
                state.Damage = ammo.CannonDamageModifier;
                state.ReloadDuration = cannons[index].ReloadDuration / player.ReloadSpeedMultiplier;
                state.ReloadEnd = NetworkManager.ServerTime.Time + state.ReloadDuration;
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
                if (state.Operator != NetworkShipController.NoHelmsman &&
                    !Near(state.Operator, cannons[i].Station, 5f))
                { CancelReload(ref state); state.Operator = NetworkShipController.NoHelmsman; changed = true; }
                if (state.ReloadEnd > 0d &&
                    (!TryPlayer(state.Operator, out var player) || !HasSelectedAmmo(player.Inventory)))
                { CancelReload(ref state); changed = true; }
                if (state.ReloadEnd > 0d && now >= state.ReloadEnd)
                { CompleteReload(ref state); changed = true; }
                if (changed) _states[i] = state;
            }
        }

        [ClientRpc]
        private void ShotClientRpc(int index, int id, Vector3 origin, Vector3 velocity,
            Vector3 gravity, double started, float radius, float lifetime)
        {
            _shotCannons[id] = index;
            CannonEffects.Shot(this, cannons[index], id, origin, velocity, gravity, started,
                lifetime, cannons[index].ProjectilePrefab, cannons[index].MuzzleEffectPrefab);
        }

        [ClientRpc]
        private void ImpactClientRpc(int id, Vector3 point, Vector3 normal, bool water, bool show,
            double at) => CannonEffects.Impact(this, id, point, normal, water, show, at,
                FindEffectSource(id)?.WaterSplashPrefab, FindEffectSource(id)?.ImpactEffectPrefab);

        [ClientRpc]
        private void WaterEntryClientRpc(int id, Vector3 point, double at) =>
            CannonEffects.EnterWater(this, id, point, at, FindEffectSource(id)?.WaterSplashPrefab);

        // Effects are identical by default, but retain the firing cannon per shot
        // so independently configured mounts also present their own effects.
        private readonly Dictionary<int, int> _shotCannons = new();
        private Cannon FindEffectSource(int shotId) => _shotCannons.TryGetValue(shotId, out var index) &&
            index >= 0 && index < cannons.Length ? cannons[index] : cannons.Length > 0 ? cannons[0] : null;

        internal void ReportProjectileImpact(int id, Vector3 point, Vector3 normal,
            bool water, bool show, double at)
        {
            ImpactClientRpc(id, point, normal, water, show, at);
            _shotCannons.Remove(id);
        }

        internal void ReportProjectileWaterEntry(int id, Vector3 point, double at) =>
            WaterEntryClientRpc(id, point, at);
    }
}
