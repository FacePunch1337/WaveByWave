using Unity.Netcode;
using Unity.NetCode;
using UnityEngine;
using WaveByWave.Combat;
using WaveByWave.Items;

namespace WaveByWave.Player
{
    public sealed partial class PlayerEquipment
    {
        public float ThrowableCharge => _localItem?.Throwable != null && IsOwner
            ? Mathf.Clamp01((Time.unscaledTime - _localChargeStarted) / _localItem.Throwable.ChargeSeconds) : 0f;

        private void ThrowBombServer(ItemDefinition item, int slot, Vector3 origin, Vector3 direction)
        {
            var profile = item.Throwable;
            if (profile == null || ClientServerBootstrap.ServerWorld is not { IsCreated: true }) return;
            var charge = _chargeStarted < 0 ? 0f : Mathf.Clamp01((float)(Now - _chargeStarted) / Mathf.Max(.05f, profile.ChargeSeconds));
            var speed = Mathf.Lerp(profile.MinimumSpeed, Mathf.Max(profile.MinimumSpeed, profile.MaximumSpeed), charge);
            var carrier = _player.GetSupportingShipOnServer();
            var inherited = carrier != null && carrier.TryGetComponent<MovingPlatform>(out var platform)
                ? platform.GetPointVelocity(origin) : Vector3.zero;
            if (!_inventory.TryConsumeServer(slot, 1, out _)) return;
            var count = _player.ProjectileCount;
            var launchDirection = (direction + Vector3.up * .35f).normalized;
            for (var i = 0; i < count; i++)
            {
                var angle = count > 1 ? Mathf.Lerp(-profile.ExtraProjectileSpread, profile.ExtraProjectileSpread, i / (float)(count - 1)) : 0f;
                var velocity = Quaternion.AngleAxis(angle, Vector3.up) * launchDirection * speed + inherited;
                var id = ++_nextBullet;
                var projectile = new DotsCannonProjectile
                {
                    Position = origin, Previous = origin, Origin = origin, Velocity = velocity,
                    Gravity = Vector3.down * profile.Gravity, Lifetime = profile.Lifetime,
                    Radius = profile.ProjectileRadius, Damage = _player.WeaponDamage(item),
                    ShooterClientId = OwnerClientId, SourceNetworkObjectId = NetworkObjectId,
                    ShotId = id, Started = (float)Now, HandThrown = 1,
                    BlastRadius = profile.BlastRadius, CraterRadius = profile.CraterRadius, CraterNoise = profile.CraterNoise
                };
                DotsCannonProjectileSystem.Spawn(projectile);
                BombShotClientRpc(id, item.Id, origin, velocity, Now);
            }
            _chargeStarted = -1d; _charging.Value = false;
            PlayServer(EquipmentAction.ThrowableThrow, profile.ThrowCooldown / _player.AttackSpeedMultiplier);
        }

        [ClientRpc]
        private void BombShotClientRpc(int id, string itemId, Vector3 origin, Vector3 velocity, double started)
        {
            if (!_inventory.Catalog.TryGet(itemId, out var item) || item.Throwable == null) return;
            var profile = item.Throwable;
            DotsCannonProjectileVisuals.Add(NetworkObjectId, (uint)id, false, item.WorldVisualPrefab,
                origin, velocity, Vector3.down * profile.Gravity, started, profile.Lifetime, null,
                fusePrefab: profile.FusePrefab, fuseOffset: profile.FuseOffset,
                explosionPrefab: profile.ExplosionPrefab);
        }

        internal void ReportBombImpact(int id, Vector3 point, Vector3 normal, bool show, double at) =>
            BombImpactClientRpc(id, point, normal, show, at);

        [ClientRpc]
        private void BombImpactClientRpc(int id, Vector3 point, Vector3 normal, bool show, double at) =>
            DotsCannonProjectileVisuals.Impact(NetworkObjectId, (uint)id, false, point, normal, false, show, at, null, null);
    }
}
