using System;
using System.Collections.Generic;
using UnityEngine;

namespace WaveByWave.Items
{
    [Serializable]
    public sealed class WeightedLootEntry
    {
        public ItemDefinition Item;
        [Min(0f)] public float Weight = 1f;
        [Min(1)] public int MinimumAmount = 1;
        [Min(1)] public int MaximumAmount = 1;
    }

    [Serializable]
    public sealed class ChestTierLoot
    {
        public ItemRarity Tier;
        [Min(1)] public int MinimumRolls = 2;
        [Min(1)] public int MaximumRolls = 5;
        public List<WeightedLootEntry> Items = new();
    }

    [CreateAssetMenu(menuName = "Wave by Wave/Items/Chest Loot Table")]
    public sealed class ChestLootTable : ScriptableObject
    {
        [Min(0.3f)] public float HoldDuration = 1.5f;
        [Min(0.1f)] public float ShakeDuration = 0.8f;
        [Min(0.1f)] public float EjectionSpeed = 2.4f;
        public GameObject OpenEffectPrefab;
        public List<ChestTierLoot> Tiers = new();

        public ChestTierLoot FindTier(ItemRarity rarity) => Tiers.Find(tier => tier != null && tier.Tier == rarity);

        public static ItemDefinition Choose(IReadOnlyList<WeightedLootEntry> entries,
            ref Unity.Mathematics.Random random, float luck = 0f)
        {
            var sum = 0f;
            if (entries == null) return null;
            foreach (var entry in entries)
                if (entry != null && entry.Item != null)
                    sum += Mathf.Max(0f, entry.Weight) *
                        (1f + Mathf.Max(0f, luck) * (int)entry.Item.Rarity * 0.5f);
            if (sum <= 0f) return null;
            var choice = random.NextFloat(0f, sum);
            foreach (var entry in entries)
            {
                if (entry == null || entry.Item == null || entry.Weight <= 0f) continue;
                choice -= entry.Weight * (1f + Mathf.Max(0f, luck) *
                    (int)entry.Item.Rarity * 0.5f);
                if (choice <= 0f) return entry.Item;
            }
            return null;
        }
    }
}
