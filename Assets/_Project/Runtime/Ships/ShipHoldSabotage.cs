using System.Collections.Generic;
using UnityEngine;
using WaveByWave.Enemies;

namespace WaveByWave.Ships
{
    // Server-owned objective and a single damage budget for the entire ship.
    // Animations and breaches use the existing enemy ghosts and flooding network list.
    [DisallowMultipleComponent, RequireComponent(typeof(ShipFlooding))]
    public sealed class ShipHoldSabotage : MonoBehaviour
    {
        public bool EnableSabotage = true;
        [Tooltip("Ship-local volume containing the walkable hold floor. Exclude upper decks.")]
        public Bounds HoldBounds = new(new Vector3(1.21f, 1.1f, -0.55f), new Vector3(3.9f, 0.8f, 9.5f));
        [Range(2, 8), Tooltip("Number of hold-edge positions where enemies can stand and play their attack animation.")]
        public int AttackPositions = 8;
        [Min(0f)] public float GraceSeconds = 3f;
        [Min(0.25f), Tooltip("Seconds required for one enemy to create a breach. Multiple contributors divide this time.")]
        public float SecondsPerBreach = 12f;
        [Min(0.25f), InspectorName("Fastest Breach Interval"),
         Tooltip("Hard speed limit for the whole crowd: breaches can never appear more often than once per this many seconds. Keep this lower than Seconds Per Breach if additional enemies should accelerate sabotage.")]
        public float MinimumBreachInterval = 3f;
        [Min(1), Tooltip("Maximum number of enemies that can accelerate sabotage. Extra enemies still attack visually but do not make breaches appear faster.")]
        public int MaximumContributors = 4;
        [Min(0.1f)] public float LeakMultiplier = 1f;
        [Min(0.1f)] public float ArrivalDistance = 0.3f;
        public bool ShowHoldBounds = true;

        private readonly Dictionary<EnemyDeckNavigationData, int[]> _slots = new();
        private Bounds _cachedBounds;
        private int _cachedCount;
        private ShipFlooding _flooding;
        private int _attackers;
        private bool _strike;
        private Vector3 _impact;
        private HoldBreachBudget _budget;
        public static readonly List<ShipHoldSabotage> Active = new();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Active.Clear();
        private void OnEnable() { if (!Active.Contains(this)) Active.Add(this); _flooding = GetComponent<ShipFlooding>(); }
        private void OnDisable() { Active.Remove(this); _budget = default; }
        private void OnValidate() => _slots.Clear();
        public bool CanSabotage => isActiveAndEnabled && EnableSabotage && _flooding != null &&
            _flooding.IsSpawned && _flooding.IsServer && !_flooding.IsSinking && !ShipFlooding.VoyageOver;

        public bool TryGetObjective(out ulong support,out Vector3 position)
        {
            support=0; position=default;
            if (!CanSabotage) return false;
            support=_flooding.NetworkObjectId+1;
            position=transform.TransformPoint(HoldBounds.center);
            return true;
        }

        public void BeginFrame(bool defended)
        {
            _attackers = 0; _strike = false;
            if (defended || !CanSabotage) _budget = default;
        }
        public void RegisterAttacker(Vector3 impact, bool strike)
        {
            _attackers++;
            if (strike) { _strike = true; _impact = impact; }
        }
        public void EndFrame(float deltaTime)
        {
            if (!CanSabotage) return;
            if (_budget.Tick(deltaTime, _attackers, _strike, GraceSeconds, SecondsPerBreach,
                    MinimumBreachInterval, MaximumContributors))
                _flooding.OpenSabotageBreachServer(_impact, LeakMultiplier);
        }

        public bool TryAttackPosition(EnemyDeckNavigationData map, int id, out int slot, out Vector3 localFeet, out Vector3 outward)
        {
            localFeet = outward = default; slot = -1;
            if (map == null || !map.IsBaked) return false;
            var count = Mathf.Clamp(AttackPositions, 2, 8);
            if (_cachedBounds != HoldBounds || _cachedCount != count)
            { _slots.Clear(); _cachedBounds = HoldBounds; _cachedCount = count; }
            if (!_slots.TryGetValue(map, out var nodes))
            {
                nodes = new int[count];
                for (var s = 0; s < count; s++)
                {
                    nodes[s] = -1;
                    var side = s % 2 == 0 ? -1 : 1;
                    var desired = HoldBounds.center + new Vector3(side * HoldBounds.extents.x, 0,
                        Mathf.Lerp(-0.7f, 0.7f, (s / 2) / (float)Mathf.Max(1, (count - 1) / 2)) * HoldBounds.extents.z);
                    var best = float.PositiveInfinity;
                    for (var n = 0; n < map.Nodes.Length; n++)
                    {
                        var node = map.Nodes[n];
                        if (!node.Boundary || !HoldBounds.Contains(node.Position)) continue;
                        var cost = (node.Position - desired).sqrMagnitude;
                        if (cost >= best) continue;
                        best = cost; nodes[s] = n;
                    }
                }
                _slots.Add(map, nodes);
            }
            slot = (id & int.MaxValue) % count;
            var index = nodes[slot];
            if (index < 0) return false;
            localFeet = map.Nodes[index].Position;
            outward = slot % 2 == 0 ? Vector3.left : Vector3.right;
            return true;
        }

        private void OnDrawGizmosSelected()
        {
            if (!ShowHoldBounds) return;
            var previous = Gizmos.matrix; Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.35f, 0.15f, 0.8f);
            Gizmos.DrawWireCube(HoldBounds.center, HoldBounds.size);
            Gizmos.matrix = previous;
        }
    }

    public struct HoldBreachBudget
    {
        private float _warmup, _progress;
        public bool Tick(float deltaTime, int attackers, bool strike, float grace, float soloInterval, float minimumInterval, int contributors)
        {
            if (attackers <= 0) { this = default; return false; }
            var elapsed = Mathf.Clamp(deltaTime, 0, 0.2f);
            if (_warmup < grace)
            {
                var delay = Mathf.Min(elapsed, grace - _warmup);
                _warmup += delay; elapsed -= delay;
            }
            var interval = Mathf.Max(0.25f, minimumInterval, soloInterval / Mathf.Clamp(attackers, 1, Mathf.Max(1, contributors)));
            _progress = Mathf.Min(1, _progress + elapsed / interval);
            if (_progress < 1 || !strike) return false;
            _progress = 0;
            return true;
        }
    }
}
