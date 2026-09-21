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
        }
    }
}
