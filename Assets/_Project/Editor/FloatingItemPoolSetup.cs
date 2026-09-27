using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WaveByWave.Generation;
using WaveByWave.Items;

namespace WaveByWave.Editor
{
    [InitializeOnLoad]
    public static class FloatingItemPoolSetup
    {
        public const string PoolPath = "Assets/_Project/Data/Ocean/FloatingItemPool.asset";
        private const string SettingsPath = "Assets/_Project/Resources/OceanGeneration.asset";
        private const string RequestPath = "Temp/FloatingItemPoolSetup.request";
        private const string ResultPath = "Temp/FloatingItemPoolSetup.result";

        static FloatingItemPoolSetup() => EditorApplication.update += InstallWhenReady;

        [MenuItem("Tools/Wave by Wave/Items/Create or migrate floating item pool")]
        public static void EnsureMenu()
        {
            try
            {
                var pool = Ensure();
                if (pool != null) Selection.activeObject = pool;
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
        }

        public static FloatingItemPool Ensure()
        {
            var settings = AssetDatabase.LoadAssetAtPath<OceanGenerationSettings>(SettingsPath);
            if (settings == null) return null;
            var pool = AssetDatabase.LoadAssetAtPath<FloatingItemPool>(PoolPath);
            if (pool == null)
            {
                pool = ScriptableObject.CreateInstance<FloatingItemPool>();
                AssetDatabase.CreateAsset(pool, PoolPath);
            }

            var settingsObject = new SerializedObject(settings);
            settingsObject.Update();
            var poolProperty = settingsObject.FindProperty("floatingItemPool");
            var legacyProperty = settingsObject.FindProperty("legacyFloatingObjects");
            var changed = false;
            if (legacyProperty != null && legacyProperty.arraySize > 0)
            {
                if (pool.Items.Count == 0)
                {
                    for (var i = 0; i < legacyProperty.arraySize; i++)
                    {
                        var source = legacyProperty.GetArrayElementAtIndex(i);
                        pool.Items.Add(new WeightedItemEntry
                        {
                            Item = source.FindPropertyRelative("Item").objectReferenceValue as ItemDefinition,
                            Weight = source.FindPropertyRelative("Weight").floatValue
                        });
                    }
                    EditorUtility.SetDirty(pool);
                }
                else
                {
                    for (var i = 0; i < legacyProperty.arraySize; i++)
                    {
                        var source = legacyProperty.GetArrayElementAtIndex(i);
                        var item = source.FindPropertyRelative("Item").objectReferenceValue as ItemDefinition;
                        if (item == null || pool.Items.Any(entry => entry?.Item == item)) continue;
                        pool.Items.Add(new WeightedItemEntry
                        {
                            Item = item,
                            Weight = source.FindPropertyRelative("Weight").floatValue
                        });
                        changed = true;
                    }
                    if (changed) EditorUtility.SetDirty(pool);
                }
                legacyProperty.ClearArray();
                changed = true;
            }
            if (poolProperty.objectReferenceValue != pool)
            {
                poolProperty.objectReferenceValue = pool;
                changed = true;
            }
            if (changed)
            {
                settingsObject.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }
            Require(settings.FloatingItemPoolAsset == pool, "Ocean Generation Settings is not linked to FloatingItemPool.");
            Require(settings.FloatingObjects.Count == pool.Items.Count, "Floating item migration lost entries.");
            return pool;
        }

        private static void InstallWhenReady()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (File.Exists(RequestPath))
            {
                File.Delete(RequestPath);
                try
                {
                    var pool = Ensure();
                    File.WriteAllText(ResultPath,
                        pool != null ? $"PASS: FloatingItemPool contains {pool.Items.Count} entries." : "FAIL: OceanGeneration settings are missing.");
                }
                catch (Exception error)
                {
                    File.WriteAllText(ResultPath, "FAIL: " + error);
                    Debug.LogException(error);
                }
                return;
            }
            EditorApplication.update -= InstallWhenReady;
            try { Ensure(); }
            catch (Exception error) { Debug.LogException(error); }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
