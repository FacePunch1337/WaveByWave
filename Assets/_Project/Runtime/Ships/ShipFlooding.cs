using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using WaveByWave.Items;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    public struct HullBreach : INetworkSerializable, IEquatable<HullBreach>
    {
        public int Id;
        public Vector2 UV, RadiusUV;
        public Vector3 Position, Normal;
        public float Leak, Repair;
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Id); serializer.SerializeValue(ref UV); serializer.SerializeValue(ref RadiusUV);
            serializer.SerializeValue(ref Position); serializer.SerializeValue(ref Normal);
            serializer.SerializeValue(ref Leak); serializer.SerializeValue(ref Repair);
        }
        public bool Equals(HullBreach other) => Id == other.Id && UV == other.UV && RadiusUV == other.RadiusUV &&
            Position == other.Position && Normal == other.Normal && Leak.Equals(other.Leak) && Repair.Equals(other.Repair);
    }

    // Shared by gameplay and regression checks. Transfers conserve even the last partial bucket.
    public struct FloodReservoir
    {
        public float Litres;
        public float Add(float amount, float capacity)
        {
            if (!float.IsFinite(amount) || amount <= 0f) return 0f;
            var before = Litres;
            Litres = Mathf.Clamp(Litres + amount, 0f, capacity);
            return Litres - before;
        }
        public float Take(float amount)
        {
            if (!float.IsFinite(amount) || amount <= 0f) return 0f;
            var taken = Mathf.Min(amount, Litres);
            Litres -= taken;
            return taken;
        }
    }

    [DisallowMultipleComponent, RequireComponent(typeof(ShipCannonBattery))]
    public sealed class ShipFlooding : NetworkBehaviour, IBucketWaterSource
    {
        public ShipHullHoles Hull;
        public ShipWaterVolume WaterVolume;
        public Material SprayMaterial;
        [Min(10f)] public float CapacityLitres = 600f;
        [Min(0.01f)] public float LeakLitresPerSecond = 3f;
        [Min(1f)] public float ReferenceCannonDamage = 25f;
        [Min(0.25f)] public float RepairSeconds = 4f;
        [Min(0.5f)] public float RepairDistance = 2.8f;
        [Min(1f)] public float SinkDuration = 8f;
        [Min(1f)] public float SinkDepth = 14f;
        private readonly NetworkVariable<float> _waterLitres = new();
        private readonly NetworkVariable<bool> _sinking = new();
        private readonly NetworkVariable<double> _sinkStarted = new();
        private NetworkList<HullBreach> _holes;
        private FloodReservoir _reservoir;
        private ShipCannonBattery _battery;
        private float _leakRate, _publishIn;
        private int _nextHole;
        private bool _boundarySiteWarningShown;
        private ShipRepairPresentation _repairPresentation;
        private Vector3 _sinkPosition;
        private Quaternion _sinkRotation;
        private readonly RaycastHit[] _repairObstructions = new RaycastHit[32];
        private GUIStyle _statusStyle, _titleStyle, _detailStyle;
        private static readonly List<ShipFlooding> Active = new();
        public int HoleCount => _holes?.Count ?? 0;
        public HullBreach GetHole(int index) => _holes[index];
        public bool IsSinking => _sinking.Value;
        private bool Submerged => IsSinking && NetworkManager != null &&
            NetworkManager.ServerTime.Time - _sinkStarted.Value >= SinkDuration * 0.65f;
        public float WaterLitres => IsServer ? _reservoir.Litres : _waterLitres.Value;
        public float Fill => Mathf.Clamp01(WaterLitres / Mathf.Max(1f, CapacityLitres));
        public float Inflow => _leakRate;
        public static bool VoyageOver
        {
            get { foreach (var ship in Active) if (ship != null && ship._battery != null && ship._battery.VoyageEnded) return true; return false; }
        }
        public float RepairMultiplier(NetworkPlayerController player) => player.RepairSpeed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Active.Clear();
        private void Awake()
        { _holes = new NetworkList<HullBreach>(); _battery = GetComponent<ShipCannonBattery>(); }

        public override void OnNetworkSpawn()
        {
            Active.Add(this);
            _holes.OnListChanged += HolesChanged;
            _sinking.OnValueChanged += SinkingChanged;
            if (!Application.isBatchMode)
            {
                _repairPresentation = GetComponent<ShipRepairPresentation>();
                if (_repairPresentation == null) _repairPresentation = gameObject.AddComponent<ShipRepairPresentation>();
                _repairPresentation.Initialize(this);
            }
            RebuildPresentation();
            WaterVolume?.Present(Fill, Submerged);
        }
        public override void OnNetworkDespawn()
        {
            Active.Remove(this); _holes.OnListChanged -= HolesChanged;
            _sinking.OnValueChanged -= SinkingChanged;
            if (_repairPresentation != null) Destroy(_repairPresentation);
        }
        public override void OnDestroy()
        { Active.Remove(this); base.OnDestroy(); }

        private void HolesChanged(NetworkListEvent<HullBreach> change) => RebuildPresentation();
        private void SinkingChanged(bool previous, bool current) => RebuildPresentation();
        private void RebuildPresentation()
        {
            _leakRate = 0f;
            for (var i = 0; i < HoleCount; i++) _leakRate += _holes[i].Leak;
            if (!Application.isBatchMode) Hull?.Present(this);
        }

        public void HitServer(float damage, Vector3 point)
        {
            if (!IsServer || !IsSpawned || IsSinking || _battery.VoyageEnded ||
                !float.IsFinite(damage) || damage <= 0f || Hull == null) return;
            if (HoleCount < ShipHullHoles.MaximumHoles && Hull.TryChooseSite(point, Occupied, out var site))
            {
                _holes.Add(new HullBreach { Id = ++_nextHole, UV = site.UV, RadiusUV = Hull.RadiusUV(site),
                    Position = site.Position, Normal = site.Normal, Leak = LeakLitresPerSecond *
                        Mathf.Clamp(damage / ReferenceCannonDamage, 0.25f, 3f) });
            }
            else if (HoleCount > 0)
            {
                // Repeated hits enlarge an existing leak once the visual budget is full.
                var nearest = 0; var distance = float.PositiveInfinity;
                for (var i = 0; i < HoleCount; i++)
                {
                    var d = (Hull.transform.TransformPoint(_holes[i].Position) - point).sqrMagnitude;
                    if (d < distance) { nearest = i; distance = d; }
                }
                var hole = _holes[nearest];
                hole.Leak = Mathf.Min(LeakLitresPerSecond * 8f, hole.Leak + LeakLitresPerSecond);
                hole.Repair = 0f; _holes[nearest] = hole;
            }
            else Debug.LogError("No valid hull breach sites. Select ShipHullHoles and rebuild hole sites after editing UV regions.", Hull);
        }

        public bool OpenBoundaryBreachServer(float leakMultiplier)
        {
            if (!IsServer || !IsSpawned || IsSinking || _battery.VoyageEnded || Hull == null ||
                !float.IsFinite(leakMultiplier) || leakMultiplier <= 0f ||
                HoleCount >= ShipHullHoles.MaximumHoles) return false;
            if (!Hull.TryChooseRandomSite(Occupied, out var site))
            {
                if (!_boundarySiteWarningShown)
                {
                    Debug.LogWarning("No free hull sites for battlefield boundary breaches. Rebuild the allowed hull hole sites.", Hull);
                    _boundarySiteWarningShown = true;
                }
                return false;
            }
            _holes.Add(new HullBreach
            {
                Id = ++_nextHole, UV = site.UV, RadiusUV = Hull.RadiusUV(site),
                Position = site.Position, Normal = site.Normal,
                Leak = LeakLitresPerSecond * Mathf.Clamp(leakMultiplier, 0.1f, 8f)
            });
            return true;
        }

        private bool Occupied(Vector3 position)
        {
            for (var i = 0; i < HoleCount; i++)
                if ((_holes[i].Position - position).sqrMagnitude < Hull.HoleRadius * Hull.HoleRadius * 4f) return true;
            return false;
        }

        public bool FindRepairTarget(Vector3 origin, Vector3 direction, out int index)
        {
            index = -1;
            if (Hull == null || IsSinking || _battery.VoyageEnded) return false;
            var closest = RepairDistance;
            for (var i = 0; i < HoleCount; i++)
            {
                var point = Hull.transform.TransformPoint(_holes[i].Position);
                var delta = point - origin;
                var along = Vector3.Dot(delta, direction);
                if (along < 0f || delta.magnitude > closest ||
                    (delta - direction * along).sqrMagnitude > 0.5f * 0.5f) continue;
                index = i; closest = delta.magnitude;
            }
            return index >= 0;
        }

        public bool RepairServer(NetworkPlayerController player, PlayerInventory inventory,
            Vector3 origin, Vector3 direction, float elapsed)
        {
            if (!IsServer || !IsSpawned || inventory.Health <= 0f || inventory.IsCarryingChest ||
                !inventory.TryGetDefinition(inventory.ServerSelectedIndex, out var plank) ||
                plank.SupplyKind != SupplyKind.Plank ||
                !FindRepairTarget(origin, direction, out var index)) return false;
            var hole = _holes[index];
            var target = Hull.transform.TransformPoint(hole.Position);
            // Allow the hull's own surface at the endpoint, but never repair through a deck or bulkhead.
            var delta = target - origin;
            var count = Physics.RaycastNonAlloc(origin, delta.normalized, _repairObstructions, Mathf.Max(0f, delta.magnitude - 0.35f),
                ~0, QueryTriggerInteraction.Ignore);
            if (count == _repairObstructions.Length) return false;
            for (var i = 0; i < count; i++)
                if (_repairObstructions[i].collider.GetComponentInParent<NetworkPlayerController>() != player) return false;
            hole.Repair = AdvanceRepair(hole.Repair, elapsed, RepairMultiplier(player), RepairSeconds);
            if (hole.Repair >= 1f)
            {
                if (!inventory.TryConsumeServer(inventory.ServerSelectedIndex, 1, out _)) return false;
                _holes.RemoveAt(index);
            }
            else _holes[index] = hole;
            return true;
        }

        public static float AdvanceRepair(float progress, float elapsed, float multiplier, float duration)
            => Mathf.Clamp01(progress + Mathf.Clamp(elapsed, 0f, 0.15f) * Mathf.Max(0f, multiplier) / Mathf.Max(0.25f, duration));

        public static ShipFlooding CompartmentAt(Vector3 point)
        {
            foreach (var ship in Active)
                if (ship != null && ship.IsSpawned && ship.WaterVolume != null &&
                    ship.WaterVolume.MasksOceanAt(point)) return ship;
            return null;
        }

        public static ShipFlooding RepairTarget(Vector3 origin, Vector3 direction, out int index)
        {
            foreach (var ship in Active)
                if (ship != null && ship.FindRepairTarget(origin, direction, out index)) return ship;
            index = -1; return null;
        }

        public static bool RayWater(Vector3 origin, Vector3 direction, float distance, out ShipFlooding ship, out Vector3 point)
        {
            ship = null; point = default;
            var nearest = distance;
            foreach (var candidate in Active)
                if (candidate != null && !candidate.IsSinking && candidate.WaterVolume != null &&
                    candidate.WaterVolume.RaySurface(origin, direction, nearest, candidate.Fill, out var hit))
                { ship = candidate; point = hit; nearest = Vector3.Distance(origin, hit); }
            return ship != null;
        }

        public float ScoopWaterServer(float litres, Vector3 point)
        {
            if (!IsServer || IsSinking || _battery.VoyageEnded || WaterVolume == null ||
                !WaterVolume.ContainsColumn(point) || Mathf.Abs(point.y - WaterVolume.HeightAt(point, Fill)) > 0.6f) return 0f;
            var taken = _reservoir.Take(litres); Publish(); return taken;
        }
        public void AddWaterServer(float litres, Vector3 point)
        {
            if (!IsServer || IsSinking || _battery.VoyageEnded || WaterVolume == null || !WaterVolume.ContainsColumn(point)) return;
            _reservoir.Add(litres, CapacityLitres); Publish(); CheckSinking();
        }

        private void Update()
        {
            if (!IsSpawned) return;
            if (IsServer && !IsSinking && !_battery.VoyageEnded && !_battery.UpgradePaused)
            {
                _reservoir.Add(_leakRate * Time.deltaTime, CapacityLitres);
                _publishIn -= Time.deltaTime;
                if (_publishIn <= 0f) { Publish(); _publishIn = 0.1f; }
                CheckSinking();
            }
            WaterVolume?.Present(Fill, Submerged);
        }

        private void Publish() => _waterLitres.Value = _reservoir.Litres;
        private void CheckSinking()
        {
            if (IsSinking || _battery.VoyageEnded || _reservoir.Litres < CapacityLitres) return;
            _sinkStarted.Value = NetworkManager.ServerTime.Time;
            _sinking.Value = true;
            _sinkPosition = transform.position; _sinkRotation = transform.rotation;
            Publish(); RebuildPresentation();
            _battery.FinishVoyageServer(false, SinkDuration + 2f);
        }

        public bool TrySinkingPose(out Vector3 position, out Quaternion rotation)
        {
            position = default; rotation = default;
            if (!IsSpawned || !IsServer || !IsSinking) return false;
            var t = Mathf.Clamp01((float)(NetworkManager.ServerTime.Time - _sinkStarted.Value) / SinkDuration);
            position = _sinkPosition + Vector3.down * (SinkDepth * t * t);
            rotation = _sinkRotation * Quaternion.Euler(12f * t, 0f, 26f * t);
            return true;
        }

        private void OnGUI()
        {
            if (!IsSpawned || NetworkManager == null || !NetworkManager.IsClient) return;
            _statusStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
            _titleStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 42, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _detailStyle ??= new GUIStyle(_statusStyle) { fontSize = 22, wordWrap = true };
            var width = Mathf.Min(410f, Screen.width - 30f);
            var rect = new Rect((Screen.width - width) * 0.5f, 65f, width, 52f);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(rect.x, rect.y, rect.width, 28f),
                $"Вода: {Fill:P0}   •   Пробоины: {HoleCount}   •   +{Inflow:0.#} л/с", _statusStyle);
            var color = GUI.color;
            GUI.color = Color.Lerp(new Color(0.1f, 0.65f, 0.8f), new Color(0.9f, 0.18f, 0.12f), Fill);
            GUI.DrawTexture(new Rect(rect.x + 10f, rect.y + 33f, (rect.width - 20f) * Fill, 9f), Texture2D.whiteTexture);
            GUI.color = color;
            if (!_battery.VoyageEnded) return;
            GUI.depth = -1000;
            GUI.color = new Color(0.015f, 0.025f, 0.04f, 0.86f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            var victory = _battery.Phase == VoyagePhase.Victory;
            GUI.Label(new Rect(0, Screen.height * 0.32f, Screen.width, 70f), victory ? "ПОБЕДА" : "КОРАБЛЬ ЗАТОНУЛ", _titleStyle);
            GUI.Label(new Rect(Screen.width * 0.15f, Screen.height * 0.47f, Screen.width * 0.7f, 100f),
                (victory ? "Все волны отражены. Пора возвращаться в порт." : "Вода заполнила трюм. Плавание окончено.") +
                $"\nВозвращение в PORT через {Mathf.CeilToInt(_battery.ReturnToPortIn)} с", _detailStyle);
            GUI.color = color; GUI.depth = 0;
        }
    }
}
