using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WaveByWave.Enemies;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    public static partial class EnemySpeciesSetup
    {
        [MenuItem("Tools/Wave by Wave/Enemies/Create and bake troll, shark and amphibian")]
        public static void CreateAndBake()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play Mode before baking.");
            Directory.CreateDirectory(Folder); Directory.CreateDirectory(PrefabFolder); Directory.CreateDirectory(ProfileFolder);
            AssetDatabase.Refresh();
            var skeleton = AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(EnemyContentSetup.CatalogPath);
            if (skeleton == null) throw new InvalidOperationException("Skeleton catalog is missing.");
            EnsureHealthBar(skeleton);
            var troll = Profile("Troll", EnemyKind.Troll, out var newTroll);
            if (newTroll)
            {
                Defaults(troll, skeleton, TrollSource);
                troll.DisplayName = "Тролль-людоед"; troll.Habitat = EnemyHabitat.Land;
                var clips = AssetDatabase.LoadAllAssetsAtPath("Assets/Troll_Сannibal/Fbx/Troll_cannibal.fbx").OfType<AnimationClip>().ToArray();
                troll.IdleClip = clips.First(c => c.name == "idle battl");
                troll.RunClip = clips.First(c => c.name == "run");
                troll.MeleeAttackClip = clips.First(c => c.name == "attack 1");
                troll.PistolAttackClip = troll.RifleAttackClip = troll.MeleeAttackClip;
                troll.MaximumHealth = 400; troll.MeleeDamage = 38; troll.MeleeRange = 2.6f;
                troll.MeleeCooldown = 2; troll.MoveSpeed = 2.8f; troll.VisualScale = 1.65f;
                troll.BodyHeight = 3; troll.BodyRadius = 0.8f; troll.ProjectileHitRadius = 0.8f;
                troll.DamageKnockback = 0.8f; troll.HealthBarHeight = 3.4f; troll.HealthBarSize = new Vector2(1.4f, 0.14f);
            }
            var amphibian = Profile("Amphibian", EnemyKind.Amphibian, out var newAmphibian);
            if (newAmphibian)
            {
                Defaults(amphibian, skeleton, "Assets/Pirates/Prefabs/Characters/skeleton_01.prefab");
                amphibian.DisplayName = "Земноводный"; amphibian.Habitat = EnemyHabitat.Amphibious;
                amphibian.IdleClip = skeleton.IdleClip; amphibian.RunClip = skeleton.RunClip;
                amphibian.MeleeAttackClip = skeleton.MeleeAttackClip;
                amphibian.PistolAttackClip = amphibian.RifleAttackClip = amphibian.MeleeAttackClip;
                amphibian.SwimClip = AssetDatabase.LoadAllAssetsAtPath("Assets/Pirates/Animations/generic/pirate_swim.FBX")
                    .OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__"));
                amphibian.VisualScale = skeleton.VisualScale;
                amphibian.MaximumHealth = 90; amphibian.MeleeDamage = 16;
                amphibian.SwimmingDepth = 1.05f; amphibian.MinimumWaterDepth = 1.1f; amphibian.SwimSpeed = 3.5f;
                amphibian.BakingRigPrefab = CleanSkeleton(amphibian.BakingRigPrefab);
                amphibian.PartSources[0].Prefab = amphibian.BakingRigPrefab;
            }
            var shark = Profile("Shark", EnemyKind.Shark, out var newShark);
            if (newShark)
            {
                var rig = CreateSharkRig(out var idle, out var swim, out var attack);
                Defaults(shark, skeleton, AssetDatabase.GetAssetPath(rig));
                shark.DisplayName = "Акула"; shark.Habitat = EnemyHabitat.Water;
                shark.IdleClip = idle; shark.RunClip = shark.SwimClip = swim;
                shark.MeleeAttackClip = shark.PistolAttackClip = shark.RifleAttackClip = attack;
                shark.VisualScale = 1; shark.MaximumHealth = 160; shark.MeleeDamage = 28;
                shark.MoveSpeed = shark.SwimSpeed = 5.2f; shark.MeleeRange = 2.2f; shark.MeleeCooldown = 1.6f;
                shark.BodyHeight = 1.3f; shark.BodyRadius = 0.55f; shark.ProjectileHitRadius = 0.55f;
                shark.CustomHitCapsule = true;
                shark.HitCapsuleStart = new Vector3(0, 0.55f, -1.25f);
                shark.HitCapsuleEnd = new Vector3(0, 0.55f, 1.15f);
                shark.SwimmingDepth = 1.6f; shark.MinimumWaterDepth = 1.8f;
                shark.HealthBarHeight = 1.6f; shark.HealthBarSize = new Vector2(1.25f, 0.12f);
            }
            foreach (var catalog in new[] { troll, shark, amphibian })
            {
                EnsureHealthBar(catalog);
                EditorUtility.SetDirty(catalog);
                EnemyContentSetup.Bake(catalog);
                SpawnPrefab(catalog);
            }
            AssetDatabase.SaveAssets();
            ValidateSpecies();
        }

        private static DotsEnemyCatalog Profile(string name, EnemyKind kind, out bool created)
        {
            var path = $"{ProfileFolder}/{name}EnemyCatalog.asset";
            var catalog = AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(path);
            created = catalog == null;
            if (!created) return catalog;
            catalog = ScriptableObject.CreateInstance<DotsEnemyCatalog>(); catalog.Kind = kind;
            AssetDatabase.CreateAsset(catalog, path);
            return catalog;
        }

        private static void Defaults(DotsEnemyCatalog catalog, DotsEnemyCatalog skeleton, string prefab)
        {
            catalog.BakingRigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefab);
            if (catalog.BakingRigPrefab == null) throw new InvalidOperationException("Missing source " + prefab);
            catalog.RandomizeAppearance = false; catalog.UseCombinedVariants = false;
            catalog.EnableMeleeSpawns = true; catalog.EnablePistolSpawns = catalog.EnableRifleSpawns = false;
            catalog.PartSources.Add(new EnemyPartSource { Category = EnemyBakedPartCategory.Body, Prefab = catalog.BakingRigPrefab });
            catalog.SkeletonMaterials = Array.Empty<Material>();
            catalog.BakeFramesPerSecond = 16;
            catalog.WaterProfile = skeleton.WaterProfile;
            catalog.LootDrops = skeleton.LootDrops;
            catalog.DeathSmokePrefab = skeleton.DeathSmokePrefab;
            catalog.SpawnSmokePrefab = skeleton.SpawnSmokePrefab;
            catalog.StunEffectPrefab = skeleton.StunEffectPrefab;
        }

        private static GameObject CleanSkeleton(GameObject source)
        {
            var path = PrefabFolder + "/AmphibianRig.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;
            var rig = Object.Instantiate(source);
            try
            {
                rig.name = "AmphibianRig";
                var bodyParts = new[] { "Skeleton", "Hand_Left", "Hand_Right", "Leg_Left", "Leg_Right" };
                foreach (var renderer in rig.GetComponentsInChildren<Renderer>(true))
                    if (!bodyParts.Contains(renderer.name)) Object.DestroyImmediate(renderer.gameObject);
                foreach (var animator in rig.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(animator);
                foreach (var collider in rig.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
                return PrefabUtility.SaveAsPrefabAsset(rig, path);
            }
            finally { Object.DestroyImmediate(rig); }
        }

        private static void SpawnPrefab(DotsEnemyCatalog catalog)
        {
            var path = $"{PrefabFolder}/{catalog.Kind}Spawn.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return;
            var go = new GameObject(catalog.Kind + "Spawn", typeof(EnemySpawnPoint));
            var point = go.GetComponent<EnemySpawnPoint>(); point.Kind = catalog.Kind;
            point.CombatType = EnemyCombatType.Melee; point.Count = 1; point.SpawnRadius = 4;
            PrefabUtility.SaveAsPrefabAsset(go, path); Object.DestroyImmediate(go);
        }

        private static void EnsureHealthBar(DotsEnemyCatalog catalog)
        {
            const string path = Folder + "/HealthBar.asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null)
            {
                mesh = new Mesh { name = "Enemy health bar" };
                mesh.vertices = new[] { new Vector3(-.5f,-.5f,0), new Vector3(.5f,-.5f,0), new Vector3(.5f,.5f,0), new Vector3(-.5f,.5f,0) };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 }; mesh.RecalculateBounds();
                AssetDatabase.CreateAsset(mesh, path);
            }
            var material = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>().FirstOrDefault();
            if (material == null)
            {
                var shader = Shader.Find("WaveByWave/EnemyHealthBar");
                if (shader == null) throw new InvalidOperationException("Enemy health bar shader missing.");
                material = new Material(shader) { name = "EnemyHealthBar", enableInstancing = true };
                AssetDatabase.AddObjectToAsset(material, path);
            }
            if (catalog.HealthBarMesh == null) catalog.HealthBarMesh = mesh;
            if (catalog.HealthBarMaterial == null) catalog.HealthBarMaterial = material;
            EditorUtility.SetDirty(catalog);
        }
    }
}
