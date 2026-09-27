using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WaveByWave.Items;

namespace WaveByWave.Editor
{
    [InitializeOnLoad]
    public static class ItemContentOrganizer
    {
        private const string DataRoot = "Assets/_Project/Data/Items";
        private const string PrefabRoot = "Assets/_Project/Prefabs/Items";
        private const string RequestPath = "Temp/ItemContentOrganizer.request";
        private const string ResultPath = "Temp/ItemContentOrganizer.result";

        static ItemContentOrganizer() => EditorApplication.update += RunRequested;

        [MenuItem("Tools/Wave by Wave/Items/Organize all item assets")]
        public static void OrganizeMenu()
        {
            try
            {
                var count = OrganizeAll();
                Debug.Log($"[Items] Organized {count} definitions and their visual prefabs by category and rarity.");
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
        }

        private static void RunRequested()
        {
            if (!File.Exists(RequestPath) || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            File.Delete(RequestPath);
            try
            {
                var count = OrganizeAll();
                File.WriteAllText(ResultPath,
                    $"PASS: organized {count} item definitions and unique visual prefabs by category and rarity.");
            }
            catch (Exception error)
            {
                File.WriteAllText(ResultPath, "FAIL: " + error);
                Debug.LogException(error);
            }
        }

        public static int OrganizeAll()
        {
            EnsureFolders();
            var items = AssetDatabase.FindAssets("t:ItemDefinition")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<ItemDefinition>)
                .Where(item => item != null)
                .OrderBy(item => item.Category)
                .ThenBy(item => item.Rarity)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToList();
            Require(items.Count > 0, "No ItemDefinition assets were found.");

            OrganizeVisualPrefabs(items);
            OrganizeDefinitions(items);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            DeleteLegacyFolderIfEmpty("Assets/_Project/Data/Treasures");
            DeleteLegacyFolderIfEmpty("Assets/_Project/Prefabs/Items/Treasures");
            DeleteLegacyFolderIfEmpty("Assets/_Project/Prefabs/Items/Crates");
            AssetDatabase.SaveAssets();
            Validate(items);
            return items.Count;
        }

        private static void EnsureFolders()
        {
            Directory.CreateDirectory(DataRoot);
            Directory.CreateDirectory(PrefabRoot);
            foreach (ItemCategory category in Enum.GetValues(typeof(ItemCategory)))
            foreach (ItemRarity rarity in Enum.GetValues(typeof(ItemRarity)))
            {
                Directory.CreateDirectory($"{DataRoot}/{category}/{rarity}");
                Directory.CreateDirectory($"{PrefabRoot}/{category}/{rarity}");
            }
            foreach (ItemRarity rarity in Enum.GetValues(typeof(ItemRarity)))
            {
                Directory.CreateDirectory($"{DataRoot}/Box/{rarity}");
                Directory.CreateDirectory($"{PrefabRoot}/Box/{rarity}");
            }
            AssetDatabase.Refresh();
        }

        private static void OrganizeVisualPrefabs(IReadOnlyList<ItemDefinition> items)
        {
            var groups = items.Where(item => item.WorldVisualPrefab != null)
                .GroupBy(item => item.WorldVisualPrefab)
                .ToList();
            foreach (var group in groups)
            {
                var members = group.OrderBy(item => item.Category)
                    .ThenBy(item => item.Rarity)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .ToList();
                var owner = members[0];
                var ownerTarget = PrefabPath(owner);
                var sourcePath = AssetDatabase.GetAssetPath(group.Key);
                Require(!string.IsNullOrEmpty(sourcePath) && sourcePath.StartsWith("Assets/", StringComparison.Ordinal),
                    $"Visual prefab for '{owner.Id}' is not a project asset.");

                var ownerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ownerTarget);
                if (ownerPrefab == null)
                {
                    if (!string.Equals(sourcePath, ownerTarget, StringComparison.Ordinal))
                    {
                        var error = AssetDatabase.MoveAsset(sourcePath, ownerTarget);
                        Require(string.IsNullOrEmpty(error), $"Cannot move visual for '{owner.Id}': {error}");
                    }
                    ownerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ownerTarget);
                }
                Require(ownerPrefab != null, $"Cannot load organized visual for '{owner.Id}'.");
                AssignVisual(owner, ownerPrefab);

                for (var i = 1; i < members.Count; i++)
                {
                    var item = members[i];
                    var target = PrefabPath(item);
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(target);
                    if (prefab == null)
                    {
                        Require(AssetDatabase.CopyAsset(ownerTarget, target),
                            $"Cannot create independent visual prefab for '{item.Id}'.");
                        prefab = AssetDatabase.LoadAssetAtPath<GameObject>(target);
                    }
                    Require(prefab != null, $"Cannot load visual prefab for '{item.Id}'.");
                    AssignVisual(item, prefab);
                }
            }
        }

