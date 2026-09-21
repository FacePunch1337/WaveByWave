using System.Collections.Generic;
using UnityEngine;
using WaveByWave.Player;
using WaveByWave.Ships;

namespace WaveByWave.Enemies
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class EnemyShipView : MonoBehaviour, IEquipmentDamageReceiver
    {
        [SerializeField] private Rigidbody body;
        [SerializeField] private Collider[] hullColliders;
        [SerializeField] private Renderer[] damageRenderers;
        [SerializeField] private EnemyShipHardpoint[] hardpoints;
        [SerializeField] private EnemyShipCrewSlot[] crewSlots;

        private readonly Dictionary<uint, EnemyShipProjectileVisual> _shots = new();
        private MaterialPropertyBlock _properties;
        private EnemyShipDefinition _definition;
        private float _flashUntil;
        private bool _dead;

        public int ShipId { get; private set; }

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

        public void SetPose(Vector3 position, Quaternion rotation)
        {
            if (body != null)
            {
                body.position = position;
                body.rotation = rotation;
            }
            else transform.SetPositionAndRotation(position, rotation);
        }

        public void SetHealth(float health, uint hitRevision)
        {
            if (hitRevision != 0) _flashUntil = Time.unscaledTime + 0.12f;
            var dead = health <= 0f;
            if (dead != _dead)
            {
                _dead = dead;
                foreach (var collider in hullColliders)
                    if (collider != null) collider.enabled = !dead;
                if (dead && _definition != null && _definition.DeathEffectPrefab != null)
                    Object.Instantiate(_definition.DeathEffectPrefab, transform.position, Quaternion.identity);
            }
        }

        private void LateUpdate()
        {
            _properties ??= new MaterialPropertyBlock();
            var flash = Time.unscaledTime < _flashUntil ? 1f : 0f;
            foreach (var renderer in damageRenderers)
            {
                if (renderer == null) continue;
                renderer.GetPropertyBlock(_properties);
                if (renderer.sharedMaterial != null && renderer.sharedMaterial.HasProperty("_EnemyDamageFlash"))
                    _properties.SetFloat("_EnemyDamageFlash", flash);
                renderer.SetPropertyBlock(_properties);
            }
        }

        public IReadOnlyList<Vector3> GetCrewLocalPositions(int requestedCount)
        {
            var positions = new List<Vector3>(Mathf.Max(0, requestedCount));
            if (crewSlots != null)
                for (var i = 0; i < requestedCount && i < crewSlots.Length; i++)
                    if (crewSlots[i] != null) positions.Add(transform.InverseTransformPoint(crewSlots[i].transform.position));
            var half = _definition != null ? _definition.CollisionHalfExtents : new Vector3(3, 1.5f, 7);
            var center = _definition != null ? _definition.CollisionCenter : new Vector3(0, 1, 0);
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
                positions.Add(new Vector3(x, center.y + half.y + 0.04f, z));
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
            if (_definition == null || _definition.ProjectilePrefab == null || _shots.ContainsKey(revision)) return;
            if (_shots.Count > 16)
            {
                var expired = new List<uint>();
                foreach (var pair in _shots) if (pair.Value == null) expired.Add(pair.Key);
                foreach (var key in expired) _shots.Remove(key);
            }
            var projectile = Object.Instantiate(_definition.ProjectilePrefab, origin, Quaternion.identity);
            if (projectile.TryGetComponent<CannonBallVisual>(out var playerVisual)) playerVisual.enabled = false;
            var visual = projectile.AddComponent<EnemyShipProjectileVisual>();
            visual.Initialize(origin, velocity, Vector3.down * _definition.ProjectileGravity, started,
                _definition.ProjectileLifetime, _definition.MuzzleEffectPrefab, _definition.WaterImpactPrefab,
                _definition.ImpactEffectPrefab);
            _shots[revision] = visual;
        }

        public void PlayImpact(uint shotRevision, Vector3 point, Vector3 normal, bool water, bool show, float at)
        {
            if (_shots.TryGetValue(shotRevision, out var visual) && visual != null)
                visual.SetImpact(point, normal, water, show, at);
            _shots.Remove(shotRevision);
        }

        public void ReceiveEquipmentHitServer(float damage, Vector3 attackerPosition, bool canBlock = true) =>
            DotsEnemyShipRuntime.Instance?.Damage(ShipId, damage, attackerPosition);

        private void OnDestroy()
        {
            DotsEnemyShipRuntime.Instance?.UnregisterView(ShipId, this);
            foreach (var shot in _shots.Values) if (shot != null) Destroy(shot.gameObject);
            _shots.Clear();
        }
    }

}
