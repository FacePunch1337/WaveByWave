using UnityEngine;

namespace WaveByWave.Effects
{
    /// <summary>Lifecycle only. The complete look is authored in the effect prefab.</summary>
    public sealed class OneShotEffect : MonoBehaviour
    {
        [SerializeField, Min(0.05f)] private float destroyAfter = 2f;

        private void OnEnable()
        {
            foreach (var particles in GetComponentsInChildren<ParticleSystem>(true)) particles.Play(true);
            Destroy(gameObject, destroyAfter);
        }

        public static GameObject Spawn(GameObject prefab, Vector3 position, Vector3 direction)
        {
            if (prefab == null) return null;
            var rotation = direction.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(direction) : Quaternion.identity;
            return Instantiate(prefab, position, rotation);
        }
    }
}
