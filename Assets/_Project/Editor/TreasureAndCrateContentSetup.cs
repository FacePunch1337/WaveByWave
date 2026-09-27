using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WaveByWave.Generation;
using WaveByWave.Items;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    [InitializeOnLoad]
    public static class TreasureAndCrateContentSetup
    {
        private const string RequestPath = "Temp/TreasureAndCrateContentSetup.request";
        private const string ResultPath = "Temp/TreasureAndCrateContentSetup.result";
        private const string GoldCupRequestPath = "Temp/GoldCupMeshRotation.request";
        private const string GoldCupResultPath = "Temp/GoldCupMeshRotation.result";
        private const string DataRoot = "Assets/_Project/Data";
        private const string TreasureData = DataRoot + "/Items/Treasure";
        private const string CrateData = DataRoot + "/Ocean/Crates";
        private const string CrateItemData = DataRoot + "/Items/Box/Common";
        private const string TreasurePrefabs = "Assets/_Project/Prefabs/Items/Treasure";
        private const string CratePrefabs = "Assets/_Project/Prefabs/Items/Box/Common";

        private readonly struct TreasureSpec
        {
            public readonly string Id, Name, PrefabName;
            public readonly ItemRarity Rarity;
            public TreasureSpec(string id, string name, string prefabName, ItemRarity rarity)
            { Id = id; Name = name; PrefabName = prefabName; Rarity = rarity; }
        }
        private readonly struct CrateSpec
        {
            public readonly string Id, Name, RewardId;
            public readonly int Minimum, Maximum;
            public CrateSpec(string id, string name, string rewardId, int minimum, int maximum)
            { Id = id; Name = name; RewardId = rewardId; Minimum = minimum; Maximum = maximum; }
        }

        private static readonly TreasureSpec[] Treasures =
        {
            new("treasure_anvil", "Наковальня", "Anvil", ItemRarity.Common),
            new("treasure_cauldron", "Котёл", "Cauldron", ItemRarity.Common),
            new("treasure_old_lamp", "Старая лампа", "Old lamp", ItemRarity.Common),
            new("treasure_old_wheel", "Старое колесо", "Old wheel", ItemRarity.Common),

            new("treasure_broken_statue", "Разбитая статуя", "Broken Statue", ItemRarity.Uncommon),
            new("treasure_shell", "Раковина", "Shell", ItemRarity.Uncommon),
            new("treasure_boot", "Старый сапог", "Treasure Boot", ItemRarity.Uncommon),
            new("treasure_vase", "Ваза", "Vase", ItemRarity.Uncommon),

            new("treasure_artful_vase", "Искусная ваза", "An artful vase", ItemRarity.Rare),
            new("treasure_inkwell_and_feather", "Чернильница с пером", "Inkwell And Feather", ItemRarity.Rare),
            new("treasure_knights_helmet", "Рыцарский шлем", "Knight's helmet", ItemRarity.Rare),
            new("treasure_luxurious_mirror", "Роскошное зеркало", "Luxurious mirror", ItemRarity.Rare),

            new("treasure_emerald", "Изумруд", "Emerald", ItemRarity.Epic),
            new("treasure_gold_bar", "Золотой слиток", "Gold bar", ItemRarity.Epic),
            new("treasure_gold_cup", "Золотой кубок", "Gold Cup", ItemRarity.Epic),
            new("treasure_golden_chain", "Золотая цепь", "Golden chain", ItemRarity.Epic),

            new("treasure_diamond", "Алмаз", "Diamond", ItemRarity.Legendary),
            new("treasure_necklace", "Ожерелье", "Necklace", ItemRarity.Legendary),
            new("treasure_orb", "Сфера", "Orb", ItemRarity.Legendary),
            new("treasure_royal_crown", "Королевская корона", "Royal Crown", ItemRarity.Legendary)
        };
        private static readonly CrateSpec[] Crates =
        {
            new("crate_cannonballs", "Ящик с ядрами", "cannonball", 8, 14),
            new("crate_planks", "Ящик с досками", "plank", 4, 8),
            new("crate_rum", "Ящик с ромом", "food", 3, 6)
        };

        static TreasureAndCrateContentSetup() => EditorApplication.update += RunRequested;

        [MenuItem("Tools/Wave by Wave/Items/Connect curated treasures")]
        public static void CreateContentMenu()
        {
            try { CreateAndValidate(); Debug.Log("The curated treasure set is connected."); }
            catch (Exception error) { Debug.LogException(error); }
        }

        [MenuItem("Tools/Wave by Wave/Items/Bake Gold Cup mesh rotation")]
        public static void BakeGoldCupMeshRotationMenu()
        {
            try
            {
                BakeGoldCupMeshRotation();
                Debug.Log("Gold Cup mesh rotation X = -90° is baked into a separate mesh asset.");
            }
            catch (Exception error) { Debug.LogException(error); }
        }

        private static void RunRequested()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (File.Exists(GoldCupRequestPath))
            {
                File.Delete(GoldCupRequestPath);
                try
                {
                    BakeGoldCupMeshRotation();
                    File.WriteAllText(GoldCupResultPath,
                        "PASS: Gold Cup vertices, normals and tangents rotated X -90; prefab rotation reset.");
                }
                catch (Exception error)
                {
                    File.WriteAllText(GoldCupResultPath, "FAIL: " + error);
                    Debug.LogException(error);
                }
                return;
            }
            if (!File.Exists(RequestPath)) return;
            File.Delete(RequestPath);
            try
            {
                CreateAndValidate();
                File.WriteAllText(ResultPath,
                    "PASS: 4 curated treasures per rarity are connected; obsolete treasures were removed without changing other item categories.");
            }
            catch (Exception error)
            {
                File.WriteAllText(ResultPath, "FAIL: " + error);
                Debug.LogException(error);
            }
        }

        private static void BakeGoldCupMeshRotation()
        {
            const string prefabPath = TreasurePrefabs + "/Epic/Gold Cup.prefab";
            const string meshFolder = "Assets/_Project/Generated/Meshes";
            const string meshPath = meshFolder + "/Gold Cup Rotated X -90.asset";
            Directory.CreateDirectory(meshFolder);
            AssetDatabase.Refresh();

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var filter = root.GetComponentInChildren<MeshFilter>(true);
                Require(filter != null && filter.sharedMesh != null,
                    "Gold Cup prefab does not contain a readable MeshFilter.");
                var baked = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                if (filter.sharedMesh != baked)
                {
                    var source = filter.sharedMesh;
                    Require(source.isReadable, "Gold Cup source mesh must have Read/Write enabled.");
                    var rotation = Quaternion.Euler(-90f, 0f, 0f);
                    var rotated = Object.Instantiate(source);
                    rotated.name = "Gold Cup Rotated X -90";

                    var vertices = source.vertices;
                    for (var i = 0; i < vertices.Length; i++) vertices[i] = rotation * vertices[i];
                    rotated.vertices = vertices;
                    var normals = source.normals;
                    if (normals.Length == vertices.Length)
                    {
                        for (var i = 0; i < normals.Length; i++) normals[i] = rotation * normals[i];
                        rotated.normals = normals;
                    }
                    var tangents = source.tangents;
                    if (tangents.Length == vertices.Length)
                    {
                        for (var i = 0; i < tangents.Length; i++)
                        {
                            var tangent = rotation * new Vector3(tangents[i].x, tangents[i].y, tangents[i].z);
                            tangents[i] = new Vector4(tangent.x, tangent.y, tangent.z, tangents[i].w);
                        }
                        rotated.tangents = tangents;
                    }
                    rotated.RecalculateBounds();

                    if (baked == null)
                    {
                        AssetDatabase.CreateAsset(rotated, meshPath);
                        baked = rotated;
                    }
                    else
                    {
                        EditorUtility.CopySerialized(rotated, baked);
                        baked.name = "Gold Cup Rotated X -90";
                        Object.DestroyImmediate(rotated);
                        EditorUtility.SetDirty(baked);
                    }
                    filter.sharedMesh = baked;
                }

                filter.transform.localRotation = Quaternion.identity;
                var transformObject = new SerializedObject(filter.transform);
                var eulerHint = transformObject.FindProperty("m_LocalEulerAnglesHint");
                if (eulerHint != null)
                {
                    eulerHint.vector3Value = Vector3.zero;
                    transformObject.ApplyModifiedPropertiesWithoutUndo();
                }
                var box = filter.GetComponent<BoxCollider>();
                if (box != null)
                {
                    box.center = baked.bounds.center;
                    box.size = baked.bounds.size;
                }
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var savedFilter = prefab != null ? prefab.GetComponentInChildren<MeshFilter>(true) : null;
            Require(savedFilter != null && AssetDatabase.GetAssetPath(savedFilter.sharedMesh) == meshPath,
                "Gold Cup prefab did not retain the baked mesh.");
            Require(Quaternion.Angle(savedFilter.transform.localRotation, Quaternion.identity) < 0.01f,
                "Gold Cup prefab still contains a transform rotation.");
        }

        private static void CreateAndValidate()
        {
            Directory.CreateDirectory(TreasureData);
            Directory.CreateDirectory(CrateData);
            Directory.CreateDirectory(CrateItemData);
            Directory.CreateDirectory(TreasurePrefabs);
            Directory.CreateDirectory(CratePrefabs);
            foreach (ItemRarity rarity in Enum.GetValues(typeof(ItemRarity)))
            {
                Directory.CreateDirectory(TreasureData + "/" + rarity);
                Directory.CreateDirectory(TreasurePrefabs + "/" + rarity);
            }
            AssetDatabase.Refresh();

            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(DataRoot + "/ItemCatalog.asset");
            var chestLoot = AssetDatabase.LoadAssetAtPath<ChestLootTable>(DataRoot + "/Ocean/ChestLoot.asset");
            var ocean = AssetDatabase.LoadAssetAtPath<OceanGenerationSettings>("Assets/_Project/Resources/OceanGeneration.asset");
            Require(catalog != null && chestLoot != null && ocean != null, "Catalog, ChestLoot or OceanGeneration is missing.");
            var chest = FindItem(catalog, "chest_0");
            var boot = FindItem(catalog, "treasure_boot");
            Require(chest != null && boot != null, "Treasure icon or held profile is missing.");

            var rarityIcons = new Dictionary<ItemRarity, Sprite>
            {
                [ItemRarity.Common] = FindOptionalItem(catalog, "treasure_anvil")?.Icon ?? chest.Icon,
                [ItemRarity.Uncommon] = boot.Icon,
                [ItemRarity.Rare] = FindOptionalItem(catalog, "treasure_artful_vase")?.Icon ??
                    FindOptionalItem(catalog, "treasure_cup")?.Icon ?? chest.Icon,
                [ItemRarity.Epic] = FindOptionalItem(catalog, "treasure_emerald")?.Icon ??
                    FindOptionalItem(catalog, "treasure_idol")?.Icon ?? chest.Icon,
                [ItemRarity.Legendary] = FindOptionalItem(catalog, "treasure_diamond")?.Icon ??
                    FindOptionalItem(catalog, "treasure_crown")?.Icon ?? chest.Icon
            };
            var definitions = new List<ItemDefinition>(Treasures.Length);
            foreach (var spec in Treasures)
            {
                var item = GetOrCreateTreasure(spec, rarityIcons[spec.Rarity], boot);
                Require(item != null && item.Id == spec.Id && item.Category == ItemCategory.Treasure && item.Rarity == spec.Rarity,
                    "Invalid treasure definition: " + spec.Id);
                definitions.Add(item);
                AddCatalogItem(catalog, item);
            }
            RemoveObsoleteTreasures(catalog, chestLoot, ocean, definitions);

            ConfigureChestPool(chestLoot, definitions);
            ConfigureFloatingPool(ocean, definitions);
            EditorUtility.SetDirty(catalog);
            EditorUtility.SetDirty(chestLoot);
            EditorUtility.SetDirty(ocean);
            if (ocean.FloatingItemPoolAsset != null)
                EditorUtility.SetDirty(ocean.FloatingItemPoolAsset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Validate(catalog, chestLoot, ocean, definitions);
        }

        private static ItemDefinition GetOrCreateTreasure(TreasureSpec spec, Sprite icon,
            ItemDefinition heldProfile)
        {
            var prefabPath = TreasurePrefabs + "/" + spec.Rarity + "/" + spec.PrefabName + ".prefab";
            var preparedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Require(preparedPrefab != null, "Prepared treasure prefab is missing: " + prefabPath);
            var preparedFilters = preparedPrefab.GetComponentsInChildren<MeshFilter>(true);
            var preparedIsSupported = preparedFilters.Length == 1 && preparedFilters[0].sharedMesh != null &&
                preparedFilters[0].GetComponent<MeshRenderer>() != null;
            var placeholderPath = TreasurePrefabs + "/" + spec.Rarity + "/Placeholder " + spec.PrefabName + ".prefab";
            GameObject prefab;
            if (preparedIsSupported)
            {
                prefab = preparedPrefab;
                if (AssetDatabase.LoadAssetAtPath<GameObject>(placeholderPath) != null)
                    AssetDatabase.DeleteAsset(placeholderPath);
            }
            else
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(
                    "Assets/_Project/Generated/Materials/LootSurface.mat");
                Require(material != null, "LootSurface material is missing for treasure placeholders.");
                prefab = CreateCubePrefab(placeholderPath, spec.Name + " (заглушка)",
                    Vector3.one * 0.55f, material);
                Debug.LogWarning($"[Treasures] '{spec.PrefabName}' contains {preparedFilters.Length} MeshFilters. " +
                    "A cube placeholder remains connected until the prepared prefab contains exactly one mesh.",
                    preparedPrefab);
            }

            var assetPath = TreasureAssetPath(spec);
            var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(assetPath);
            var created = item == null;
            if (created) item = ScriptableObject.CreateInstance<ItemDefinition>();
            var resolvedIcon = item.Icon != null ? item.Icon : icon;
            var serialized = new SerializedObject(item);
            SetCommonItem(serialized, spec.Id, spec.Name,
                "Сдать в корабельный сундук для опыта команды.", ItemCategory.Treasure, spec.Rarity,
                resolvedIcon, prefab);
            serialized.FindProperty("potency").floatValue = 0f;
            serialized.FindProperty("treasureExperience").intValue = Experience(spec.Rarity);
            serialized.FindProperty("equipmentKind").enumValueIndex = (int)ItemEquipmentKind.Carry;
            serialized.FindProperty("heldAndIkProfile").objectReferenceValue = spec.Id == "treasure_boot" ? null : heldProfile;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            if (created) AssetDatabase.CreateAsset(item, assetPath);
            else EditorUtility.SetDirty(item);
            return item;
        }

        private static string TreasureAssetPath(TreasureSpec spec) =>
            TreasureData + "/" + spec.Rarity + "/Item_" + spec.Id + ".asset";

        private static void RemoveObsoleteTreasures(ItemCatalog catalog, ChestLootTable chestLoot,
            OceanGenerationSettings ocean, IReadOnlyCollection<ItemDefinition> retained)
        {
            var keep = new HashSet<ItemDefinition>(retained);
            var obsolete = AssetDatabase.FindAssets("t:ItemDefinition", new[] { TreasureData })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<ItemDefinition>)
                .Where(item => item != null && item.Category == ItemCategory.Treasure && !keep.Contains(item))
                .ToList();
            if (obsolete.Count == 0) return;
            var remove = new HashSet<ItemDefinition>(obsolete);
            chestLoot.LootPool.RemoveAll(entry => entry?.Item != null && remove.Contains(entry.Item));
            ocean.FloatingObjects.RemoveAll(entry => entry?.Item != null && remove.Contains(entry.Item));

            var catalogObject = new SerializedObject(catalog);
            var items = catalogObject.FindProperty("items");
            for (var i = items.arraySize - 1; i >= 0; i--)
            {
                var candidate = items.GetArrayElementAtIndex(i).objectReferenceValue as ItemDefinition;
                if (candidate == null || !remove.Contains(candidate)) continue;
                items.DeleteArrayElementAtIndex(i);
                if (i < items.arraySize && items.GetArrayElementAtIndex(i).objectReferenceValue == null)
                    items.DeleteArrayElementAtIndex(i);
            }
            catalogObject.ApplyModifiedPropertiesWithoutUndo();
            foreach (var item in obsolete)
            {
                var path = AssetDatabase.GetAssetPath(item);
                Require(AssetDatabase.DeleteAsset(path), "Cannot remove obsolete treasure: " + path);
            }
        }
        private static ItemDefinition CreateCrate(CrateSpec spec, ChestLootTable table, Sprite icon,
            Material material, ItemDefinition heldProfile)
        {
            var assetPath = CrateItemData + "/Item_" + spec.Id + ".asset";
            var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(assetPath);
            if (item == null)
            {
                var prefab = CreateCubePrefab(CratePrefabs + "/" + spec.Id + ".prefab", spec.Name,
                    new Vector3(0.55f, 0.42f, 0.45f), material);
                item = ScriptableObject.CreateInstance<ItemDefinition>();
                var serialized = new SerializedObject(item);
                SetCommonItem(serialized, spec.Id, spec.Name,
                    "E — подобрать. Удерживать E — открыть и получить припасы.", ItemCategory.Chest,
                    ItemRarity.Common, icon, prefab);
                serialized.FindProperty("potency").floatValue = 0f;
                serialized.FindProperty("treasureExperience").intValue = 0;
                serialized.FindProperty("chestLoot").objectReferenceValue = table;
                serialized.FindProperty("equipmentKind").enumValueIndex = (int)ItemEquipmentKind.Carry;
                serialized.FindProperty("heldAndIkProfile").objectReferenceValue = heldProfile;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.CreateAsset(item, assetPath);
            }
            else
            {
                var serialized = new SerializedObject(item);
                serialized.FindProperty("chestLoot").objectReferenceValue = table;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(item);
            }
            return item;
        }

        private static ChestLootTable CreateCrateTable(CrateSpec spec, ItemDefinition reward, GameObject effect)
        {
            var path = CrateData + "/Loot_" + spec.Id + ".asset";
            var table = AssetDatabase.LoadAssetAtPath<ChestLootTable>(path);
            if (table == null)
            {
                table = ScriptableObject.CreateInstance<ChestLootTable>();
                table.HoldDuration = 1.2f;
                table.ShakeDuration = 0.65f;
                table.EjectionSpeed = 2.4f;
                table.OpenEffectPrefab = effect;
                AssetDatabase.CreateAsset(table, path);
            }
            table.LootPool.Clear();
            table.LootPool.Add(new WeightedLootEntry
            { Item = reward, Weight = 1f, MinimumAmount = spec.Minimum, MaximumAmount = spec.Maximum });
            table.Tiers.Clear();
            table.Tiers.Add(new ChestTierLoot
            {
                Tier = ItemRarity.Common,
                MinimumRolls = 1,
                MaximumRolls = 1,
                MatchingRarityPercent = 100f,
                AllowedRarities = new List<LootRarityTier> { new() { Rarity = ItemRarity.Common } }
            });
            EditorUtility.SetDirty(table);
            return table;
        }

        private static GameObject CreateCubePrefab(string path, string name, Vector3 scale, Material material)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null) return prefab;
            var instance = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                instance.name = name;
                instance.transform.localScale = scale;
                Object.DestroyImmediate(instance.GetComponent<Collider>());
                instance.GetComponent<MeshRenderer>().sharedMaterial = material;
                return PrefabUtility.SaveAsPrefabAsset(instance, path);
            }
            finally { Object.DestroyImmediate(instance); }
        }

        private static void SetCommonItem(SerializedObject serialized, string id, string displayName,
            string description, ItemCategory category, ItemRarity rarity, Sprite icon, GameObject prefab)
        {
            serialized.FindProperty("id").stringValue = id;
            serialized.FindProperty("displayName").stringValue = displayName;
            serialized.FindProperty("description").stringValue = description;
            serialized.FindProperty("category").enumValueIndex = (int)category;
            serialized.FindProperty("rarity").enumValueIndex = (int)rarity;
            serialized.FindProperty("icon").objectReferenceValue = icon;
            serialized.FindProperty("maximumStack").intValue = 1;
            serialized.FindProperty("supplyKind").enumValueIndex = (int)SupplyKind.None;
            serialized.FindProperty("worldVisualPrefab").objectReferenceValue = prefab;
            serialized.FindProperty("restingEulerAngles").vector3Value = Vector3.zero;
        }

        private static int Experience(ItemRarity rarity) => rarity switch
        {
            ItemRarity.Uncommon => 25,
            ItemRarity.Rare => 200,
            ItemRarity.Epic => 600,
            ItemRarity.Legendary => 1200,
            _ => 10
        };

        private static void AddCatalogItem(ItemCatalog catalog, ItemDefinition item)
        {
            var serialized = new SerializedObject(catalog);
            var items = serialized.FindProperty("items");
            for (var i = 0; i < items.arraySize; i++)
                if (items.GetArrayElementAtIndex(i).objectReferenceValue == item) return;
            items.InsertArrayElementAtIndex(items.arraySize);
            items.GetArrayElementAtIndex(items.arraySize - 1).objectReferenceValue = item;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureChestPool(ChestLootTable table, IReadOnlyList<ItemDefinition> treasures)
        {
            foreach (var item in treasures)
            {
                var entry = table.LootPool.Find(value => value != null && value.Item == item);
                if (entry == null)
                {
                    entry = new WeightedLootEntry { Item = item };
                    table.LootPool.Add(entry);
                }
                // Four curated variants preserve the old aggregate treasure weight of three per rarity.
                entry.Weight = 0.75f;
                entry.MinimumAmount = entry.MaximumAmount = 1;
            }
        }

        private static void ConfigureFloatingPool(OceanGenerationSettings ocean,
            IReadOnlyList<ItemDefinition> treasures)
        {
            var tierTotals = new[] { 0.8f, 0.5f, 0.2f, 0.1f, 0.01f };
            foreach (var item in treasures)
            {
                var entry = ocean.FloatingObjects.Find(value => value != null && value.Item == item);
                if (entry == null)
                {
                    entry = new WeightedItemEntry { Item = item };
                    ocean.FloatingObjects.Add(entry);
                }
                entry.Weight = tierTotals[(int)item.Rarity] / 4f;
            }
        }

        private static void Validate(ItemCatalog catalog, ChestLootTable chestLoot,
            OceanGenerationSettings ocean, IReadOnlyList<ItemDefinition> treasures)
        {
            Require(treasures.Count == 20 && treasures.Distinct().Count() == 20, "Treasure list is not unique.");
            foreach (ItemRarity rarity in Enum.GetValues(typeof(ItemRarity)))
                Require(treasures.Count(item => item.Rarity == rarity) == 4,
                    rarity + " does not contain exactly four treasures.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in catalog.Items)
                Require(item == null || ids.Add(item.Id), "Duplicate catalog ID: " + item?.Id);
            var retained = new HashSet<ItemDefinition>(treasures);
            Require(catalog.Items.Where(item => item != null && item.Category == ItemCategory.Treasure)
                    .All(retained.Contains) &&
                catalog.Items.Count(item => item != null && item.Category == ItemCategory.Treasure) == 20,
                "ItemCatalog still contains an obsolete treasure.");
            Require(chestLoot.LootPool.Count(entry => entry?.Item != null &&
                    entry.Item.Category == ItemCategory.Treasure) == 20 &&
                chestLoot.LootPool.Where(entry => entry?.Item != null &&
                    entry.Item.Category == ItemCategory.Treasure).All(entry => retained.Contains(entry.Item)),
                "ChestLoot still contains an obsolete treasure.");
            Require(ocean.FloatingObjects.Count(entry => entry?.Item != null &&
                    entry.Item.Category == ItemCategory.Treasure) == 20 &&
                ocean.FloatingObjects.Where(entry => entry?.Item != null &&
                    entry.Item.Category == ItemCategory.Treasure).All(entry => retained.Contains(entry.Item)),
                "FloatingItemPool still contains an obsolete treasure.");
            foreach (var item in treasures)
            {
                Require(catalog.Items.Contains(item), item.Id + " is absent from ItemCatalog.");
                var filters = item.WorldVisualPrefab != null
                    ? item.WorldVisualPrefab.GetComponentsInChildren<MeshFilter>(true)
                    : Array.Empty<MeshFilter>();
                Require(filters.Length == 1 && filters[0].sharedMesh != null &&
                    filters[0].GetComponent<MeshRenderer>() != null,
                    item.Id + " does not have a usable single-mesh visual or cube placeholder.");
                Require(chestLoot.LootPool.Count(entry => entry?.Item == item) == 1,
                    item.Id + " is absent or duplicated in ChestLoot.");
                Require(ocean.FloatingObjects.Count(entry => entry?.Item == item) == 1,
                    item.Id + " is absent or duplicated in FloatingObjects.");
            }
            var storedTreasures = AssetDatabase.FindAssets("t:ItemDefinition", new[] { TreasureData })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<ItemDefinition>)
                .Where(item => item != null && item.Category == ItemCategory.Treasure)
                .ToList();
            Require(storedTreasures.Count == 20 && storedTreasures.All(retained.Contains),
                "Treasure folders still contain obsolete ItemDefinition assets.");
        }

        private static ItemDefinition FindOptionalItem(ItemCatalog catalog, string id) =>
            catalog.Items.FirstOrDefault(candidate => candidate != null && candidate.Id == id);

        private static ItemDefinition FindItem(ItemCatalog catalog, string id)
        {
            var item = catalog.Items.FirstOrDefault(candidate => candidate != null && candidate.Id == id);
            Require(item != null, "Item is missing from catalog: " + id);
            return item;
        }

        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
