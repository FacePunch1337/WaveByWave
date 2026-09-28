using UnityEngine;

namespace WaveByWave.Items
{
    [CreateAssetMenu(menuName = "Wave by Wave/Items/Throwable Weapon", fileName = "Throwable_")]
    public sealed class ThrowableWeaponProfile : ScriptableObject
    {
        [Min(.05f)] public float ChargeSeconds = 1.5f;
        [Min(.1f)] public float MinimumSpeed = 8f, MaximumSpeed = 22f;
        [Min(0f)] public float Gravity = 12f;
        [Min(.05f)] public float ProjectileRadius = .14f;
        [Min(.1f)] public float Lifetime = 15f;
        [Min(.05f)] public float ThrowCooldown = .65f;
        [Range(0f, 30f)] public float ExtraProjectileSpread = 5f;
        public bool LoadInCannons = true;
        [Header("Explosion")]
        [Min(.1f)] public float BlastRadius = 4f;
        [Min(0f)] public float CraterRadius = 2.5f;
        [Range(0f, .6f)] public float CraterNoise = .25f;
        [Tooltip("Looping particles, in prefab-local coordinates. Used in the hand and during flight.")]
        public GameObject FusePrefab;
        public Vector3 FuseOffset = new(0, .14f, 0);
        public GameObject ExplosionPrefab;
    }
}
