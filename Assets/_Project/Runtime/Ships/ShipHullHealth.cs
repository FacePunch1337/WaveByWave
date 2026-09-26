using Unity.Netcode;
using UnityEngine;
using WaveByWave.Combat;

namespace WaveByWave.Ships
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkShipController))]
    public sealed class ShipHullHealth : NetworkBehaviour
    {
        [SerializeField, Min(1f)] private float maximumHealth = 400f;
        private readonly NetworkVariable<float> _health = new(400f);
        private readonly NetworkVariable<float> _armorBonus = new();
        private ShipFlooding _flooding;
        private ShipCannonBattery _shipState;

        public float Health => _flooding != null ? maximumHealth * (1f - _flooding.Fill) : _health.Value;
        public float MaximumHealth => maximumHealth;

        private void Awake()
        {
            _flooding = GetComponent<ShipFlooding>();
            _shipState = GetComponent<ShipCannonBattery>();
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer) _health.Value = maximumHealth;
        }

        public void ApplyDamageServer(float amount) => ApplyDamageServer(amount, transform.position);

        public void ApplyDamageServer(float amount, Vector3 hitPoint)
        {
            if (!IsServer || (_shipState != null && _shipState.VoyageEnded) ||
                !float.IsFinite(amount) || amount <= 0f) return;
            amount /= 1f + _armorBonus.Value;
            DotsDamagePopups.ReportServer(hitPoint, amount);
            if (_flooding != null) _flooding.HitServer(amount, hitPoint);
            else _health.Value = Mathf.Max(0f, _health.Value - amount);
        }

        public bool OpenBoundaryBreachServer(float leakMultiplier) =>
            IsServer && (_shipState == null || !_shipState.VoyageEnded) && _flooding != null &&
            _flooding.OpenBoundaryBreachServer(leakMultiplier);

        public void ApplyArmorUpgradeServer(float bonus)
        {
            if (!IsServer || !float.IsFinite(bonus)) return;
            _armorBonus.Value = Mathf.Clamp(_armorBonus.Value + bonus, 0f, 2f);
        }
    }
}
