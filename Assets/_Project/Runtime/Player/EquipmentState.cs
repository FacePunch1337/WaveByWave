using System;
using Unity.Netcode;
using UnityEngine;

namespace WaveByWave.Player
{
    public interface IEquipmentDamageReceiver
    {
        // Call on the server; attackers must supply their position for directional blocking.
        void ReceiveEquipmentHitServer(float damage, Vector3 attackerPosition, bool canBlock = true, Vector3? impactPoint = null);
    }

    public static class EquipmentDamageReceiverUtility
    {
        public static bool TryGet(Collider collider, out IEquipmentDamageReceiver receiver,
            out MonoBehaviour behaviour)
        {
            receiver = null;
            behaviour = null;
            if (collider == null)
                return false;

            var components = collider.GetComponentsInParent<MonoBehaviour>(true);
            // PlayerEquipment owns directional blocking, so it gets first refusal over the
            // NetworkHealth component living on the same player object.
            foreach (var component in components)
                if (component is PlayerEquipment playerEquipment)
                {
                    receiver = playerEquipment;
                    behaviour = playerEquipment;
                    return true;
                }
            foreach (var component in components)
                if (component is IEquipmentDamageReceiver candidate)
                {
                    receiver = candidate;
                    behaviour = component;
                    return true;
                }
            return false;
        }
    }

    public struct EquipmentMotionState : INetworkSerializable, IEquatable<EquipmentMotionState>
    {
        public EquipmentAction Action;
        public uint Sequence;
        public double Started;
        public float Duration;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        { s.SerializeValue(ref Action); s.SerializeValue(ref Sequence); s.SerializeValue(ref Started); s.SerializeValue(ref Duration); }
        public bool Equals(EquipmentMotionState o) => Action == o.Action && Sequence == o.Sequence &&
            Started.Equals(o.Started) && Duration.Equals(o.Duration);
    }

    public enum HookPhase : byte { Stowed, Flying, Landed, Reeling, Returning }
    public struct EquipmentHookState : INetworkSerializable, IEquatable<EquipmentHookState>
    {
        public HookPhase Phase;
        public Vector3 Origin, Velocity;
        public double Started;
        // The visible tip follows the original throw arc, even if pulling changes its travel velocity.
        public Vector3 FlightFacingVelocity;
        public double FlightFacingStarted;
        public bool HasSupport;
        public bool Pulling;
        public NetworkObjectReference Support;
        public Vector3 Evaluate(double now, float gravity)
        {
            var t = Mathf.Max(0f, (float)(now - Started));
            return Phase == HookPhase.Flying ? Origin + Velocity * t + Vector3.down * (0.5f * gravity * t * t) :
                Phase is HookPhase.Reeling or HookPhase.Returning
                    ? Origin + Velocity * Mathf.Min(t, 0.1f) : Origin;
        }
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Phase); s.SerializeValue(ref Origin); s.SerializeValue(ref Velocity);
            s.SerializeValue(ref Started); s.SerializeValue(ref FlightFacingVelocity);
            s.SerializeValue(ref FlightFacingStarted); s.SerializeValue(ref HasSupport); s.SerializeValue(ref Pulling);
            s.SerializeValue(ref Support);
        }
        public bool Equals(EquipmentHookState o) => Phase == o.Phase && Origin.Equals(o.Origin) &&
            Velocity.Equals(o.Velocity) && Started.Equals(o.Started) &&
            FlightFacingVelocity.Equals(o.FlightFacingVelocity) && FlightFacingStarted.Equals(o.FlightFacingStarted) &&
            HasSupport == o.HasSupport &&
            Pulling == o.Pulling && Support.Equals(o.Support);
    }
}
