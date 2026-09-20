using System.Collections.Generic;
using UnityEngine;
using WaveByWave.Effects;

namespace WaveByWave.Ships
{
    public static class CannonEffects
    {
        private static readonly Dictionary<(ShipCannonBattery, int), CannonBallVisual> Shots = new();

        public static void Shot(ShipCannonBattery battery, ShipCannon cannon, int id, Vector3 origin, Vector3 velocity, Vector3 gravity,
            double started, float lifetime, GameObject projectilePrefab, GameObject muzzleEffectPrefab)
        {
            if (!battery.IsClient || projectilePrefab == null) return;
            var ball = Object.Instantiate(projectilePrefab, origin, Quaternion.identity);
            ball.transform.position = origin;
            if (!ball.TryGetComponent<CannonBallVisual>(out var visual))
            { Debug.LogError("Cannon projectile prefab requires CannonBallVisual.", projectilePrefab); Object.Destroy(ball); return; }
            visual.Initialize(battery, cannon, id, origin, velocity, gravity, started, lifetime, muzzleEffectPrefab);
            Shots[(battery, id)] = visual;
        }

        public static void Impact(ShipCannonBattery battery, int id, Vector3 point, Vector3 normal, bool water,
            bool show, double at, GameObject waterImpactPrefab, GameObject groundImpactPrefab)
        {
            if (!battery.IsClient) return;
            if (Shots.TryGetValue((battery, id), out var ball) && ball != null)
                ball.SetImpact(point, normal, water, show, at, waterImpactPrefab, groundImpactPrefab);
        }

        public static void Forget(ShipCannonBattery battery, int id) => Shots.Remove((battery, id));
        public static void ClearShots(ShipCannonBattery battery)
        {
            var remove = new List<CannonBallVisual>();
            foreach (var entry in Shots) if (entry.Key.Item1 == battery && entry.Value != null) remove.Add(entry.Value);
            foreach (var shot in remove) Object.Destroy(shot.gameObject);
        }

        public static void Muzzle(Vector3 point, Vector3 forward, GameObject prefab) =>
            OneShotEffect.Spawn(prefab, point, forward);

        public static void Hit(Vector3 point, Vector3 normal, bool water, GameObject waterPrefab, GameObject groundPrefab)
        {
            var prefab = water ? waterPrefab : groundPrefab;
            OneShotEffect.Spawn(prefab, point + (water ? Vector3.zero : normal * 0.08f), water ? Vector3.up : normal);
        }
    }

}
