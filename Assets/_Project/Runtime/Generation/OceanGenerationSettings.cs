using System;
using System.Collections.Generic;
using StylizedWater3;
using UnityEngine;
using UnityEngine.Serialization;
using WaveByWave.Items;

namespace WaveByWave.Generation
{
    public enum IslandSize : byte { Small, Medium, Large }

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
        public List<WeightedLootEntry> FloatingLoot = new();
        [Header("Предзагрузка островов")]
        [FormerlySerializedAs("IslandRadius")]
        [Tooltip("Кольцо, в котором первые острова строятся под загрузочной шторкой.")]
        public Vector2 InitialIslandRadius = new(65f, 130f);
        [Tooltip("Кольцо упреждающей генерации следующих островов. Минимум должен быть дальше дистанции показа.")]
        public Vector2 StreamingIslandRadius = new(160f, 230f);
        [Min(10f), Tooltip("Полностью готовый остров становится видимым только внутри этой дистанции.")]
        public float IslandRevealRadius = 135f;
        [Min(0.1f)] public float IslandStreamingInterval = 0.5f;
        [Range(1, 16)] public int IslandsPerShip = 5;
        [Min(20f)] public float IslandDespawnRadius = 300f;
        [Min(2f)] public float IslandSpacing = 20f;
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
        public List<IslandDecoration> Decorations = new();

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
        public List<WeightedLootEntry> BuriedChests = new();
        public GameObject BuriedMarkerPrefab;

        [Header("Общие ресурсы")]
        public ItemCatalog Catalog;
        public WaveProfile WaterProfile;
        public GameObject RarityEffectPrefab;

        public float Diameter(IslandSize size) => size switch
        { IslandSize.Small => SmallDiameter, IslandSize.Medium => MediumDiameter, _ => LargeDiameter };
    }
}
