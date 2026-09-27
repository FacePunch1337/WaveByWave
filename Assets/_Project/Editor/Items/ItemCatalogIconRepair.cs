using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WaveByWave.Items;

namespace WaveByWave.Editor.Items
{
    [InitializeOnLoad]
    public static class ItemCatalogIconRepair
    {
        private const string RequestPath = "Temp/ItemCatalogIconRepair.request";
        private const string ResultPath = "Temp/ItemCatalogIconRepair.result";
        private const string CatalogPath = "Assets/_Project/Data/ItemCatalog.asset";
        private static ItemIconBakeJob _job;
        private static int _repairCount;
        private static string _repairDescription;

        static ItemCatalogIconRepair() => EditorApplication.update += Tick;

        [MenuItem("Tools/Wave by Wave/Items/Check connections and repair shared icons")]
        public static void RepairMenu()
        {
            try { Begin(null); }
            catch (Exception error) { Debug.LogException(error); }
        }

        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (_job == null && File.Exists(RequestPath)) EditorApplication.isPlaying = false;
                return;
            }
            if (_job == null)
            {
                if (!File.Exists(RequestPath)) return;
                var command = File.ReadAllText(RequestPath);
                File.Delete(RequestPath);
                try { Begin(command); }
                catch (Exception error)
                {
                    File.WriteAllText(ResultPath, "FAIL: " + error);
                    Debug.LogException(error);
                }
                return;
            }

            _job.Tick(EditorApplication.timeSinceStartup);
            if (!_job.Done) return;
            try
            {
                if (_job.Failed > 0)
                    throw new InvalidOperationException(string.Join(" | ", _job.Messages));
                var items = LoadAndValidateConnections();
                Require(items.All(item => item.Icon != null), "At least one item still has no icon.");
                Require(items.GroupBy(item => item.Icon).All(group => group.Count() == 1),
                    "At least two items still share the same icon asset.");
                AssetDatabase.SaveAssets();
                File.WriteAllText(ResultPath,
                    $"PASS: {items.Count} catalog items connected; {_repairCount} {_repairDescription} rebaked as unique assets.");
            }
            catch (Exception error)
            {
                File.WriteAllText(ResultPath, "FAIL: " + error);
                Debug.LogException(error);
            }
            finally { _job = null; _repairCount = 0; _repairDescription = null; }
        }

        private static void Begin(string command)
        {
            if (_job != null) throw new InvalidOperationException("Icon repair is already running.");
            var items = LoadAndValidateConnections();
            ItemDefinition[] repair;
            if (!string.IsNullOrWhiteSpace(command) && command.StartsWith("items=", StringComparison.Ordinal))
            {
                var ids = command.Substring("items=".Length).Split(',')
                    .Select(value => value.Trim()).Where(value => value.Length > 0)
                    .ToHashSet(StringComparer.Ordinal);
                repair = items.Where(item => ids.Contains(item.Id)).ToArray();
                Require(repair.Length == ids.Count, "At least one requested item ID is absent from ItemCatalog.");
                _repairDescription = "selected icons with enclosed-background removal";
            }
            else
            {
                var shared = items.Where(item => item.Icon != null)
                    .GroupBy(item => item.Icon)
                    .Where(group => group.Count() > 1)
                    .SelectMany(group => group)
                    .ToHashSet();
                repair = items.Where(item => item.Icon == null || shared.Contains(item)).ToArray();
                _repairDescription = "shared or empty icons";
            }
            _repairCount = repair.Length;
            if (repair.Length == 0)
            {
                File.WriteAllText(ResultPath,
                    $"PASS: {items.Count} catalog items connected; every item already has a unique icon asset.");
                return;
            }
            _job = new ItemIconBakeJob(repair, new ItemIconBakeSettings
            {
                ReplaceExisting = true,
                RemoveEnclosedBackground = true
            });
        }

        private static List<ItemDefinition> LoadAndValidateConnections()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(CatalogPath);
            Require(catalog != null, "ItemCatalog is missing.");
            var items = catalog.Items.Where(item => item != null).ToList();
            Require(items.Count == catalog.Items.Count, "ItemCatalog contains an empty entry.");
            Require(items.Distinct().Count() == items.Count, "ItemCatalog contains a duplicate definition.");
            Require(items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == items.Count,
                "ItemCatalog contains a duplicate ID.");
            var allDefinitions = AssetDatabase.FindAssets("t:ItemDefinition")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<ItemDefinition>)
                .Where(item => item != null)
                .ToList();
            Require(allDefinitions.Count == items.Count && allDefinitions.All(items.Contains),
                "ItemCatalog does not contain every ItemDefinition exactly once.");

            var visuals = new HashSet<GameObject>();
            foreach (var item in items)
            {
                Require(item.WorldVisualPrefab != null, item.Id + " has no World Visual Prefab.");
                Require(visuals.Add(item.WorldVisualPrefab), item.Id + " shares a visual prefab with another item.");
                var filters = item.WorldVisualPrefab.GetComponentsInChildren<MeshFilter>(true);
                Require(filters.Length == 1 && filters[0].sharedMesh != null &&
                    filters[0].GetComponent<MeshRenderer>() != null,
                    item.Id + " visual must contain one MeshFilter and MeshRenderer.");
                var folder = item.Category == ItemCategory.Chest && item.Id.StartsWith("crate_", StringComparison.Ordinal)
                    ? "Box" : item.Category.ToString();
                var definitionPath = AssetDatabase.GetAssetPath(item).Replace('\\', '/');
                var visualPath = AssetDatabase.GetAssetPath(item.WorldVisualPrefab).Replace('\\', '/');
                Require(definitionPath.StartsWith($"Assets/_Project/Data/Items/{folder}/{item.Rarity}/",
                        StringComparison.Ordinal), item.Id + " definition is in the wrong category folder.");
                Require(visualPath.StartsWith($"Assets/_Project/Prefabs/Items/{folder}/{item.Rarity}/",
                        StringComparison.Ordinal), item.Id + " visual is in the wrong category folder.");
            }
            return items;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
