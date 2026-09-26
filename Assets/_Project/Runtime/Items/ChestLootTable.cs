using System;
using System.Collections.Generic;
using UnityEngine;

namespace WaveByWave.Items
{
    [Serializable]
    public sealed class LootRarityTier
    {
        public ItemRarity Rarity;

        public static int ToMask(IReadOnlyList<LootRarityTier> rarities)
        {
            var mask = 0;
            if (rarities == null) return mask;
            foreach (var tier in rarities)
                if (tier != null && (int)tier.Rarity < 32)
                    mask |= 1 << (int)tier.Rarity;
            return mask;
        }
    }

    [Serializable]
    public class WeightedItemEntry
    {
        public ItemDefinition Item;
        [Min(0f)] public float Weight = 1f;
    }

    [Serializable]
    public sealed class WeightedLootEntry : WeightedItemEntry
    {
        [Min(1)] public int MinimumAmount = 1;
        [Min(1)] public int MaximumAmount = 1;
    }

    [Serializable]
    public sealed class ChestTierLoot
    {
        [Tooltip("Rarity of the chest item that uses this profile.")]
        public ItemRarity Tier;
        [Min(1)] public int MinimumRolls = 2;
        [Min(1)] public int MaximumRolls = 5;
        [Tooltip("Reward rarities allowed from the shared Loot Pool. Select several tiers if needed. Empty means no rewards; lower tiers are not added automatically.")]
        public List<LootRarityTier> AllowedRarities = new();
        [Range(51f, 100f), Tooltip("Minimum percentage of reward rolls reserved for the chest's own rarity, rounded up. Item amounts may increase this share to keep matching items in the majority. Applies only when this rarity is allowed and has eligible rewards.")]
        public float MatchingRarityPercent = 65f;
    }

    public readonly struct ChestReward
    {
        public readonly ItemDefinition Item;
        public readonly int Amount;

        public ChestReward(ItemDefinition item, int amount) { Item = item; Amount = amount; }
    }

    [CreateAssetMenu(menuName = "Wave by Wave/Items/Chest Loot Table")]
    public sealed class ChestLootTable : ScriptableObject
    {
        public const int MaximumRewards = 48;
        [Min(0.3f)] public float HoldDuration = 1.5f;
        [Min(0.1f)] public float ShakeDuration = 0.8f;
        [Min(0.1f)] public float EjectionSpeed = 2.4f;
        public GameObject OpenEffectPrefab;
        [Tooltip("All possible chest rewards, their weights and amounts. Each chest profile filters this pool by Allowed Rarities. Add each item once.")]
        public List<WeightedLootEntry> LootPool = new();
        [Tooltip("One profile per chest rarity. Rolls and allowed reward rarities are independent for each profile.")]
        public List<ChestTierLoot> Tiers = new();

        public ChestTierLoot FindTier(ItemRarity rarity) => Tiers?.Find(tier => tier != null && tier.Tier == rarity);

