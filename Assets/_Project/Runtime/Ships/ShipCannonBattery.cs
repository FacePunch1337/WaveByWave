using System.Collections.Generic;
using StylizedWater3;
using Unity.Netcode;
using UnityEngine;
using WaveByWave.Items;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    [DefaultExecutionOrder(1250)]
    [RequireComponent(typeof(NetworkShipController))]
    public sealed class ShipCannonBattery : NetworkBehaviour
    {
        [SerializeField] private ShipCannon[] cannons;
        [SerializeField] private GameObject waterSplashPrefab;
        [SerializeField] private Material effectMaterial;
        [SerializeField] private Material ballMaterial;
        [SerializeField] private ShipTreasureChest treasureChest;
        [SerializeField] private LayerMask hitLayers = ~0;
        [SerializeField, Min(1f)] private float maximumHealth = 400f;
        private NetworkList<CannonState> _states;
        private readonly NetworkVariable<float> _health = new(400f);
        private readonly NetworkVariable<float> _damageBonus = new();
        private readonly NetworkVariable<float> _armorBonus = new();
        private readonly NetworkVariable<int> _treasureExperience = new();
        private readonly List<Ball> _balls = new(32);
        private readonly RaycastHit[] _hits = new RaycastHit[64];
        private readonly Collider[] _overlaps = new Collider[32];
        private HeightQuerySystem.Sampler _waterSampler;
        private AlignToWater _alignment;
        private NetworkShipController _ship;
        private MovingPlatform _platform;
        private double[] _lastAimReceived;
        private int _nextBallId;
        private const double CollisionSampleStep = 1d / 60d;
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public float Health => _health.Value;
        public float MaximumHealth => maximumHealth;
        public int TreasureExperience => _treasureExperience.Value;
        public int CrewLevel => 1 + Mathf.FloorToInt(Mathf.Sqrt(TreasureExperience / 100f));
        public ShipCannon[] Cannons => cannons;

        private struct Ball
        {
            public int Id;
            public ulong Shooter;
            public Vector3 Origin, Velocity, Gravity, Previous;
            public double Started;
            public float Radius, Lifetime, Damage;
            public int SampleIndex;
        }

        private void Awake()
        {
            _states = new NetworkList<CannonState>();
            _ship = GetComponent<NetworkShipController>();
            _platform = GetComponent<MovingPlatform>();
            _alignment = GetComponent<AlignToWater>();
            if (cannons == null || cannons.Length == 0)
                cannons = GetComponentsInChildren<ShipCannon>(true);
        }

        public override void OnNetworkSpawn()
        {
            if (!IsServer) return;
            _states.Clear();
            _lastAimReceived = new double[cannons.Length];
            foreach (var cannon in cannons)
                _states.Add(new CannonState { Operator = NetworkShipController.NoHelmsman });
            _health.Value = maximumHealth;
            _waterSampler = new HeightQuerySystem.Sampler();
            _waterSampler.SetSampleCount(2, true);
            NetworkManager.OnClientDisconnectCallback += OnDisconnected;
        }

        public override void OnNetworkDespawn()
        {
            if (NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= OnDisconnected;
            _waterSampler?.Dispose();
            _waterSampler = null;
            _balls.Clear();
            CannonEffects.ClearShots(this);
        }

        public override void OnDestroy()
        {
            _waterSampler?.Dispose();
            _waterSampler = null;
            CannonEffects.ClearShots(this);
            base.OnDestroy();
        }

        public int GetCannonIndex(ShipCannon cannon) => System.Array.IndexOf(cannons, cannon);
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
        private void OnDisconnected(ulong sender) => ReleaseOperator(sender);

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
                if (_balls.Count >= 32) { _states[index] = state; return; }
                var cannon = cannons[index];
                cannon.GetMuzzlePose(new Vector2(state.Yaw, state.Elevation), out var origin, out var forward);
                var velocity = forward * cannon.MuzzleSpeed + _platform.GetPointVelocity(origin);
                var id = ++_nextBallId;
                var started = NetworkManager.ServerTime.Time;
                _balls.Add(new Ball { Id = id, Shooter = sender, Origin = origin, Previous = origin,
                    Velocity = velocity, Gravity = cannon.Gravity, Started = started, Radius = cannon.BallRadius,
                    Lifetime = cannon.ProjectileLifetime, Damage = state.Damage * (1f + _damageBonus.Value) });
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
            for (var i = _balls.Count - 1; i >= 0; i--)
            {
                var ball = _balls[i];
                var age = (float)(now - ball.Started);
                var lastSample = (int)System.Math.Ceiling(ball.Lifetime / CollisionSampleStep);
                var target = System.Math.Min(lastSample, (int)System.Math.Floor(age / CollisionSampleStep));
                var removed = false;
                // Sample fixed points of the analytic trajectory, independent of
                // frame rate. A bounded catch-up avoids a long frame causing a spike.
                for (var step = 0; step < 8 && ball.SampleIndex < target; step++)
                {
                    ball.SampleIndex++;
                    if (!SampleBall(ref ball)) continue;
                    _balls.RemoveAt(i);
                    removed = true;
                    break;
                }
                if (removed) continue;
                if (ball.SampleIndex >= lastSample)
                {
                    ImpactClientRpc(ball.Id, ball.Previous, Vector3.up, false, false, ball.Started + ball.Lifetime);
                    _balls.RemoveAt(i);
                }
                else _balls[i] = ball;
            }
        }

        private bool SampleBall(ref Ball ball)
        {
            if (ball.SampleIndex == 1)
            {
                var count = Physics.OverlapSphereNonAlloc(ball.Origin, ball.Radius, _overlaps, hitLayers,
                    QueryTriggerInteraction.Ignore);
                var overlaps = count == _overlaps.Length ? Physics.OverlapSphere(ball.Origin, ball.Radius,
                    hitLayers, QueryTriggerInteraction.Ignore) : _overlaps;
                if (overlaps != _overlaps) count = overlaps.Length;
                for (var i = 0; i < count; i++)
                {
                    var overlap = overlaps[i];
                    if (overlap.GetComponentInParent<WaterObject>() != null) continue;
                    if (overlap.GetComponentInParent<ShipCannonBattery>() == this) continue;
                    var player = overlap.GetComponentInParent<NetworkPlayerController>();
                    if (player != null && player.OwnerClientId == ball.Shooter) continue;
                    overlap.GetComponentInParent<ShipCannonBattery>()?.ApplyDamageServer(ball.Damage);
                    overlap.GetComponentInParent<PlayerInventory>()?.ApplyDamageServer(ball.Damage);
                    ImpactClientRpc(ball.Id, overlap.ClosestPoint(ball.Origin), -ball.Velocity.normalized,
                        false, true, ball.Started);
                    return true;
                }
            }
            var age = Mathf.Min(ball.Lifetime, (float)(ball.SampleIndex * CollisionSampleStep));
            var position = ball.Origin + ball.Velocity * age + ball.Gravity * (0.5f * age * age);
            var delta = position - ball.Previous;
            var distance = delta.magnitude;
            var nearest = float.PositiveInfinity;
            var hitPoint = position;
            var normal = Vector3.up;
            Collider collider = null;
            if (distance > 0.0001f)
            {
                var count = Physics.SphereCastNonAlloc(ball.Previous, ball.Radius, delta / distance,
                    _hits, distance, hitLayers, QueryTriggerInteraction.Ignore);
                // Dense overlaps must not silently discard a nearer solid.
                var hits = count == _hits.Length ? Physics.SphereCastAll(ball.Previous, ball.Radius,
                    delta / distance, distance, hitLayers, QueryTriggerInteraction.Ignore) : _hits;
                if (hits != _hits) count = hits.Length;
                for (var h = 0; h < count; h++)
                {
                    var candidate = hits[h];
                    if (candidate.collider == null || candidate.distance >= nearest) continue;
                    if (candidate.collider.GetComponentInParent<WaterObject>() != null) continue;
                    if (age < 0.2f && candidate.collider.GetComponentInParent<ShipCannonBattery>() == this) continue;
                    var player = candidate.collider.GetComponentInParent<NetworkPlayerController>();
                    if (age < 0.2f && player != null && player.OwnerClientId == ball.Shooter) continue;
                    nearest = candidate.distance;
                    hitPoint = candidate.point;
                    normal = candidate.normal;
                    collider = candidate.collider;
                }
            }
            var waterFraction = WaterCrossing(ball.Previous, position, ball.Radius);
            var water = waterFraction >= 0f && waterFraction * distance <= nearest;
            if (water || collider != null)
            {
                var fraction = water ? waterFraction : Mathf.Clamp01(nearest / Mathf.Max(0.0001f, distance));
                if (water)
                {
                    hitPoint = Vector3.Lerp(ball.Previous, position, waterFraction);
                    hitPoint.y -= ball.Radius;
                }
                if (collider != null && !water)
                {
                    collider.GetComponentInParent<ShipCannonBattery>()?.ApplyDamageServer(ball.Damage);
                    collider.GetComponentInParent<PlayerInventory>()?.ApplyDamageServer(ball.Damage);
                }
                var previousAge = (ball.SampleIndex - 1) * CollisionSampleStep;
                var impactTime = ball.Started + previousAge + (age - previousAge) * fraction;
                ImpactClientRpc(ball.Id, hitPoint, water ? Vector3.up : normal, water, true, impactTime);
                return true;
            }
            ball.Previous = position;
            return false;
        }

        private float WaterCrossing(Vector3 from, Vector3 to, float radius)
        {
            var water = _alignment != null ? _alignment.heightInterface : null;
            if (water == null || water.waterObject == null || water.waterObject.material == null) return -1f;
            var level = water.GetWaterLevel();
            _waterSampler.positions[0] = from;
            _waterSampler.positions[1] = to;
            _waterSampler.heightValues[0] = _waterSampler.heightValues[1] = level;
            if (water.waveProfile != null) Gerstner.ComputeHeight(_waterSampler, water);
            var above = from.y - _waterSampler.heightValues[0] - radius;
            var below = to.y - _waterSampler.heightValues[1] - radius;
            if (above <= 0f) return 0f;
            if (below > 0f) return -1f;
            return Mathf.Clamp01(above / (above - below));
        }

        [ClientRpc]
        private void ShotClientRpc(int index, int id, Vector3 origin, Vector3 velocity, Vector3 gravity, double started, float radius, float lifetime)
            => CannonEffects.Shot(this, cannons[index], id, origin, velocity, gravity, started, radius, lifetime, ballMaterial, effectMaterial);
        [ClientRpc]
        private void ImpactClientRpc(int id, Vector3 point, Vector3 normal, bool water, bool show, double at)
            => CannonEffects.Impact(this, id, point, normal, water, show, at, waterSplashPrefab, effectMaterial, ballMaterial);

        public void ApplyDamageServer(float amount)
        {
            if (IsServer && IsFinite(amount))
                _health.Value = Mathf.Max(0f, _health.Value - Mathf.Max(0f, amount) / (1f + _armorBonus.Value));
        }
        public void RepairServer(float amount)
        {
            if (IsServer) _health.Value = Mathf.Min(maximumHealth, _health.Value + Mathf.Max(0f, amount));
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
        }
    }
}
