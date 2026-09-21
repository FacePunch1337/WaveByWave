using UnityEngine;
using StylizedWater3;

namespace WaveByWave.Enemies
{
    [CreateAssetMenu(menuName = "Wave By Wave/Enemies/DOTS enemy ship", fileName = "EnemyShipDefinition")]
    public sealed class EnemyShipDefinition : ScriptableObject
    {
        [Header("Presentation")]
        [Tooltip("Hybrid view instantiated for each replicated DOTS ship. It must contain EnemyShipView and a solid hull collider.")]
        public GameObject ViewPrefab;
        public GameObject ProjectilePrefab;
        public GameObject MuzzleEffectPrefab;
        public GameObject ImpactEffectPrefab;
        public GameObject WaterImpactPrefab;
        public GameObject DeathEffectPrefab;
        public WaveProfile WaterProfile;

        [Header("Hull")]
        [Min(1f)] public float MaximumHealth = 500f;
        [Tooltip("Center of the collision box in ship-local coordinates.")]
        public Vector3 CollisionCenter = new(0f, 1.2f, 0f);
        [Tooltip("Half-size of the collision box used by the server movement query.")]
        public Vector3 CollisionHalfExtents = new(3.2f, 1.5f, 7.5f);
        [Min(0f)] public float CollisionSkin = 0.15f;
        public LayerMask CollisionLayers = ~0;

        [Header("Sailing")]
        [Min(0.1f)] public float BaseSpeed = 2.4f;
        [Min(0.1f)] public float MaximumSpeed = 5.8f;
        [Min(0f)] public float Acceleration = 1.4f;
        [Min(0f)] public float Deceleration = 0.8f;
        [Min(0f)] public float WindSpeedBonus = 2.4f;
        [Min(0f)] public float WindDriftSpeed = 0.3f;
        [Min(1f)] public float TurnSpeed = 16f;
        [Min(0.1f)] public float WaterHeightResponse = 3.5f;
        [Min(0.1f)] public float WaterTiltResponse = 2.8f;
        [Tooltip("Local waterline offset above the sampled surface.")]
        public float WaterlineOffset;
        [Tooltip("Width and length used to sample the wave normal.")]
        public Vector2 WaterSampleSize = new(6f, 15f);

        [Header("Broadside tactics")]
        [Min(2f)] public float PreferredBroadsideRange = 32f;
        [Min(0.5f)] public float RangeCorrectionBand = 12f;
        [Range(0f, 3f)] public float OrbitWeight = 1f;
        [Range(0f, 3f)] public float RangeCorrectionWeight = 1.25f;
        [Range(1f, 60f)] public float BroadsideFireAngle = 18f;
        [Min(2f)] public float FireRange = 52f;
        [Min(0.1f)] public float FireCooldown = 5.5f;
        [Min(0f)] public float FireCooldownJitter = 1.5f;

        [Header("Cannon balls")]
        [Min(1f)] public float ProjectileSpeed = 44f;
        [Min(0f)] public float ProjectileGravity = 9.81f;
        [Min(0.01f)] public float ProjectileRadius = 0.16f;
        [Min(0.1f)] public float ProjectileLifetime = 12f;
        [Min(0f)] public float ProjectileDamage = 34f;
        [Min(0f)] public float CannonHeight = 1.8f;
        [Min(0f)] public float CannonSideOffset = 3.2f;

        [Header("Fleet avoidance")]
        [Min(1f)] public float AvoidanceRadius = 15f;
        [Range(0f, 4f)] public float AvoidanceStrength = 1.6f;
        [Range(1, 2000)] public int MaximumShips = 1000;
        [Range(1, 32)] public int SpawnsPerFrame = 2;
        [Min(10f)] public float TargetSearchRadius = 350f;

        [Header("Fleet performance")]
        [Range(5, 60)] public int SimulationRate = 20;
        [Range(1, 20)] public int DistantSimulationRate = 5;
        [Min(30f)] public float DetailedSimulationDistance = 100f;
        [Min(30f)] public float PhysicsViewDistance = 100f;
        [Range(1, 32)] public int ViewCreationsPerFrame = 2;
        [Tooltip("Render distant hulls in instanced batches using the authored prefab meshes.")]
        public bool InstanceDistantShips = true;

        [Header("Skeleton crew")]
        [Range(0, 64)] public int CrewCount = 8;
        public EnemyCombatType CrewCombatType = EnemyCombatType.Random;

        [Header("Sinking")]
        [Min(0.1f)] public float SinkDuration = 8f;
        [Min(0f)] public float SinkSpeed = 0.9f;

        private void OnValidate()
        {
            CollisionHalfExtents = Vector3.Max(CollisionHalfExtents, Vector3.one * 0.1f);
            MaximumSpeed = Mathf.Max(BaseSpeed, MaximumSpeed);
            FireRange = Mathf.Max(PreferredBroadsideRange, FireRange);
            WaterSampleSize = Vector2.Max(WaterSampleSize, Vector2.one * 0.1f);
        }
    }
}