        public int BuildRewards(ChestTierLoot tier, ref Unity.Mathematics.Random random, float luck,
            ChestReward[] rewards)
        {
            if (tier == null || rewards == null || rewards.Length == 0 || LootPool == null) return 0;
            var allowed = LootRarityTier.ToMask(tier.AllowedRarities);
            if (allowed == 0) return 0;
            luck = float.IsFinite(luck) ? Mathf.Max(0f, luck) : 0f;
            var minimumRolls = Mathf.Clamp(tier.MinimumRolls, 1, 24);
            var rolls = random.NextInt(minimumRolls, Mathf.Clamp(tier.MaximumRolls, minimumRolls, 24) + 1);
            rolls = Mathf.Min(Mathf.Min(MaximumRewards, rewards.Length), rolls +
                Mathf.FloorToInt(Mathf.Min(MaximumRewards, luck * 2f)));
            var matching = (int)tier.Tier < 32 ? allowed & (1 << (int)tier.Tier) : 0;
            var hasMatching = false;
            foreach (var entry in LootPool)
                if (IsEligible(entry, matching, false, true)) { hasMatching = true; break; }
            var percent = float.IsFinite(tier.MatchingRarityPercent) ?
                Mathf.Clamp(tier.MatchingRarityPercent, 51f, 100f) : 65f;
            var matchingRolls = hasMatching ? Mathf.Max(rolls / 2 + 1,
                Mathf.CeilToInt(rolls * percent / 100f)) : 0;
            var count = 0;
            var matchingAmount = 0;
            var otherAmount = 0;
            for (var roll = 0; roll < rolls; roll++)
            {
                var mask = !hasMatching ? allowed : roll < matchingRolls ? matching : allowed & ~matching;
                var entry = ChooseEntry(LootPool, ref random, luck, mask, excludeChests: true);
                if (entry == null && hasMatching)
                    entry = ChooseEntry(LootPool, ref random, luck, matching, excludeChests: true);
                if (entry == null) break;
                var amount = RollAmount(entry, ref random, luck);
                rewards[count++] = new ChestReward(entry.Item, amount);
                if (entry.Item.Rarity == tier.Tier) matchingAmount += amount;
                else otherAmount += amount;
            }

            // Large stacks of lower-tier supplies must not outweigh the chest's own tier.
            // Replace whole rolls, preserving the replacement entry's configured amount range.
            for (var i = 0; hasMatching && matchingAmount <= otherAmount && i < count; i++)
            {
                if (rewards[i].Item.Rarity == tier.Tier) continue;
                var entry = ChooseEntry(LootPool, ref random, luck, matching, excludeChests: true);
                otherAmount -= rewards[i].Amount;
                var amount = RollAmount(entry, ref random, luck);
                matchingAmount += amount;
                rewards[i] = new ChestReward(entry.Item, amount);
            }

            // Matching rewards precede other tiers, so the total item cap cannot reverse the majority.
            var emitted = 0;
            for (var i = 0; i < count; i++)
            {
                var amount = Mathf.Min(rewards[i].Amount, MaximumRewards - emitted);
                rewards[i] = new ChestReward(rewards[i].Item, amount);
                emitted += amount;
                if (emitted == MaximumRewards) return i + 1;
            }
            return count;
        }

        private static int RollAmount(WeightedLootEntry entry, ref Unity.Mathematics.Random random, float luck)
        {
            var minimum = Mathf.Clamp(entry.MinimumAmount, 1, 16);
            var amount = random.NextInt(minimum, Mathf.Clamp(entry.MaximumAmount, minimum, 16) + 1);
            return entry.Item.IsCoinReward ?
                Mathf.CeilToInt(Mathf.Min(MaximumRewards, amount * (1f + luck))) : amount;
        }

        public static ItemDefinition Choose(IReadOnlyList<WeightedItemEntry> entries,
            ref Unity.Mathematics.Random random, float luck = 0f, int rarityMask = -1, bool chestsOnly = false)
            => ChooseEntry(entries, ref random, luck, rarityMask, chestsOnly)?.Item;

        public static T ChooseEntry<T>(IReadOnlyList<T> entries, ref Unity.Mathematics.Random random,
            float luck = 0f, int rarityMask = -1, bool chestsOnly = false, bool excludeChests = false) where T : WeightedItemEntry
        {
            var sum = 0f;
            if (entries == null) return null;
            foreach (var entry in entries)
                if (IsEligible(entry, rarityMask, chestsOnly, excludeChests))
                    sum += entry.Weight *
                        (1f + Mathf.Max(0f, luck) * (int)entry.Item.Rarity * 0.5f);
            if (sum <= 0f) return null;
            var choice = random.NextFloat(0f, sum);
            foreach (var entry in entries)
            {
                if (!IsEligible(entry, rarityMask, chestsOnly, excludeChests)) continue;
                choice -= entry.Weight * (1f + Mathf.Max(0f, luck) *
                    (int)entry.Item.Rarity * 0.5f);
                if (choice <= 0f) return entry;
            }
            return null;
        }

        private static bool IsEligible(WeightedItemEntry entry, int rarityMask, bool chestsOnly, bool excludeChests)
            => entry != null && entry.Item != null && float.IsFinite(entry.Weight) && entry.Weight > 0f &&
               (int)entry.Item.Rarity < 32 && (rarityMask & (1 << (int)entry.Item.Rarity)) != 0 &&
               (!chestsOnly || entry.Item.IsChest) && (!excludeChests || !entry.Item.IsChest);
    }
}
