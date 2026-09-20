using UnityEngine;

namespace WaveByWave.Combat
{
    public sealed class DeathDustBurst : MonoBehaviour
    {
        [SerializeField] private ParticleSystem particles;
        [SerializeField, Min(0.1f)] private float destroyAfter = 1.5f;

        public void Play(float scale)
        {
            if (particles == null) particles = GetComponentInChildren<ParticleSystem>(true);
            if (particles == null)
            {
                Debug.LogError("DeathDustBurst prefab has no ParticleSystem.", this);
                Destroy(gameObject);
                return;
            }
            transform.localScale = Vector3.one * Mathf.Max(0.01f, scale);
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            particles.Emit(42);
            particles.Play();
            Destroy(gameObject, destroyAfter);
        }
    }
}
