using System.Collections.Generic;
using UnityEngine;
using WaveByWave.Enemies;
using WaveByWave.Generation;
using WaveByWave.Player;
using WaveByWave.Ships;

namespace WaveByWave.Combat
{
    public static class ProjectileExplosion
    {
        private static readonly Collider[] Hits = new Collider[128];
        private struct BlastTarget
        {
            public float Damage;
            public Vector3 Point;
            public ShipHullHealth Hull;
            public IEquipmentDamageReceiver Receiver;
        }
        private static readonly Dictionary<MonoBehaviour, BlastTarget> Targets = new();
        public static float DamageAt(float damage, float distance, float radius) =>
            distance > radius ? 0f : damage * Mathf.Lerp(1f, .25f, Mathf.Clamp01(distance / Mathf.Max(.01f, radius)));

        internal static void Detonate(in DotsCannonProjectile bomb, Vector3 center)
        {
            DamagePhysics(center, bomb.BlastRadius, bomb.Damage);
            DotsEnemyRuntime.Instance?.Explosion(center, bomb.BlastRadius, bomb.Damage);
            DotsEnemyShipRuntime.Instance?.Explosion(center, bomb.BlastRadius, bomb.Damage);
            OceanWorldDirector.Instance?.ExplodeIslandServer(center, bomb.CraterRadius, bomb.CraterNoise, unchecked((uint)bomb.ShotId));
        }

        private static void DamagePhysics(Vector3 center, float radius, float baseDamage)
        {
            var count = Physics.OverlapSphereNonAlloc(center, radius, Hits, ~0, QueryTriggerInteraction.Collide);
            var hits = count == Hits.Length ? Physics.OverlapSphere(center, radius, ~0, QueryTriggerInteraction.Collide) : Hits;
            if (hits != Hits) count = hits.Length;
            Targets.Clear();
            for (var i = 0; i < count; i++)
            {
                var collider = hits[i];
                if (collider == null || collider.isTrigger && collider.GetComponent<EquipmentHitbox>() == null) continue;
                // PhysX ClosestPoint does not support non-convex hull meshes.
                var point = collider is MeshCollider mesh && !mesh.convex
                    ? collider.bounds.ClosestPoint(center) : collider.ClosestPoint(center);
                var damage = DamageAt(baseDamage, Vector3.Distance(center, point), radius);
                if (damage <= 0f) continue;
                var hull = collider.GetComponentInParent<ShipHullHealth>();
                MonoBehaviour component = hull;
                IEquipmentDamageReceiver receiver = null;
                if (hull == null && !EquipmentDamageReceiverUtility.TryGet(collider, out receiver, out component)) continue;
                // Collider order must not change damage; one actor receives the strongest hit once.
                if (Targets.TryGetValue(component, out var previous) && previous.Damage >= damage) continue;
                Targets[component] = new BlastTarget { Damage = damage, Point = point, Hull = hull, Receiver = receiver };
            }
            foreach (var target in Targets.Values)
                if (target.Hull != null) target.Hull.ApplyExplosionDamageServer(target.Damage, target.Point);
                else target.Receiver.ReceiveEquipmentHitServer(target.Damage, center, false, target.Point);
            Targets.Clear();
            System.Array.Clear(Hits, 0, Hits.Length);
        }
    }
}
