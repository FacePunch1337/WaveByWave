using System;
using UnityEngine;
using WaveByWave.Items;

namespace WaveByWave.Player
{
    public enum PlayerRingStat : byte
    {
        MaximumHealth,
        MeleeDamage,
        RangedDamage,
        MoveSpeed,
        Stamina,
        Regeneration,
        JumpHeight,
        HookRetrievalSpeed,
        Luck
    }

    [Serializable]
    public struct PlayerRingDefinition
    {
        public PlayerRingStat Stat;
        public string DisplayName;
        [Min(0.001f)] public float BaseBonus;
    }

    [CreateAssetMenu(menuName = "Wave By Wave/Player/Ring progression", fileName = "PlayerRingCatalog")]
    public sealed class PlayerRingCatalog : ScriptableObject
    {
        [Min(1)] public int MaximumDistinctRings = 4;
        [Min(1)] public int FirstLevelCost = 100;
        [Min(1.01f)] public float LevelCostGrowth = 1.4f;
        public PlayerRingDefinition[] Rings = Array.Empty<PlayerRingDefinition>();
        public float[] RarityMultipliers = { 1f, 1.35f, 1.8f, 2.5f, 3.5f };
        public float[] RarityWeights = { 55f, 26f, 12f, 5f, 2f };

        public int ExperienceForLevel(int level)
        {
            var total = 0d;
            for (var i = 1; i < Mathf.Max(1, level); i++)
                total += FirstLevelCost * Math.Pow(LevelCostGrowth, i - 1);
            return (int)Math.Min(int.MaxValue, Math.Ceiling(total));
        }

        public PlayerRingDefinition Find(PlayerRingStat stat)
        {
            foreach (var ring in Rings) if (ring.Stat == stat) return ring;
            return default;
        }

        public float Bonus(PlayerRingStat stat, ItemRarity rarity)
        {
            var index = Mathf.Clamp((int)rarity, 0, RarityMultipliers.Length - 1);
            return Find(stat).BaseBonus * Mathf.Max(0f, RarityMultipliers[index]);
        }

        public ItemRarity RollRarity(ref Unity.Mathematics.Random random)
        {
            var sum = 0f;
            foreach (var weight in RarityWeights) sum += Mathf.Max(0f, weight);
            if (sum <= 0f) return ItemRarity.Common;
            var roll = random.NextFloat(sum);
            for (var i = 0; i < Mathf.Min(5, RarityWeights.Length); i++)
            {
                roll -= Mathf.Max(0f, RarityWeights[i]);
                if (roll <= 0f) return (ItemRarity)i;
            }
            return ItemRarity.Common;
        }
    }
}
