using System;
using Unity.Collections;
using Unity.Netcode;

namespace WaveByWave.Commerce
{
    public enum PurchaseOrderStatus : byte
    {
        None,
        LetterOnDesk,
        LetterHeld,
        InTransit,
        Delivered
    }

    [Serializable]
    public struct OrderLineState : INetworkSerializable, IEquatable<OrderLineState>
    {
        public FixedString64Bytes ItemId;
        public ushort Quantity;

        public OrderLineState(string itemId, ushort quantity)
        {
            ItemId = itemId;
            Quantity = quantity;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ItemId);
            serializer.SerializeValue(ref Quantity);
        }

        public bool Equals(OrderLineState other) => ItemId.Equals(other.ItemId) && Quantity == other.Quantity;
    }
}
