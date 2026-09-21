using UnityEngine;
using WaveByWave.Generation;

namespace WaveByWave.Enemies
{
    [DisallowMultipleComponent]
    public sealed class EnemySpawnPoint : MonoBehaviour
    {
        public EnemySpawnMode Mode;
        public EnemyCombatType CombatType = EnemyCombatType.Random;
        [Range(1, 3000)] public int Count = 6;
        [Min(0.1f)] public float SpawnRadius = 4f;
        [Min(0.1f)] public float ActivationRadius = 20f;
        [Tooltip("Zero chooses a new appearance/layout seed on each activation.")]
        public int Seed;

        // Only generated island points own their population; ordinary scene points keep their existing lifecycle.
        internal ProceduralIsland OwnerIsland { get; set; }
        internal int SpawnGroup { get; set; }
        internal bool IsIslandPoint => !ReferenceEquals(OwnerIsland, null);
        internal bool CanActivate => !IsIslandPoint || OwnerIsland != null && OwnerIsland.ShipCollisionActive;

        private void OnEnable()
        {
            if (Application.isPlaying) DotsEnemyRuntime.EnsureInstance()?.Register(this);
        }
        private void OnDisable()
        {
            if (Application.isPlaying) DotsEnemyRuntime.Instance?.Unregister(this);
        }
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.65f, 0.1f);
            Gizmos.DrawWireSphere(transform.position, SpawnRadius);
            if (Mode != EnemySpawnMode.WhenPlayerEntersRadius) return;
            Gizmos.color = new Color(1f, 0.2f, 0.1f, 0.6f);
            Gizmos.DrawWireSphere(transform.position, ActivationRadius);
        }
    }
}
