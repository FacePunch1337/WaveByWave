using System;
using Unity.Netcode;
using UnityEngine;

namespace WaveByWave.Items
{
    public struct WorldItemTetherState : INetworkSerializable, IEquatable<WorldItemTetherState>
    {
        public bool Active;
        public NetworkObjectReference Player;
        public Vector3 Offset;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        { s.SerializeValue(ref Active); s.SerializeValue(ref Player); s.SerializeValue(ref Offset); }
        public bool Equals(WorldItemTetherState o) => Active == o.Active && Player.Equals(o.Player) && Offset.Equals(o.Offset);
    }
}
