using System;
using UnityEditor;
using UnityEngine;
using WaveByWave.Enemies;

namespace WaveByWave.Editor
{
    // Explicit editor authoring only: no prefab changes or baking during gameplay.
    public static class EnemyOptimizationSetup
    {
        [MenuItem("Tools/Wave by Wave/Enemies/Bake crowd optimization assets")]
        public static void BakeProjectAssets()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play Mode first.");
            var catalog = AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(EnemyContentSetup.CatalogPath);
            if (catalog == null) throw new InvalidOperationException("Skeleton catalog is missing.");
            if (!catalog.IsBaked || catalog.BakeSourceHash != EnemyContentSetup.SourceHash(catalog)) EnemyContentSetup.Bake(catalog);
            if (catalog.CombinedSourceHash != EnemyCombinedVariantBaker.SourceHash(catalog)) EnemyCombinedVariantBaker.Bake(catalog);
            BakeShip("Assets/_Project/Prefabs/Enemies/EnemyShip.prefab", "Assets/_Project/Data/Enemies/EnemyShipNavigation.asset", catalog);
            BakeShip("Assets/_Project/Prefabs/Ship.prefab", "Assets/_Project/Data/Enemies/PlayerShipNavigation.asset", catalog);
            AssetDatabase.SaveAssets();
        }

        private static void BakeShip(string prefabPath, string dataPath, DotsEnemyCatalog catalog)
        {
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var nav = root.GetComponent<EnemyDeckNavigation>();
                if (nav == null)
                {
                    nav = root.AddComponent<EnemyDeckNavigation>();
                    var scale = Mathf.Max(0.001f, Mathf.Min(Mathf.Abs(root.transform.lossyScale.x),
                        Mathf.Abs(root.transform.lossyScale.y), Mathf.Abs(root.transform.lossyScale.z)));
                    nav.AgentRadius = catalog.BodyRadius / scale;
                    nav.AgentHeight = catalog.BodyHeight / scale;
                    nav.MaximumSlope = catalog.MaximumSlope;
                    nav.StepHeight = Mathf.Min(0.45f, catalog.StepHeight / scale);
                    nav.MaximumDrop = Mathf.Min(2f, catalog.MaximumDrop / scale);
                }
                if (nav.Data == null || nav.Data.SourceHash != EnemyDeckNavigationBaker.SourceHash(nav))
                    EnemyDeckNavigationBaker.Bake(nav, dataPath);
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