        private static void OrganizeDefinitions(IEnumerable<ItemDefinition> items)
        {
            foreach (var item in items)
            {
                var source = AssetDatabase.GetAssetPath(item);
                var target = DefinitionPath(item);
                if (string.Equals(source, target, StringComparison.Ordinal)) continue;
                var collision = AssetDatabase.LoadAssetAtPath<ItemDefinition>(target);
                Require(collision == null || collision == item,
                    $"Destination already contains another item: {target}");
                var error = AssetDatabase.MoveAsset(source, target);
                Require(string.IsNullOrEmpty(error), $"Cannot move '{item.Id}': {error}");
            }
        }

        private static void AssignVisual(ItemDefinition item, GameObject prefab)
        {
            if (item.WorldVisualPrefab == prefab) return;
            var serialized = new SerializedObject(item);
            serialized.FindProperty("worldVisualPrefab").objectReferenceValue = prefab;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(item);
        }

        private static void Validate(IReadOnlyList<ItemDefinition> items)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/Data/ItemCatalog.asset");
            Require(catalog != null, "ItemCatalog is missing.");
            Require(catalog.Items.Count == items.Count && items.All(catalog.Items.Contains),
                "ItemCatalog does not contain every organized ItemDefinition exactly once.");
            var visualPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                Require(string.Equals(AssetDatabase.GetAssetPath(item), DefinitionPath(item), StringComparison.Ordinal),
                    $"Item '{item.Id}' is outside its category/rarity folder.");
                Require(item.WorldVisualPrefab != null, $"Item '{item.Id}' has no visual prefab.");
                var visualPath = AssetDatabase.GetAssetPath(item.WorldVisualPrefab);
                Require(string.Equals(visualPath, PrefabPath(item), StringComparison.Ordinal),
                    $"Visual for '{item.Id}' is outside its category/rarity folder.");
                Require(visualPaths.Add(visualPath),
                    $"Items still share a visual prefab after organization: {visualPath}");
                var filters = item.WorldVisualPrefab.GetComponentsInChildren<MeshFilter>(true);
                Require(filters.Length == 1 && filters[0].sharedMesh != null &&
                    filters[0].GetComponent<MeshRenderer>() != null,
                    $"Visual prefab for '{item.Id}' must contain one MeshFilter and MeshRenderer.");
            }
        }

        private static string DefinitionPath(ItemDefinition item) =>
            $"{DataRoot}/{CategoryFolder(item)}/{item.Rarity}/{Path.GetFileName(AssetDatabase.GetAssetPath(item))}";

        private static string PrefabPath(ItemDefinition item)
        {
            var current = item.WorldVisualPrefab != null
                ? AssetDatabase.GetAssetPath(item.WorldVisualPrefab)
                : string.Empty;
            var expectedFolder = $"{PrefabRoot}/{CategoryFolder(item)}/{item.Rarity}/";
            if (!string.IsNullOrEmpty(current) && current.StartsWith(expectedFolder, StringComparison.Ordinal))
                return current;
            return $"{expectedFolder}{SafeName(item.Id)}.prefab";
        }

        private static string CategoryFolder(ItemDefinition item) =>
            item.Category == ItemCategory.Chest && item.Id != null &&
            item.Id.StartsWith("crate_", StringComparison.Ordinal) ? "Box" : item.Category.ToString();

        private static string SafeName(string value)
        {
            Require(!string.IsNullOrWhiteSpace(value), "Item ID is empty.");
            foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return value;
        }

        private static void DeleteLegacyFolderIfEmpty(string path)
        {
            if (!AssetDatabase.IsValidFolder(path)) return;
            var absolute = Path.GetFullPath(path);
            if (Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories)
                .Any(file => !file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))) return;
            AssetDatabase.DeleteAsset(path);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
