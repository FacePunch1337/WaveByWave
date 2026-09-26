using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using StylizedWater3;
using WaveByWave.Generation;
using WaveByWave.Items;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    [InitializeOnLoad]
    public static class OceanContentSetup
    {
        private const string ResourcesFolder = "Assets/_Project/Resources";
        private const string Prefabs = "Assets/_Project/Prefabs/Generation";
        private const string Data = "Assets/_Project/Data/Ocean";
        private const string Materials = "Assets/_Project/Generated/Materials";
        static OceanContentSetup() => EditorApplication.update += InstallWhenReady;
        private static void InstallWhenReady()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            EditorApplication.update -= InstallWhenReady;
            var settings = AssetDatabase.LoadAssetAtPath<OceanGenerationSettings>(ResourcesFolder + "/OceanGeneration.asset");
            if (settings == null || settings.LoadingCurtainPrefab == null) Install();
        }

        [MenuItem("Tools/Wave by Wave/Create Ocean Generation Assets")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            Folder(ResourcesFolder); Folder(Prefabs); Folder(Data);
            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/Data/ItemCatalog.asset");
            if (catalog == null) throw new InvalidOperationException("ItemCatalog is missing.");
            var ground = Material(Materials + "/IslandGround.mat", "WaveByWave/Island Ground", material =>
            {
                material.SetTexture("_SandTex", AssetDatabase.LoadAssetAtPath<Texture2D>(
                    "Assets/Stylized Water 3/_Demo/DemoAssets/Terrain/SWS_DemoSand.png"));
                material.SetTexture("_RockTex", AssetDatabase.LoadAssetAtPath<Texture2D>(
                    "Assets/Stylized Water 3/_Demo/DemoAssets/Materials/Rocks/Textures/Boulder_A_albedo.png"));
            });
            var cross = Material(Materials + "/BuriedCross.mat", "WaveByWave/Buried Cross Decal", _ => { });
            var islandPrefab = Prefab(Prefabs + "/ProceduralIsland.prefab", () =>
            { var root = new GameObject("Procedural Island"); root.AddComponent<ProceduralIsland>(); return root; });
            var chunkPrefab = Prefab(Prefabs + "/IslandChunk.prefab", () =>
            {
                var root = new GameObject("Island Ground Chunk", typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
                root.GetComponent<MeshRenderer>().sharedMaterial = ground;
                return root;
            });
            var markerPrefab = Prefab(Prefabs + "/BuriedChestCross.prefab", () =>
            {
                var root = new GameObject("Buried Chest Cross", typeof(MeshFilter),
                    typeof(MeshRenderer), typeof(BuriedChestMarker));
                var renderer = root.GetComponent<MeshRenderer>(); renderer.sharedMaterial = cross;
                renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
                return root;
            });
            var loadingCurtainPrefab = CreateLoadingCurtainPrefab();
            var chestPrefab = CreateChestPrefab();
            var tablePath = Data + "/ChestLoot.asset";
            var table = AssetDatabase.LoadAssetAtPath<ChestLootTable>(tablePath);
            if (table == null)
            {
                table = ScriptableObject.CreateInstance<ChestLootTable>();
                table.OpenEffectPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Effects/DeathDust.prefab");
                foreach (var item in catalog.Items)
                {
                    if (item == null || (item.Category != ItemCategory.Supply &&
                        item.Category != ItemCategory.Treasure && item.Category != ItemCategory.Weapon)) continue;
                    table.LootPool.Add(new WeightedLootEntry { Item = item, Weight = item.Category == ItemCategory.Treasure ? 3f : 1f,
                        MinimumAmount = 1, MaximumAmount = item.Category == ItemCategory.Supply ? 3 : 1 });
                }
                for (var rarity = 0; rarity < 5; rarity++)
                {
                    var tier = new ChestTierLoot { Tier = (ItemRarity)rarity, MinimumRolls = 2 + rarity, MaximumRolls = 4 + rarity };
                    for (var rewardRarity = 0; rewardRarity <= Mathf.Min(rarity + 1, 4); rewardRarity++)
                        tier.AllowedRarities.Add(new LootRarityTier { Rarity = (ItemRarity)rewardRarity });
                    table.Tiers.Add(tier);
                }
                AssetDatabase.CreateAsset(table, tablePath);
            }
            var chests = new List<ItemDefinition>();
            var names = new[] { "Обычный сундук", "Необычный сундук", "Редкий сундук", "Эпический сундук", "Легендарный сундук" };
            for (var rarity = 0; rarity < 5; rarity++)
            {
                var path = Data + $"/Item_chest_{rarity}.asset";
                var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
                if (item == null)
                {
                    item = ScriptableObject.CreateInstance<ItemDefinition>();
                    var so = new SerializedObject(item);
                    so.FindProperty("id").stringValue = $"chest_{rarity}";
                    so.FindProperty("displayName").stringValue = names[rarity];
                    so.FindProperty("description").stringValue = "E — подобрать. Удерживать E — открыть и получить добычу.";
                    so.FindProperty("category").enumValueIndex = (int)ItemCategory.Chest;
                    so.FindProperty("rarity").enumValueIndex = rarity;
                    so.FindProperty("equipmentKind").enumValueIndex = (int)ItemEquipmentKind.Carry;
                    so.FindProperty("worldVisualPrefab").objectReferenceValue = chestPrefab;
                    so.FindProperty("chestLoot").objectReferenceValue = table;
                    so.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.CreateAsset(item, path);
                }
                chests.Add(item);
            }
            var catalogObject = new SerializedObject(catalog);
            var items = catalogObject.FindProperty("items");
            foreach (var chest in chests)
            {
                var exists = false;
                for (var i = 0; i < items.arraySize; i++) if (items.GetArrayElementAtIndex(i).objectReferenceValue == chest) exists = true;
                if (!exists) { items.InsertArrayElementAtIndex(items.arraySize); items.GetArrayElementAtIndex(items.arraySize - 1).objectReferenceValue = chest; }
            }
            catalogObject.ApplyModifiedPropertiesWithoutUndo();

            var settingsPath = ResourcesFolder + "/OceanGeneration.asset";
            var settings = AssetDatabase.LoadAssetAtPath<OceanGenerationSettings>(settingsPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<OceanGenerationSettings>();
                settings.Catalog = catalog; settings.GroundMaterial = ground; settings.IslandPrefab = islandPrefab;
                settings.ChunkPrefab = chunkPrefab; settings.BuriedMarkerPrefab = markerPrefab;
                settings.WaterProfile = AssetDatabase.LoadAssetAtPath<WaveProfile>("Assets/Stylized Water 3/Profiles/Ocean Wave Profile.asset");
                settings.RarityEffectPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Effects/LootRarity.prefab");
                foreach (var item in catalog.Items)
                    if (item != null && (item.Category == ItemCategory.Supply || item.Category == ItemCategory.Treasure))
                        settings.FloatingObjects.Add(new WeightedItemEntry { Item = item, Weight = item.Category == ItemCategory.Supply ? 4f : 1f });
                for (var i = 0; i < chests.Count; i++) settings.BuriedChests.Add(new WeightedLootEntry
                    { Item = chests[i], Weight = i == 0 ? 45f : i == 1 ? 28f : i == 2 ? 16f : i == 3 ? 8f : 3f });
                AddDecoration(settings, "Assets/Stylized Water 3/_Demo/DemoAssets/Prefabs/Vegetation/SW3_PalmTree.prefab", 0.025f, 0.55f, 0.85f);
                AddDecoration(settings, "Assets/Stylized Water 3/_Demo/DemoAssets/Prefabs/Rocks/Boulder.prefab", 0.035f, 0.25f, 0.6f);
                // The list accepts any authored bush/tree/stone prefab; there is no runtime primitive decoration.
                AssetDatabase.CreateAsset(settings, settingsPath);
            }
            var settingsObject = new SerializedObject(settings);
            settingsObject.FindProperty("LoadingCurtainPrefab").objectReferenceValue = loadingCurtainPrefab;
            settingsObject.ApplyModifiedPropertiesWithoutUndo();
            Prefab(ResourcesFolder + "/OceanWorld.prefab", () =>
            {
                var root = new GameObject("Ocean World"); var director = root.AddComponent<OceanWorldDirector>();
                var so = new SerializedObject(director); so.FindProperty("settings").objectReferenceValue = settings;
                so.ApplyModifiedPropertiesWithoutUndo(); return root;
            });
            Prefab(Prefabs + "/IslandTestSpawner.prefab", () =>
            { var root = new GameObject("Island Test Spawner"); root.AddComponent<IslandTestSpawner>(); return root; });
            Prefab(ResourcesFolder + "/ChestHoldProgress.prefab", () =>
            {
                var root = new GameObject("Chest Hold Progress", typeof(RectTransform), typeof(Image));
                var rect = (RectTransform)root.transform;
                rect.anchorMin = new Vector2(0f, 0f); rect.anchorMax = new Vector2(1f, 0f);
                rect.pivot = new Vector2(0.5f, 1f); rect.sizeDelta = new Vector2(0f, 7f);
                root.GetComponent<Image>().color = new Color(0.03f, 0.025f, 0.01f, 0.95f);
                root.GetComponent<Image>().raycastTarget = false;
                var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image)); fill.transform.SetParent(root.transform, false);
                var fillRect = (RectTransform)fill.transform; fillRect.anchorMin = Vector2.zero; fillRect.anchorMax = Vector2.one;
                fillRect.offsetMin = fillRect.offsetMax = Vector2.zero;
                var image = fill.GetComponent<Image>(); image.color = new Color(1f, 0.76f, 0.2f);
                image.type = Image.Type.Filled; image.fillMethod = Image.FillMethod.Horizontal; image.fillAmount = 0f;
                image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"); image.raycastTarget = false;
                return root;
            });
            AssetDatabase.SaveAssets();
            Debug.Log("[Ocean] Generation assets, five chest tiers and test spawner are ready.");
        }

        private static GameObject CreateChestPrefab()
        {
            return Prefab("Assets/_Project/Prefabs/Items/LootChest.prefab", () =>
            {
                var source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/ShipTreasureChest.prefab");
                var filters = source.GetComponentsInChildren<MeshFilter>(true);
                var combine = new List<CombineInstance>(); var materials = new List<Material>();
                foreach (var filter in filters)
                {
                    var renderer = filter.GetComponent<MeshRenderer>();
                    for (var sub = 0; sub < filter.sharedMesh.subMeshCount; sub++)
                    {
                        combine.Add(new CombineInstance { mesh = filter.sharedMesh, subMeshIndex = sub,
                            transform = filter.transform.localToWorldMatrix });
                        var material = renderer.sharedMaterials[Mathf.Min(sub, renderer.sharedMaterials.Length - 1)];
                        var path = Materials + "/Chest_" + material.name + ".mat";
                        var copy = AssetDatabase.LoadAssetAtPath<Material>(path);
                        if (copy == null) { copy = new Material(material) { enableInstancing = true }; AssetDatabase.CreateAsset(copy, path); }
                        materials.Add(copy);
                    }
                }
                var mesh = new Mesh { name = "Loot Chest Single Mesh" };
                mesh.CombineMeshes(combine.ToArray(), false, true); mesh.RecalculateBounds();
                var vertices = mesh.vertices; var center = mesh.bounds.center; var scale = 0.85f / mesh.bounds.size.x;
                for (var i = 0; i < vertices.Length; i++) vertices[i] = (vertices[i] - center) * scale;
                mesh.vertices = vertices; mesh.RecalculateBounds();
                AssetDatabase.CreateAsset(mesh, Data + "/LootChestMesh.asset");
                var root = new GameObject("Loot Chest", typeof(MeshFilter), typeof(MeshRenderer));
                root.GetComponent<MeshFilter>().sharedMesh = mesh; root.GetComponent<MeshRenderer>().sharedMaterials = materials.ToArray();
                return root;
            });
        }

        private static GameObject CreateLoadingCurtainPrefab()
        {
            return Prefab("Assets/_Project/Prefabs/Generation/OceanLoadingCurtain.prefab", () =>
            {
                var root = new GameObject("Ocean Loading Curtain", typeof(RectTransform), typeof(Canvas),
                    typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup), typeof(OceanLoadingCurtain));
                var rootRect = (RectTransform)root.transform;
                rootRect.anchorMin = Vector2.zero; rootRect.anchorMax = Vector2.one;
                rootRect.offsetMin = rootRect.offsetMax = Vector2.zero;
                rootRect.localScale = Vector3.one;
                var canvas = root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32000;
                root.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                root.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1920f, 1080f);

                var backdrop = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
                backdrop.transform.SetParent(root.transform, false);
                var backdropRect = (RectTransform)backdrop.transform;
                backdropRect.anchorMin = Vector2.zero; backdropRect.anchorMax = Vector2.one;
                backdropRect.offsetMin = backdropRect.offsetMax = Vector2.zero;
                var backdropImage = backdrop.GetComponent<Image>();
                backdropImage.color = new Color(0.015f, 0.02f, 0.025f, 1f);

                var label = new GameObject("Progress", typeof(RectTransform), typeof(Text));
                label.transform.SetParent(backdrop.transform, false);
                var labelRect = (RectTransform)label.transform;
                labelRect.anchorMin = new Vector2(0.2f, 0.35f); labelRect.anchorMax = new Vector2(0.8f, 0.65f);
                labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
                var text = label.GetComponent<Text>();
                text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                text.fontSize = 32; text.alignment = TextAnchor.MiddleCenter;
                text.color = Color.white; text.raycastTarget = false;
                text.text = "Подготавливаем океан...";

                var serialized = new SerializedObject(root.GetComponent<OceanLoadingCurtain>());
                serialized.FindProperty("canvasGroup").objectReferenceValue = root.GetComponent<CanvasGroup>();
                serialized.FindProperty("progressLabel").objectReferenceValue = text;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                return root;
            });
        }
        private static void AddDecoration(OceanGenerationSettings settings, string path, float density, float min, float max)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null) settings.Decorations.Add(new IslandDecoration { Prefab = prefab,
                InstancesPerSquareMetre = density, ScaleRange = new Vector2(min, max), Spacing = 1.4f });
        }
        private static Material Material(string path, string shaderName, Action<Material> configure)
        {
            var result = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (result != null) return result;
            var shader = Shader.Find(shaderName);
            if (shader == null) throw new InvalidOperationException("Shader unavailable: " + shaderName);
            result = new Material(shader); configure(result); AssetDatabase.CreateAsset(result, path); return result;
        }
        private static GameObject Prefab(string path, Func<GameObject> build)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;
            var root = build();
            try { return PrefabUtility.SaveAsPrefabAsset(root, path); }
            finally { Object.DestroyImmediate(root); }
        }
        private static void Folder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/'); Folder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }

    [CustomEditor(typeof(IslandTestSpawner))]
    public sealed class IslandTestSpawnerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var settings = AssetDatabase.LoadAssetAtPath<OceanGenerationSettings>("Assets/_Project/Resources/OceanGeneration.asset");
            var sizes = settings != null
                ? $"Small {settings.SmallDiameter:0.#} м, Medium {settings.MediumDiameter:0.#} м, Large {settings.LargeDiameter:0.#} м."
                : "Диаметры берутся из OceanGeneration.asset.";
            EditorGUILayout.HelpBox($"Play Mode: {sizes} Генерация выполняется у хоста и видна всем игрокам.", MessageType.Info);
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Сгенерировать остров")) ((IslandTestSpawner)target).Generate();
                if (GUILayout.Button("Удалить остров")) ((IslandTestSpawner)target).Remove();
            }
        }
    }
}
