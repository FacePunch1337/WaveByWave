using System;
using System.Collections.Generic;
using StylizedWater3;
using UnityEngine;
using UnityEngine.Serialization;
using WaveByWave.Enemies;
using WaveByWave.Items;

namespace WaveByWave.Generation
{
    public enum IslandSize : byte { Small, Medium, Large }

    [Serializable]
    public sealed class IslandEnemyDayEntry
    {
        public EnemyKind EnemyType;
    }

    [Serializable]
    public sealed class IslandEnemyDayRule
    {
        [Min(1), Tooltip("Applies from this voyage day until the next rule. Day numbers match the voyage HUD.")]
        public int FromDay = 1;
        [Tooltip("On: choose one entry from Enemies per spawn point. Off: spawn a group of every listed enemy. An empty list means no enemies.")]
        public bool Random = true;
        [Tooltip("Enemy species allowed on this day. Counts, radii and combat settings come from the matching Enemy Spawn Point Prefab.")]
        public List<IslandEnemyDayEntry> Enemies = new();
    }

    [Serializable]
    public sealed class LootRarityUnlock
    {
        [Min(1), Tooltip("All listed rarities become available on this voyage day and stay available on every later day.")]
        public int FromDay = 1;
        [Tooltip("Add every rarity unlocked on this day. Earlier unlocks remain available without repeating them here.")]
        public List<LootRarityTier> Rarities = new();
    }

    [Serializable]
    public sealed class IslandDecoration
    {
        public GameObject Prefab;
        [Min(0f)] public float InstancesPerSquareMetre = 0.025f;
        public Vector2 ScaleRange = new(0.8f, 1.2f);
        [Range(0f, 60f)] public float MaximumSlope = 30f;
        [Min(0f)] public float MinimumHeightAboveWater = 0.25f;
        [Min(0.1f)] public float Spacing = 1.2f;
        public bool AlignToSurface;
        [Tooltip("Place the bottom of the visible mesh on the island instead of the prefab root. Accounts for nested model offsets and scale; useful after replacing a tree mesh. Off preserves deliberately buried rocks.")]
        public bool GroundMeshBase;
    }

    [CreateAssetMenu(menuName = "Wave by Wave/World/Ocean Generation Settings")]
    public sealed class OceanGenerationSettings : ScriptableObject
    {
        [Header("Океан — ограниченный бюджет вокруг кораблей")]
        public bool GenerateOceanLoot = true;
        public bool GenerateIslands = true;
        public int WorldSeed = 481516;
        public Vector2 LootRadius = new(18f, 65f);
        [Min(1)] public int FloatingLootPerShip = 100;
        [Min(0.1f)] public float LootInterval = 0.5f;
        [Min(1)] public int LootPerInterval = 2;
        [Min(1f)] public float LootDespawnRadius = 100f;
        [Min(0.1f)] public float LootFadeDuration = 1.2f;
        [Range(0.05f, 1f), Tooltip("Интервал проверки видимости готовых островов после загрузки. Эта проверка не обязана выполняться каждый кадр.")]
        public float PresentationRefreshInterval = 0.2f;
        [FormerlySerializedAs("FloatingLoot"), Tooltip("All floating items and their relative selection weights. Locked rarities are excluded until their unlock day.")]
        public List<WeightedItemEntry> FloatingObjects = new();
        [Tooltip("Each day entry permanently unlocks every tier in its Rarities list. Unlisted rarities never spawn; an empty schedule disables floating loot.")]
        public List<LootRarityUnlock> FloatingRarityUnlocks = DefaultRarityUnlocks();
        [Header("Цепочка и предзагрузка островов")]
        [FormerlySerializedAs("IslandRadius")]
        [Tooltip("Минимальная и максимальная дистанция от корабля до первого острова впереди. Первые острова полностью строятся под загрузочной шторкой.")]
        public Vector2 InitialIslandRadius = new(65f, 130f);
        [Range(0, 32), Tooltip("Точное общее количество островов, которое должно быть полностью создано впереди кораблей до снятия загрузочной шторки.")]
        public int InitialIslandCount = 4;
        [FormerlySerializedAs("StreamingIslandRadius")]
        [Tooltip("Минимальная и максимальная дистанция между центрами текущего и следующего острова. Следующий остров создаётся впереди по курсу корабля.")]
        public Vector2 IslandChainDistance = new(160f, 230f);
        [Range(0f, 80f), Tooltip("Максимальное случайное отклонение следующего острова влево или вправо от курса корабля.")]
        public float IslandForwardArc = 35f;
        [Min(10f), Tooltip("Полностью готовый остров становится видимым только внутри этой дистанции.")]
        public float IslandRevealRadius = 135f;
        [Min(20f), Tooltip("Уже показанный остров скрывается только дальше этой дистанции.")]
        public float IslandHideRadius = 400f;
        [Min(20f), Tooltip("Удаление острова из мира. Поворот корабля не удаляет близкие острова.")]
        public float IslandRemovalRadius = 650f;
        [Range(4, 128), Tooltip("Бюджет процедурных островов. При заполнении новые ожидают удаления дальних.")]
        public int MaximumResidentIslands = 32;
        [Min(0.1f)] public float IslandStreamingInterval = 0.5f;
        [FormerlySerializedAs("IslandDespawnRadius")]
        [Min(20f), Tooltip("Максимальная дистанция, на которой уже существующий остров можно повторно включить в цепочку корабля после резкой смены курса или телепортации.")]
        public float IslandRecoveryRadius = 300f;
        [Min(2f), Tooltip("Минимальный свободный промежуток между геометрией соседних островов.")]
        public float IslandSpacing = 20f;
        public GameObject LoadingCurtainPrefab;

