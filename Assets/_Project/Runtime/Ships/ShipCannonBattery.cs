using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using WaveByWave.Items;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    public enum VoyagePhase : byte { Day, Sunset, Night, Sunrise, Victory, Defeat }

    [DefaultExecutionOrder(1250)]
    [RequireComponent(typeof(NetworkShipController))]
    public sealed class ShipCannonBattery : NetworkBehaviour
    {
        [SerializeField] private Cannon[] cannons;
        [SerializeField] private ShipTreasureChest treasureChest;
        private readonly NetworkVariable<float> _damageBonus = new();
        private readonly NetworkVariable<int> _treasureExperience = new();
        private readonly NetworkVariable<int> _crewLevel = new(1);
        private readonly NetworkVariable<bool> _upgradePaused = new();
        private readonly NetworkVariable<byte> _voyagePhase = new();
        private readonly NetworkVariable<float> _voyageDayProgress = new();
        private readonly NetworkVariable<float> _voyageHour = new(10f);
        private readonly NetworkVariable<int> _voyageWave = new();
        private readonly NetworkVariable<int> _waveEnemiesDefeated = new();
        private readonly NetworkVariable<int> _waveEnemiesTotal = new();
        private readonly NetworkVariable<Vector4> _battlefield = new();
        private readonly NetworkVariable<double> _battlefieldStarted = new();
        private readonly NetworkVariable<double> _returnToPortAt = new();
        private bool _returnRequested;
        private readonly HashSet<ulong> _pendingRingSelections = new();
        private static readonly HashSet<ShipCannonBattery> ActiveBatteries = new();
        public static bool AnyUpgradePaused
        {
            get
            {
                foreach (var ship in ActiveBatteries) if (ship.IsSpawned && ship.UpgradePaused) return true;
                return false;
            }
        }
        private PlayerRingCatalog _ringCatalog;
        private NetworkShipController _ship;
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        // Upgrade integration point. A value of 0 keeps the cannon at its authored
        // base damage; 0.15 means +15%. Upgrade assets are intentionally unchanged.
        public float CannonDamageModifier => _damageBonus.Value;
        public int TreasureExperience => _treasureExperience.Value;
        public int CrewLevel => _crewLevel.Value;
        public bool UpgradePaused => _upgradePaused.Value;
        public VoyagePhase Phase => (VoyagePhase)_voyagePhase.Value;
        public bool VoyageEnded => Phase == VoyagePhase.Victory || Phase == VoyagePhase.Defeat;
        public float ReturnToPortIn => IsSpawned ? Mathf.Max(0f, (float)(_returnToPortAt.Value - NetworkManager.ServerTime.Time)) : 0f;
        public float DayProgress => _voyageDayProgress.Value;
        public float TimeOfDay => _voyageHour.Value;
        public int WaveNumber => _voyageWave.Value;
        public int WaveEnemiesDefeated => _waveEnemiesDefeated.Value;
        public int WaveEnemiesTotal => _waveEnemiesTotal.Value;
        public bool BattlefieldActive => _battlefield.Value.w > 0f && !VoyageEnded;
        public Vector3 BattlefieldCenter => new(_battlefield.Value.x, _battlefield.Value.y, _battlefield.Value.z);
        public float BattlefieldRadius => _battlefield.Value.w;
        public float BattlefieldAge => IsSpawned ? Mathf.Max(0f,
            (float)(NetworkManager.ServerTime.Time - _battlefieldStarted.Value)) : 0f;
        public bool IsInsideBattlefield(Vector3 worldPosition)
        {
            var circle = _battlefield.Value;
            if (circle.w <= 0f) return false;
            var dx = worldPosition.x - circle.x;
            var dz = worldPosition.z - circle.z;
            return dx * dx + dz * dz <= circle.w * circle.w;
        }
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
        public Cannon[] Cannons => cannons;

        private void Awake()
        {
            _ship = GetComponent<NetworkShipController>();
            if (cannons == null || cannons.Length == 0)
                cannons = GetComponentsInChildren<Cannon>(true);
        }

        public override void OnNetworkSpawn()
        {
            ActiveBatteries.Add(this);
            _ringCatalog = Resources.Load<PlayerRingCatalog>("PlayerRingCatalog");
            _upgradePaused.OnValueChanged += OnUpgradePauseChanged;
            OnUpgradePauseChanged(false, _upgradePaused.Value);
            if (!IsServer) return;
            _crewLevel.Value = 1;
            _treasureExperience.Value = 0;
            _waveEnemiesDefeated.Value = 0;
            _waveEnemiesTotal.Value = 0;
            _battlefield.Value = Vector4.zero;
            NetworkManager.OnClientDisconnectCallback += OnDisconnected;
        }

        public override void OnNetworkDespawn()
        {
            ActiveBatteries.Remove(this);
            _upgradePaused.OnValueChanged -= OnUpgradePauseChanged;
            Time.timeScale = 1f;
            _pendingRingSelections.Clear();
            if (NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= OnDisconnected;
        }

        public override void OnDestroy()
        {
            ActiveBatteries.Remove(this);
            if (_upgradePaused.Value) Time.timeScale = 1f;
            base.OnDestroy();
        }

        public int GetCannonIndex(Cannon cannon) => System.Array.IndexOf(cannons, cannon);

        private static void OnUpgradePauseChanged(bool previous, bool paused) =>
            Time.timeScale = paused ? 0f : 1f;

        private void CheckTreasureLevel()
        {
            if (!IsServer || VoyageEnded || _upgradePaused.Value || _ringCatalog == null ||
                TreasureExperience < _ringCatalog.ExperienceForLevel(_crewLevel.Value + 1)) return;
            _crewLevel.Value++;
            _pendingRingSelections.Clear();
            _upgradePaused.Value = true;
            // The voyage roster is fixed: joining is permitted only back in Port.
            foreach (var client in NetworkManager.ConnectedClientsList)
            {
                if (client.PlayerObject == null ||
                    !client.PlayerObject.TryGetComponent<NetworkPlayerController>(out var player)) continue;
                _pendingRingSelections.Add(client.ClientId);
                if (!player.BeginRingChoiceServer(_crewLevel.Value,
                    unchecked((uint)_crewLevel.Value * 7919u + (uint)client.ClientId * 104729u)))
                    _pendingRingSelections.Remove(client.ClientId);
            }
            if (_pendingRingSelections.Count == 0) _upgradePaused.Value = false;
        }

        internal void MarkRingChoiceComplete(ulong clientId)
        {
            if (!IsServer || !_pendingRingSelections.Remove(clientId)) return;
            if (_pendingRingSelections.Count != 0) return;
            _upgradePaused.Value = false;
            CheckTreasureLevel();
        }

        private void OnDisconnected(ulong clientId) => MarkRingChoiceComplete(clientId);

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
        internal void SetWaveEnemyProgressServer(int defeated, int total)
        {
            if (!IsServer) return;
            total = Mathf.Max(0, total);
            _waveEnemiesTotal.Value = total;
            _waveEnemiesDefeated.Value = Mathf.Clamp(defeated, 0, total);
        }
        internal void SetBattlefieldServer(Vector3 center, float radius)
        {
            // The server writes this snapshot only once for each wave. Ship
            // movement, camera movement and later settings edits cannot move it.
            if (IsServer && !VoyageEnded && _battlefield.Value.w <= 0f)
            {
                _battlefieldStarted.Value = NetworkManager.ServerTime.Time;
                _battlefield.Value = new Vector4(center.x, center.y, center.z, Mathf.Max(0f, radius));
            }
        }
        internal void ClearBattlefieldServer()
        {
            if (IsServer) _battlefield.Value = Vector4.zero;
        }
        internal void SetVoyageVictoryServer(float displayDuration = 5f) => FinishVoyageServer(true, displayDuration);

        public void FinishVoyageServer(bool won, float displayDuration)
        {
            if (!IsServer || !IsSpawned || VoyageEnded) return;
            _voyagePhase.Value = (byte)(won ? VoyagePhase.Victory : VoyagePhase.Defeat);
            _battlefield.Value = Vector4.zero;
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
        public int GetOperatorCannon(ulong clientId)
        {
            var controller = GetComponent<CannonNetworkController>();
            return controller != null ? controller.GetOperatorCannon(clientId) : -1;
        }

        private bool TryPlayer(ulong clientId, out NetworkPlayerController player)
        {
            player = null;
            return NetworkManager.ConnectedClients.TryGetValue(clientId, out var client) &&
                client.PlayerObject != null && client.PlayerObject.TryGetComponent(out player);
        }

        private bool Near(ulong clientId, Transform point, float range = 4f)
        {
            if (VoyageEnded || !TryPlayer(clientId, out var player)) return false;
            var position = player.TryGetPositionOnPlatform(NetworkObject, out var onShip)
                ? onShip : player.transform.position;
            return (position - point.position).sqrMagnitude <= range * range;
        }

        public void ApplyUpgradeServer(ItemDefinition upgrade)
        {
            if (!IsServer || upgrade.Category != ItemCategory.ShipUpgrade) return;
            switch (upgrade.UpgradeStat)
            {
                case ShipUpgradeStat.CannonDamage: _damageBonus.Value = Mathf.Min(2f, _damageBonus.Value + upgrade.UpgradeBonus); break;
                case ShipUpgradeStat.Armor:
                    GetComponent<ShipHullHealth>()?.ApplyArmorUpgradeServer(upgrade.UpgradeBonus);
                    break;
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
