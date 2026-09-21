using UnityEngine;

namespace WaveByWave.Enemies
{
    [DisallowMultipleComponent]
    public sealed class EnemyShipSpawnPoint : MonoBehaviour
    {
        public EnemySpawnMode Mode = EnemySpawnMode.WhenPlayerEntersRadius;
        [Range(1, 32)] public int Count = 1;
        [Min(1f)] public float SpawnRadius = 35f;
        [Min(1f)] public float ActivationRadius = 180f;
        [Tooltip("Zero creates a different fleet layout each session.")]
        public int Seed;

        private void OnEnable()
        {
            if (Application.isPlaying) DotsEnemyShipRuntime.EnsureInstance()?.Register(this);
        }

        private void OnDisable()
        {
            if (Application.isPlaying) DotsEnemyShipRuntime.Instance?.Unregister(this);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.75f, 0.1f, 0.05f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, SpawnRadius);
            if (Mode != EnemySpawnMode.WhenPlayerEntersRadius) return;
            Gizmos.color = new Color(0.9f, 0.35f, 0.08f, 0.35f);
            Gizmos.DrawWireSphere(transform.position, ActivationRadius);
        }
    }
}
