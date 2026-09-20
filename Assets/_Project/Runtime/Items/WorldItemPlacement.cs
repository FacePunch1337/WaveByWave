using System;
using Unity.Netcode;
using UnityEngine;

namespace WaveByWave.Items
{
    // One atomic spawn state replaces continuous world-space Transform updates.
    public struct WorldItemPlacement : INetworkSerializable, IEquatable<WorldItemPlacement>
    {
        public bool Initialized;
        public bool HasSupport;
        public bool OnWater;
        public NetworkObjectReference Support;
        public Vector3 Start;
        public Vector3 End;
        public Vector3 ArcUp;
        public Quaternion Rotation;
        public Vector3 FallbackPosition;
        public Quaternion FallbackRotation;
        public double Started;
        public float Duration;
        public float ArcHeight;

        public Vector3 Evaluate(double time)
        {
            var t = Duration > 0f ? Mathf.Clamp01((float)((time - Started) / Duration)) : 1f;
            return Vector3.LerpUnclamped(Start, End, t) + ArcUp * (4f * ArcHeight * t * (1f - t));
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Initialized);
            serializer.SerializeValue(ref HasSupport);
            serializer.SerializeValue(ref OnWater);
            serializer.SerializeValue(ref Support);
            serializer.SerializeValue(ref Start);
            serializer.SerializeValue(ref End);
            serializer.SerializeValue(ref ArcUp);
            serializer.SerializeValue(ref Rotation);
            serializer.SerializeValue(ref FallbackPosition);
            serializer.SerializeValue(ref FallbackRotation);
            serializer.SerializeValue(ref Started);
            serializer.SerializeValue(ref Duration);
            serializer.SerializeValue(ref ArcHeight);
        }

        public bool Equals(WorldItemPlacement other) => Initialized == other.Initialized &&
            HasSupport == other.HasSupport && OnWater == other.OnWater && Support.Equals(other.Support) && Start.Equals(other.Start) &&
            End.Equals(other.End) && ArcUp.Equals(other.ArcUp) && Rotation.Equals(other.Rotation) &&
            FallbackPosition.Equals(other.FallbackPosition) && FallbackRotation.Equals(other.FallbackRotation) &&
            Started.Equals(other.Started) && Duration.Equals(other.Duration) && ArcHeight.Equals(other.ArcHeight);
    }
}
