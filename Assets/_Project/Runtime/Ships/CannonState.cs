using System;
using Unity.Collections;
using Unity.Netcode;

namespace WaveByWave.Ships
{
    public struct CannonState : INetworkSerializable, IEquatable<CannonState>
    {
        public ulong Operator;
        public float Yaw;
        public float Elevation;
        public bool Loaded;
        public double ReloadEnd;
        public float ReloadDuration;
        public FixedString64Bytes AmmoId;
        public float Damage;
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Operator);
            serializer.SerializeValue(ref Yaw);
            serializer.SerializeValue(ref Elevation);
            serializer.SerializeValue(ref Loaded);
            serializer.SerializeValue(ref ReloadEnd);
            serializer.SerializeValue(ref ReloadDuration);
            serializer.SerializeValue(ref AmmoId);
            serializer.SerializeValue(ref Damage);
        }
        public bool Equals(CannonState other) => Operator == other.Operator && Yaw.Equals(other.Yaw) &&
            Elevation.Equals(other.Elevation) && Loaded == other.Loaded && ReloadEnd.Equals(other.ReloadEnd) &&
            AmmoId.Equals(other.AmmoId) && Damage.Equals(other.Damage) && ReloadDuration.Equals(other.ReloadDuration);
    }
}
