using System;
using Unity.Collections;
using Unity.Netcode;

namespace WaveByWave.Player
{
    public struct InventorySlotState : INetworkSerializable, IEquatable<InventorySlotState>
    {
        public FixedString64Bytes ItemId;
        public ushort Amount;

        public InventorySlotState(string itemId, ushort amount = 1)
        {
            ItemId = itemId;
            Amount = amount;
        }

        public bool IsEmpty => ItemId.IsEmpty || Amount == 0;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ItemId);
            serializer.SerializeValue(ref Amount);
        }

        public bool Equals(InventorySlotState other) => ItemId.Equals(other.ItemId) && Amount == other.Amount;
        public override bool Equals(object obj) => obj is InventorySlotState other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(ItemId, Amount);
    }
}
