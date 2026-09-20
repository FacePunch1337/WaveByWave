using System;
using Unity.Netcode;

namespace WaveByWave.Customization
{
    public enum PirateCustomizationCategory : byte
    {
        Pirate,
        Hair,
        Bandana,
        Hat,
        Coat
    }

    [Serializable]
    public struct PirateAppearanceState : INetworkSerializable, IEquatable<PirateAppearanceState>
    {
        public byte Pirate;
        public byte Hair;
        public byte Bandana;
        public byte Hat;
        public byte Coat;

        public byte Get(PirateCustomizationCategory category) => category switch
        {
            PirateCustomizationCategory.Pirate => Pirate,
            PirateCustomizationCategory.Hair => Hair,
            PirateCustomizationCategory.Bandana => Bandana,
            PirateCustomizationCategory.Hat => Hat,
            PirateCustomizationCategory.Coat => Coat,
            _ => 0
        };

        public void Set(PirateCustomizationCategory category, byte value)
        {
            switch (category)
            {
                case PirateCustomizationCategory.Pirate: Pirate = value; break;
                case PirateCustomizationCategory.Hair: Hair = value; break;
                case PirateCustomizationCategory.Bandana: Bandana = value; break;
                case PirateCustomizationCategory.Hat: Hat = value; break;
                case PirateCustomizationCategory.Coat: Coat = value; break;
            }
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Pirate);
            serializer.SerializeValue(ref Hair);
            serializer.SerializeValue(ref Bandana);
            serializer.SerializeValue(ref Hat);
            serializer.SerializeValue(ref Coat);
        }

        public bool Equals(PirateAppearanceState other) => Pirate == other.Pirate && Hair == other.Hair &&
            Bandana == other.Bandana && Hat == other.Hat && Coat == other.Coat;
    }
}
