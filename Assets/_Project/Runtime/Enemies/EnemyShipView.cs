using System.Collections.Generic;
using UnityEngine;
using WaveByWave.Player;
using WaveByWave.Ships;
using WaveByWave.Combat;

namespace WaveByWave.Enemies
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class EnemyShipView : MonoBehaviour
    {
        [SerializeField] private Rigidbody body;
        [SerializeField] private Collider[] hullColliders;
        [SerializeField] private Renderer[] damageRenderers;
        [SerializeField] private EnemyShipHardpoint[] hardpoints;
        [SerializeField] private EnemyShipCrewSlot[] crewSlots;

        [Header("DOTS ship contact bounds")]
        [SerializeField, Tooltip("Center of the lightweight ship-contact box in prefab-local coordinates.")]
        private Vector3 contactBoundsCenter = new(0f, 1.2f, 0f);
        [SerializeField, Tooltip("Full size of the lightweight ship-contact box. This replaces mesh collision for ship-to-ship movement.")]
        private Vector3 contactBoundsSize = new(6.4f, 3f, 15f);

        [Header("Buoyancy footprint")]
        [SerializeField, Tooltip("Center of the wave-sampling area in prefab-local coordinates. The cyan gizmo shows the four sample points.")]
        private Vector3 buoyancyCenter;
        [SerializeField, Tooltip("Width (X) and length (Z) of the area that follows the water, like AlignTransformToWater.Surface Size.")]
        private Vector2 buoyancySize = new(6f, 15f);

        private MaterialPropertyBlock _properties;
        private EnemyShipDefinition _definition;
        private float _flashUntil;
        private bool _dead;
        private bool _flashActive;
        private static readonly int DamageFlashId = Shader.PropertyToID("_EnemyDamageFlash");
        private Vector3 _simulationPosition;
        private Quaternion _simulationRotation;
        private bool _hasSimulationPose;

        public int ShipId { get; private set; }
        public Vector3 ContactBoundsCenter => contactBoundsCenter;
        public Vector3 ContactBoundsHalfExtents => contactBoundsSize * 0.5f;
        public Vector3 BuoyancyCenter => buoyancyCenter;
        public Vector2 BuoyancySize => buoyancySize;
        public Matrix4x4 SimulationFrame => _hasSimulationPose
            ? Matrix4x4.TRS(_simulationPosition, _simulationRotation, transform.lossyScale)
            : transform.localToWorldMatrix;

        public void ConfigurePrefabReferences(
            Rigidbody configuredBody,
            Collider[] configuredHullColliders,
            Renderer[] configuredDamageRenderers,
            EnemyShipHardpoint[] configuredHardpoints,
            EnemyShipCrewSlot[] configuredCrewSlots)
        {
            body = configuredBody;
            hullColliders = configuredHullColliders;
            damageRenderers = configuredDamageRenderers;
            hardpoints = configuredHardpoints;
            crewSlots = configuredCrewSlots;
        }

        private void Awake()
        {
            _properties = new MaterialPropertyBlock();
        }

        private void OnValidate()
        {
            contactBoundsSize = new Vector3(
                Mathf.Max(0.1f, Mathf.Abs(contactBoundsSize.x)),
                Mathf.Max(0.1f, Mathf.Abs(contactBoundsSize.y)),
                Mathf.Max(0.1f, Mathf.Abs(contactBoundsSize.z)));
            buoyancySize = new Vector2(Mathf.Max(0.1f, Mathf.Abs(buoyancySize.x)),
                Mathf.Max(0.1f, Mathf.Abs(buoyancySize.y)));
        }

        private void OnDrawGizmosSelected()
        {
            var previous = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.45f, 0.08f, 0.9f);
            Gizmos.DrawWireCube(contactBoundsCenter, contactBoundsSize);
            Gizmos.color = new Color(0.1f, 0.9f, 1f, 0.95f);
            var halfX = buoyancySize.x * 0.5f;
            var halfZ = buoyancySize.y * 0.5f;
            Gizmos.DrawWireCube(buoyancyCenter, new Vector3(buoyancySize.x, 0.02f, buoyancySize.y));
            Gizmos.DrawWireSphere(buoyancyCenter + Vector3.left * halfX, 0.12f);
            Gizmos.DrawWireSphere(buoyancyCenter + Vector3.right * halfX, 0.12f);
            Gizmos.DrawWireSphere(buoyancyCenter + Vector3.back * halfZ, 0.12f);
            Gizmos.DrawWireSphere(buoyancyCenter + Vector3.forward * halfZ, 0.12f);
            Gizmos.matrix = previous;
        }

        public void Initialize(int id, EnemyShipDefinition definition)
        {
            ShipId = id;
            _definition = definition;
            name = $"Enemy Ship {id}";
            var anchor = GetComponent<EnemySurfaceAnchor>();
            if (anchor == null) anchor = gameObject.AddComponent<EnemySurfaceAnchor>();
            anchor.Key = $"DOTS.EnemyShip.{id}";
            DotsEnemyRuntime.Instance?.RegisterSurface(transform);
        }

        public void SetSimulationPose(Vector3 position, Quaternion rotation)
        {
            _simulationPosition = position;
            _simulationRotation = rotation;
            _hasSimulationPose = true;
        }

        private void FixedUpdate() => RestoreSimulationPose();

        internal void RestoreSimulationPose()
        {
            if (!_hasSimulationPose) return;
            if (body != null)
            {
                body.position = _simulationPosition;
                body.rotation = _simulationRotation;
            }
            transform.SetPositionAndRotation(_simulationPosition, _simulationRotation);
        }

        public void SetPose(Vector3 position, Quaternion rotation) => transform.SetPositionAndRotation(position, rotation);

        public void SetHealth(float health, uint hitRevision)
        {
            if (hitRevision != 0) _flashUntil = Time.unscaledTime + 0.12f;
            var dead = health <= 0f;
            if (dead != _dead)
            {
                _dead = dead;
                if (hullColliders != null)
                    foreach (var collider in hullColliders)
                        if (collider != null) collider.enabled = !dead;
                if (dead && _definition != null && _definition.DeathEffectPrefab != null)
                    Object.Instantiate(_definition.DeathEffectPrefab, transform.position, Quaternion.identity);
            }
        }

        internal void UpdateDamageFlash()
        {
            var active = Time.unscaledTime < _flashUntil;
            if (_flashActive == active) return;
            _flashActive = active;
            var flash = active ? 1f : 0f;
            if (damageRenderers == null) return;
            foreach (var renderer in damageRenderers)
            {
                if (renderer == null || renderer.sharedMaterial == null ||
                    !renderer.sharedMaterial.HasProperty(DamageFlashId)) continue;
                renderer.GetPropertyBlock(_properties);
                _properties.SetFloat(DamageFlashId, flash);
                renderer.SetPropertyBlock(_properties);
            }
        }

        public IReadOnlyList<Vector3> GetCrewLocalPositions(int requestedCount, EnemyShipDefinition definition = null)
        {
            var positions = new List<Vector3>(Mathf.Max(0, requestedCount));
            if (crewSlots != null)
                for (var i = 0; positions.Count < requestedCount && i < crewSlots.Length; i++)
                    if (crewSlots[i] != null) positions.Add(transform.InverseTransformPoint(crewSlots[i].transform.position));
            // A slot is a spawn point, not a one-person capacity. Reuse authored points
            // for the whole crew; crowd separation spreads them after spawning.
            var authoredCount = positions.Count;
            if (authoredCount > 0)
            {
                while (positions.Count < requestedCount) positions.Add(positions[positions.Count % authoredCount]);
                return positions;
            }
            definition ??= _definition;
            var half = definition != null ? definition.CollisionHalfExtents : new Vector3(3, 1.5f, 7);
            var center = definition != null ? definition.CollisionCenter : new Vector3(0, 1, 0);
            while (positions.Count < requestedCount)
            {
                var index = positions.Count;
                var columns = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(requestedCount)));
                var row = index / columns;
                var column = index % columns;
                var x = Mathf.Lerp(-half.x * 0.55f, half.x * 0.55f,
                    columns == 1 ? 0.5f : column / (float)(columns - 1));
                var rows = Mathf.Max(1, Mathf.CeilToInt(requestedCount / (float)columns));
                var z = Mathf.Lerp(-half.z * 0.55f, half.z * 0.55f,
                    rows == 1 ? 0.5f : row / (float)(rows - 1));
                positions.Add(new Vector3(center.x + x, center.y + half.y + 0.04f, center.z + z));
            }
            return positions;
        }

        public bool TryGetMuzzle(float side, uint revision, out Vector3 position)
        {
            position = default;
            if (hardpoints == null || hardpoints.Length == 0) return false;
            var requested = side < 0f ? EnemyShipSide.Port : EnemyShipSide.Starboard;
            var count = 0;
            foreach (var hardpoint in hardpoints)
                if (hardpoint != null && hardpoint.Side == requested) count++;
            if (count == 0) return false;
            var selected = (int)(revision % (uint)count);
            foreach (var hardpoint in hardpoints)
            {
                if (hardpoint == null || hardpoint.Side != requested) continue;
                if (selected-- != 0) continue;
                position = hardpoint.transform.position;
                return true;
            }
            return false;
        }

        public void PlayShot(uint revision, Vector3 origin, Vector3 velocity, float started)
        {
            if (_definition == null || _definition.ProjectilePrefab == null) return;
            DotsCannonProjectileVisuals.Add((ulong)ShipId, revision, true,
                _definition.ProjectilePrefab, origin, velocity,
                Vector3.down * _definition.ProjectileGravity, started,
                _definition.ProjectileLifetime, _definition.MuzzleEffectPrefab);
        }

        public void PlayImpact(uint shotRevision, Vector3 point, Vector3 normal, bool water, bool show, float at)
        {
            if (_definition == null) return;
            DotsCannonProjectileVisuals.Impact((ulong)ShipId, shotRevision, true,
                point, normal, water, show, at,
                _definition.WaterImpactPrefab, _definition.ImpactEffectPrefab);
        }

        private void OnDestroy()
        {
            DotsEnemyShipRuntime.Instance?.UnregisterView(ShipId, this);
        }
    }

}
