using System;
using Unity.Netcode;
using UnityEngine;

namespace WaveByWave.Player
{
    public interface IEquipmentDamageReceiver
    {
        // Call on the server; attackers must supply their position for directional blocking.
        void ReceiveEquipmentHitServer(float damage, Vector3 attackerPosition, bool canBlock = true);
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

    public enum HookPhase : byte { Stowed, Flying, Landed, Reeling }
    public struct EquipmentHookState : INetworkSerializable, IEquatable<EquipmentHookState>
    {
        public HookPhase Phase;
        public Vector3 Origin, Velocity;
        public double Started;
        public bool HasSupport;
        public NetworkObjectReference Support;
        public Vector3 Evaluate(double now, float gravity)
        {
            var t = Mathf.Max(0f, (float)(now - Started));
            return Phase == HookPhase.Flying ? Origin + Velocity * t + Vector3.down * (0.5f * gravity * t * t) :
                Phase == HookPhase.Reeling ? Origin + Velocity * Mathf.Min(t, 0.1f) : Origin;
        }
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Phase); s.SerializeValue(ref Origin); s.SerializeValue(ref Velocity);
            s.SerializeValue(ref Started); s.SerializeValue(ref HasSupport); s.SerializeValue(ref Support);
        }
        public bool Equals(EquipmentHookState o) => Phase == o.Phase && Origin.Equals(o.Origin) &&
            Velocity.Equals(o.Velocity) && Started.Equals(o.Started) && HasSupport == o.HasSupport && Support.Equals(o.Support);
    }
}
