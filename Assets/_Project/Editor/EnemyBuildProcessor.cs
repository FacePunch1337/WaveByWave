using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using WaveByWave.Enemies;

namespace WaveByWave.Editor
{
    public sealed class EnemyBuildProcessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -100;
        public void OnPreprocessBuild(BuildReport report)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(EnemyContentSetup.CatalogPath);
            if (catalog == null) throw new BuildFailedException("Skeleton enemy catalog is missing. Create it via Tools/Wave by Wave/Enemies.");
            if (!catalog.IsBaked || catalog.BakeSourceHash != EnemyContentSetup.SourceHash(catalog))
                EnemyContentSetup.Bake(catalog);
            if (catalog.UseCombinedVariants && catalog.CombinedSourceHash != EnemyCombinedVariantBaker.SourceHash(catalog))
                EnemyCombinedVariantBaker.Bake(catalog);
            foreach (var name in new[] { "Troll", "Shark", "Amphibian" })
            {
                var species = AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>($"{EnemySpeciesSetup.ProfileFolder}/{name}EnemyCatalog.asset");
                if (species == null) throw new BuildFailedException($"Missing {name} enemy catalog. Use Tools/Wave by Wave/Enemies/Create and bake troll, shark and amphibian.");
                if (!species.IsBaked || species.BakeSourceHash != EnemyContentSetup.SourceHash(species))
                    EnemyContentSetup.Bake(species);
            }
        }
    }
}
