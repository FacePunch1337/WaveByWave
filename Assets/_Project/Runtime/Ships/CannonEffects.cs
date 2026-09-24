using UnityEngine;
using WaveByWave.Combat;
using WaveByWave.Effects;

namespace WaveByWave.Ships
{
    public static class CannonEffects
    {
        public static void Shot(ShipCannonBattery battery, ShipCannon cannon, int id, Vector3 origin, Vector3 velocity, Vector3 gravity,
            double started, float lifetime, GameObject projectilePrefab, GameObject muzzleEffectPrefab)
        {
            if (!battery.IsClient) return;
            cannon?.PlayRecoil();
            if (projectilePrefab == null) return;
            var motion = battery.GetComponent<WaveByWave.Player.PlatformNetworkTransform>();
            var presentationTime = !battery.IsServer && motion != null
                ? motion.PresentationServerTime : -1d;
            DotsCannonProjectileVisuals.Add(battery.NetworkObjectId, (uint)id, false,
                projectilePrefab, origin, velocity, gravity, started, lifetime,
                muzzleEffectPrefab, presentationTime);
        }

        public static void Impact(ShipCannonBattery battery, int id, Vector3 point, Vector3 normal, bool water,
            bool show, double at, GameObject waterImpactPrefab, GameObject groundImpactPrefab)
        {
            if (!battery.IsClient) return;
            DotsCannonProjectileVisuals.Impact(battery.NetworkObjectId, (uint)id, false,
                point, normal, water, show, at, waterImpactPrefab, groundImpactPrefab);
        }

        public static void Forget(ShipCannonBattery battery, int id) { }
        public static void ClearShots(ShipCannonBattery battery) =>
            DotsCannonProjectileVisuals.ClearOwner(battery.NetworkObjectId, false);

        public static void Muzzle(Vector3 point, Vector3 forward, GameObject prefab) =>
            OneShotEffect.Spawn(prefab, point, forward);

        public static void Hit(Vector3 point, Vector3 normal, bool water, GameObject waterPrefab, GameObject groundPrefab)
        {
            var prefab = water ? waterPrefab : groundPrefab;
            OneShotEffect.Spawn(prefab, point + (water ? Vector3.zero : normal * 0.08f), water ? Vector3.up : normal);
        }
    }

}