        [Header("Геометрия острова, метры")]
        [Min(1f)] public float SmallDiameter = 5f;
        [Min(1f)] public float MediumDiameter = 10f;
        [Min(1f)] public float LargeDiameter = 15f;
        [Range(0.2f, 0.6f)] public float VoxelSize = 0.2f;
        [Range(8, 64)] public int ChunkCells = 32;
        [Min(0.2f)] public float SandHeight = 1.8f;
        [Min(0.5f)] public float SandDepth = 2.4f;
        [Min(0.5f)] public float NoiseScale = 2.8f;
        [Range(0f, 0.45f)] public float ShoreIrregularity = 0.22f;
        public Material GroundMaterial;
        public GameObject IslandPrefab;
        public GameObject ChunkPrefab;
        [Range(1, 32), Tooltip("Максимум попыток размещения декораций острова за кадр, включая неудачные.")]
        public int DecorationAttemptsPerFrame = 4;
        [Range(0.1f, 5f), Tooltip("Бюджет декораций в мс. Следующая попытка переносится на новый кадр после исчерпания бюджета.")]
        public float DecorationBudgetMilliseconds = 1f;
        public List<IslandDecoration> Decorations = new();

        [Header("Island enemies")]
        [Tooltip("Empty keeps the existing prefab pool. Otherwise only the day's listed enemies can spawn; before the first rule, none spawn. Rules are evaluated when players activate a spawn point. Existing enemies are not rerolled.")]
        public List<IslandEnemyDayRule> EnemySpawnDays = new();
        public bool GenerateEnemySpawnPoints = true;
        [Tooltip("Пул префабов с EnemySpawnPoint, например SkeletonSpawn_OnPlayerRadius. Выбирается случайный вариант; активация всегда по входу игрока в радиус.")]
        public List<EnemySpawnPoint> EnemySpawnPointPrefabs = new();
        public Vector2Int EnemySpawnPointCountSmall = new(1, 2);
        public Vector2Int EnemySpawnPointCountMedium = new(2, 4);
        public Vector2Int EnemySpawnPointCountLarge = new(3, 6);
        [Min(0f)] public float EnemySpawnPointSpacing = 3f;
        [Range(0f, 60f)] public float EnemySpawnPointMaximumSlope = 35f;
        [Min(0f)] public float EnemySpawnPointMinimumHeightAboveWater = 0.25f;

        [Header("Копание")]
        [Range(0.3f, 1.5f)] public float DigRadius = 0.65f;
        [Range(0.05f, 0.5f)] public float DigPenetration = 0.22f;
        [Range(0f, 0.5f), Tooltip("Скругляет стыки соседних ударов лопатой и убирает острые CSG-клинья.")]
        public float DigSmoothing = 0.45f;
        [Range(1, 8)] public int MeshUploadsPerFrame = 2;

        [Header("Зарытые сундуки")]
        public Vector2 BurialDepth = new(0.6f, 1.6f);
        public Vector2Int ChestCountSmall = new(1, 2);
        public Vector2Int ChestCountMedium = new(2, 3);
        public Vector2Int ChestCountLarge = new(3, 5);
        [Tooltip("All island chest items, their relative selection weights and copies per selection. Chest Count settings limit the total number of chests on an island.")]
        public List<WeightedLootEntry> BuriedChests = new();
        [Tooltip("Cumulative rarity unlocks for the Buried Chests pool. Unlisted rarities never spawn. Existing chests are not rerolled when a new rarity unlocks.")]
        public List<LootRarityUnlock> BuriedChestRarityUnlocks = DefaultRarityUnlocks();
        public GameObject BuriedMarkerPrefab;

        [Header("Общие ресурсы")]
        public ItemCatalog Catalog;
        public WaveProfile WaterProfile;
        [Tooltip("Префаб-источник материала свечения. Сам префаб в игре не создаётся: луч и искры отрисовываются через DOTS.")]
        public GameObject RarityEffectPrefab;

        public float Diameter(IslandSize size) => size switch
        { IslandSize.Small => SmallDiameter, IslandSize.Medium => MediumDiameter, _ => LargeDiameter };

        public Vector2Int EnemySpawnPointCount(IslandSize size) => size switch
        { IslandSize.Small => EnemySpawnPointCountSmall, IslandSize.Medium => EnemySpawnPointCountMedium, _ => EnemySpawnPointCountLarge };

        public IslandEnemyDayRule EnemyRuleForDay(int day)
        {
            IslandEnemyDayRule result = null;
            if (EnemySpawnDays == null) return null;
            foreach (var rule in EnemySpawnDays)
                if (rule != null && rule.FromDay <= Mathf.Max(1, day) &&
                    (result == null || rule.FromDay > result.FromDay)) result = rule;
            return result;
        }

        public static int UnlockedRarities(IReadOnlyList<LootRarityUnlock> rules, int day)
        {
            var mask = 0;
            if (rules == null) return mask;
            foreach (var rule in rules)
            {
                if (rule == null || rule.FromDay > Mathf.Max(1, day) || rule.Rarities == null) continue;
                mask |= LootRarityTier.ToMask(rule.Rarities);
            }
            return mask;
        }

        private static List<LootRarityUnlock> DefaultRarityUnlocks() => new()
        {
            new() { FromDay = 1, Rarities = new() { new() { Rarity = ItemRarity.Common }, new() { Rarity = ItemRarity.Uncommon } } },
            new() { FromDay = 2, Rarities = new() { new() { Rarity = ItemRarity.Rare } } },
            new() { FromDay = 3, Rarities = new() { new() { Rarity = ItemRarity.Epic } } },
            new() { FromDay = 4, Rarities = new() { new() { Rarity = ItemRarity.Legendary } } }
        };
    }
}
