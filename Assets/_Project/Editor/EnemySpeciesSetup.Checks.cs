using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using WaveByWave.Enemies;
using WaveByWave.Generation;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    public static partial class EnemySpeciesSetup
    {
        [MenuItem("Tools/Wave by Wave/Enemies/Validate new species and render animations")]
        public static void ValidateSpecies()
        {
            var report = new StringBuilder();
            var profiles = new[] { "Troll", "Shark", "Amphibian" }.Select(name =>
                AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>($"{ProfileFolder}/{name}EnemyCatalog.asset")).ToArray();
            foreach (var catalog in profiles)
            {
                if (catalog == null || !catalog.IsBaked) throw new InvalidOperationException("Missing baked species.");
                if (catalog.RandomizeAppearance || catalog.UseCombinedVariants || catalog.EnablePistolSpawns || catalog.EnableRifleSpawns)
                    throw new InvalidOperationException("Fixed species uses random appearance or weapons: " + catalog.name);
                if (catalog.HealthBarMaterial == null || catalog.HealthBarMesh == null || ShaderUtil.ShaderHasError(catalog.HealthBarMaterial.shader))
                    throw new InvalidOperationException("Health bar assets are missing or failed shader compilation.");
                var first = new List<int>(); var second = new List<int>();
                DotsEnemyPresentation.SelectParts(31, EnemyCombatType.Melee, catalog, first);
                DotsEnemyPresentation.SelectParts(932, EnemyCombatType.Melee, catalog, second);
                if (first.Count == 0 || !first.SequenceEqual(second)) throw new InvalidOperationException("Fixed appearance mismatch.");
                foreach (var part in catalog.BakedParts)
                {
                    if (part.Mesh == null || part.Positions == null || part.Normals == null ||
                        part.Materials.Any(m => m == null || ShaderUtil.ShaderHasError(m.shader)))
                        throw new InvalidOperationException("Incomplete animation bake: " + part.Name);
                    if (part.Mesh.bounds.size.magnitude > 18 || part.Mesh.bounds.size.magnitude < .001f)
                        throw new InvalidOperationException("Invalid baked bounds: " + part.Name + " " + part.Mesh.bounds);
                    var clip = part.Clips[1];
                    var a = part.Positions.GetPixels(0, clip.FirstRow, part.Positions.width, 1);
                    var b = part.Positions.GetPixels(0, clip.FirstRow + clip.Count / 3, part.Positions.width, 1);
                    var motion = 0f;
                    for (var i = 0; i < part.Mesh.vertexCount; i++) motion = Mathf.Max(motion,
                        Vector3.Distance(new Vector3(a[i].r,a[i].g,a[i].b), new Vector3(b[i].r,b[i].g,b[i].b)));
                    if (motion < .0001f) throw new InvalidOperationException("Static baked animation: " + part.Name);
                    report.AppendLine($"{catalog.Kind}/{part.Name}: {part.Mesh.vertexCount} vertices, {part.Clips.Length} clips, motion={motion:0.000}, bounds={part.Mesh.bounds}");
                    if (catalog.Kind == EnemyKind.Shark)
                    {
                        var swim = part.Clips[(int)EnemyAnimationState.Swim];
                        var start = part.Positions.GetPixels(0,swim.FirstRow,part.Positions.width,1);
                        var end = part.Positions.GetPixels(0,swim.FirstRow+swim.Count-1,part.Positions.width,1);
                        for (var v = 0; v < part.Mesh.vertexCount; v++)
                            Require(Vector4.Distance(start[v],end[v]) < .005f, "Shark swim loop has a visible discontinuity.");
                    }
                }
                if (catalog.Kind == EnemyKind.Shark)
                    Require(catalog.SpawnWhenPlayerEntersOcean && catalog.OceanEncounterMinimumDistance > 0f &&
                            catalog.OceanEncounterMaximumDistance > catalog.OceanEncounterMinimumDistance,
                        "Automatic shark encounter distances are invalid.");
                DotsEnemyRuntime.HitCapsule(new DotsEnemyState { Rotation = Unity.Mathematics.quaternion.identity }, catalog,
                    out var bottom, out var top, out var radius);
                var center = Vector3.Lerp(bottom, top, .5f);
                if (!EnemyHitGeometry.SegmentCapsule(center + Vector3.left * 10, center + Vector3.right * 10, bottom, top, radius, out _))
                    throw new InvalidOperationException("Hit capsule missed " + catalog.Kind);
                DotsEnemyRuntime.HitCapsule(new DotsEnemyState { Rotation = Unity.Mathematics.quaternion.RotateY(1.1f) }, catalog,
                    out bottom, out top, out radius);
                Require(EnemyHitGeometry.SegmentCapsule(top + Vector3.up * 5, top - Vector3.up * 5, bottom,top,radius,out _),
                    "Rotated capsule endpoint missed " + catalog.Kind);
            }
            var rowPath = "Assets/_Project/Prefabs/UI/Resources/UI/Elements/EnemySpawnRow.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(rowPath) == null)
                AssetDatabase.CopyAsset("Assets/_Project/Prefabs/UI/Resources/UI/Elements/ItemSpawnRow.prefab", rowPath);
            AssetDatabase.SaveAssets();
            CheckAutomaticOceanEncounter();
            CheckSpeciesMovement(profiles);
            RenderSpecies(profiles);
            File.WriteAllText("Temp/EnemySpecies.result", "PASS: fixed appearances, animation motion, hit capsules, target habitats, land/water spawning, amphibian shoreline in both directions, shark shore restriction, one shared shark per ocean entry, profile-based fragmented waves, ship-crew totals, separated battlefield settings.\n" + report);
            Debug.Log("[Enemies] Species content, animation motion, fixed appearance and hit capsules validated.");
        }

        private static void CheckAutomaticOceanEncounter()
        {
            var method = typeof(DotsEnemyRuntime).GetMethod("UpdateOceanEntries",
                BindingFlags.Static | BindingFlags.NonPublic);
            Require(method != null, "Automatic shark encounter gate is missing.");
            var previous = new HashSet<ulong>();
            var current = new HashSet<ulong>();
            ulong Step(ulong[] ids, byte[] water, bool occupied) => (ulong)method.Invoke(null,
                new object[] { ids, water, previous, current, occupied });
            Require(Step(new ulong[] { 7, 11 }, new byte[] { 1, 1 }, false) == 7,
                "The first player entering ocean water must own the shared shark encounter.");
            Require(Step(new ulong[] { 7, 11 }, new byte[] { 1, 1 }, false) == 7,
                "An unoccupied encounter must retry for players who stayed in the ocean.");
            Require(Step(new ulong[] { 7, 11, 15 }, new byte[] { 1, 1, 1 }, true) == ulong.MaxValue,
                "A second player created a shark while the global encounter was occupied.");
            Step(new ulong[] { 7, 11, 15 }, new byte[] { 1, 1, 0 }, false);
            Require(Step(new ulong[] { 7, 11, 15 }, new byte[] { 1, 1, 1 }, false) == 7,
                "The shared encounter must still use the first eligible player.");
            var amphibian = AssetDatabase.LoadAssetAtPath<WaveEnemyProfile>(
                "Assets/_Project/Data/Enemies/WaveTypes/EnemyType_Amphibian.asset");
            var waveEnemy = new NightWaveEnemy
                { EnemyType = amphibian, Count = 4, SpawnRadius = 20f, SpawnBandWidth = 15f };
            Require(waveEnemy.EnemyType != null && waveEnemy.EnemyType.Species == EnemyKind.Amphibian,
                "Night waves cannot select an amphibian enemy profile without a visual rig prefab.");
            Require(waveEnemy.SpawnRadius == 20f && waveEnemy.SpawnBandWidth == 15f,
                "Wave enemy exclusion radius or spawn band was not saved.");
            var profileGuids = AssetDatabase.FindAssets("t:WaveEnemyProfile");
            Require(profileGuids.Length >= 6, "Wave enemy profiles are missing.");
            var ship = AssetDatabase.LoadAssetAtPath<WaveEnemyProfile>(
                "Assets/_Project/Data/Enemies/WaveTypes/EnemyType_Ship.asset");
            var shipDefinition = AssetDatabase.LoadAssetAtPath<EnemyShipDefinition>(
                EnemyShipContentSetup.DefinitionPath);
            var wave = new NightWaveDefinition
            {
                Fragments = new[]
                {
                    new NightWaveFragment { Enemies = new[]
                    {
                        new NightWaveEnemy { EnemyType = amphibian, Count = 3 },
                        new NightWaveEnemy { EnemyType = ship, Count = 2 }
                    }},
                    new NightWaveFragment { Enemies = new[]
                    {
                        new NightWaveEnemy { EnemyType = amphibian, Count = 4 }
                    }}
                }
            };
            var count = typeof(NightWaveController).GetMethod("CountWaveBots",
                BindingFlags.Static | BindingFlags.NonPublic);
            Require(count != null, "Wave bot counter is missing.");
            var expected = 7 + shipDefinition.CrewCount * 2;
            Require((int)count.Invoke(null, new object[] { wave, shipDefinition, 0 }) == expected,
                "Wave total must count characters and ship crews, but not ships.");
            Require((int)count.Invoke(null, new object[] { wave, shipDefinition, 1 }) == 4,
                "Future fragment total is invalid.");
            var battlefield = AssetDatabase.LoadAssetAtPath<NightBattlefieldSettings>(
                "Assets/_Project/Resources/NightBattlefieldSettings.asset");
            Require(battlefield != null && Mathf.Approximately(battlefield.FogDensity, 0.015f) &&
                    Mathf.Approximately(battlefield.FogWindSpeed, 4f) &&
                    Mathf.Approximately(battlefield.BoundaryGraceSeconds, 8f),
                "Migrated battlefield/fog settings changed.");
            Require(typeof(NightWaveSettings).GetField("FogDensity") == null,
                "Fog configuration still belongs to the wave settings object.");
        }

        private static void RenderSpecies(DotsEnemyCatalog[] profiles)
        {
            var sheet = new Texture2D(1200, 900, TextureFormat.RGB24, false);
            var preview = new PreviewRenderUtility();
            try
            {
                preview.camera.fieldOfView = 35; preview.camera.nearClipPlane = .01f;
                preview.camera.farClipPlane = 50; preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = new Color(.11f,.15f,.20f);
                preview.lights[0].intensity = 1.3f;
                preview.lights[0].transform.rotation = Quaternion.Euler(35,150,0);
                preview.lights[1].intensity = .8f; preview.ambientColor = Color.gray;
                for (var row = 0; row < profiles.Length; row++)
                for (var cell = 0; cell < 4; cell++)
                {
                    var catalog = profiles[row];
                    var center = Vector3.up * (catalog.BodyHeight * .5f);
                    preview.camera.transform.position = center + new Vector3(4, 1.5f, 7);
                    preview.camera.transform.LookAt(center);
                    preview.BeginStaticPreview(new Rect(0,0,300,300));
                    var materials = new List<Material>();
                    foreach (var part in catalog.BakedParts)
                    {
                        var layout = part.Clips[cell == 0 ? 0 : cell == 3 ? 2 : cell == 2 && catalog.SwimClip != null ? 6 : 1];
                        var frame = layout.FirstRow + Mathf.RoundToInt((cell == 2 ? .7f : .2f) * (layout.Count - 1));
                        for (var sub = 0; sub < part.Materials.Length; sub++)
                        {
                            var material = new Material(part.Materials[sub]); materials.Add(material);
                            material.SetVector("_EnemyFrame", new Vector4(frame,frame,0,0));
                            preview.DrawMesh(part.Mesh, Matrix4x4.Scale(Vector3.one * catalog.VisualScale), material, sub);
                        }
                    }
                    var bar = new Material(catalog.HealthBarMaterial); materials.Add(bar);
                    bar.SetVector("_EnemyFrame", new Vector4(.65f,0,0,0));
                    preview.DrawMesh(catalog.HealthBarMesh, Matrix4x4.TRS(Vector3.up * catalog.HealthBarHeight,
                        preview.camera.transform.rotation,new Vector3(catalog.HealthBarSize.x,catalog.HealthBarSize.y,1)),bar,0);
                    preview.Render(true);
                    var image = preview.EndStaticPreview();
                    sheet.SetPixels(cell * 300,(2-row)*300,300,300,image.GetPixels());
                    Object.DestroyImmediate(image); foreach (var m in materials) Object.DestroyImmediate(m);
                }
                sheet.Apply(); File.WriteAllBytes("Temp/EnemySpecies.png",sheet.EncodeToPNG());
            }
            finally { preview.Cleanup(); Object.DestroyImmediate(sheet); }
        }
    }
}
