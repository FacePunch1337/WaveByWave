using System;
using System.IO;
using System.Linq;
using System.Reflection;
using StylizedWater3;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WaveByWave.Combat;
using WaveByWave.Enemies;
using WaveByWave.Generation;
using WaveByWave.Items;
using WaveByWave.Player;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    internal static class BombChecks
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [InitializeOnLoadMethod] private static void Install() => EditorApplication.update += Poll;
        private static void Poll()
        {
            if (!File.Exists("Temp/BombChecks.request") || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (File.GetLastWriteTimeUtc("Assets/_Project/Editor/BombChecks.cs") > File.GetLastWriteTimeUtc(typeof(BombChecks).Assembly.Location))
            { AssetDatabase.Refresh(); return; }
            File.Delete("Temp/BombChecks.request");
            try { Run(); File.WriteAllText("Temp/BombChecks.result", "PASS: content/loot, deterministic crater replay, unchanged bedrock, DOTS flight, combat trigger hits for both teams, one blast hit per actor, generic trigger exclusion, underwater drops and crew death accounting."); }
            catch (Exception error) { File.WriteAllText("Temp/BombChecks.result", "FAIL: " + error); Debug.LogException(error); }
        }

        [MenuItem("Tools/Wave by Wave/Checks/Bombs and Cannon Hits")]
        private static void Run()
        {
            CheckContent(); CheckCrater(); CheckBallistics(); CheckPhysicsAndWater();
        }
        private static void Require(bool value, string reason)
        { if (!value) throw new InvalidOperationException(reason); }

        private static void CheckContent()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/Data/ItemCatalog.asset");
            Require(catalog.TryGet("bomb", out var bomb) && bomb.IsWeapon && bomb.IsCannonAmmo && bomb.Rarity == ItemRarity.Common,
                "Common bomb is a weapon and cannon ammunition");
            Require(bomb.Icon != null && bomb.Throwable != null && bomb.Throwable.FusePrefab != null && bomb.Throwable.ExplosionPrefab != null,
                "Bomb icon and both authored effects assigned");
            var mesh = bomb.WorldVisualPrefab.GetComponent<MeshFilter>().sharedMesh;
            Require(AssetDatabase.GetAssetPath(mesh).EndsWith("SM_CANNONBALL.fbx"), "Bomb uses the requested mesh");
            var size = Vector3.Scale(mesh.bounds.size, bomb.WorldVisualPrefab.transform.localScale);
            Require(Mathf.Abs(Mathf.Max(size.x, size.y, size.z) - .28f) < .001f, "Bomb's presentation matches projectile diameter");
            Require(catalog.TryGet("crate_bombs", out var box) && box.IsChest && box.Rarity == ItemRarity.Common && box.Icon != null,
                "Common bomb crate registered with an icon");
            var random = new Unity.Mathematics.Random(42);
            var rewards = new ChestReward[ChestLootTable.MaximumRewards];
            var count = box.ChestLoot.BuildRewards(box.ChestLoot.FindTier(ItemRarity.Common), ref random, 0, rewards);
            Require(count == 1 && rewards[0].Item == bomb && rewards[0].Amount >= 4 && rewards[0].Amount <= 8,
                "Bomb crate gives 4–8 bombs");
            var pool = AssetDatabase.LoadAssetAtPath<FloatingItemPool>("Assets/_Project/Data/Ocean/FloatingItemPool.asset");
            Require(pool.Items.Count(x => x.Item == bomb) == 1 && pool.Items.Count(x => x.Item == box) == 1, "Floating pool contains each new item once");
            Require(ProjectileExplosion.DamageAt(40, 0, 4) == 40 && ProjectileExplosion.DamageAt(40, 5, 4) == 0,
                "Blast damage limited to radius");
        }

        private static void CheckCrater()
        {
            const int side = 21;
            using var a = new NativeArray<float2>(side * side * side, Allocator.Temp);
            using var b = new NativeArray<float2>(a.Length, Allocator.Temp);
            using var round = new NativeArray<float2>(a.Length, Allocator.Temp);
            Fill(a); Fill(b); Fill(round);
            var job = new IslandDigJob { Parameters = new IslandFieldParameters { Points = side, Origin = -5f, CellSize = .5f },
                Density = a, Center = 0, Radius = 3, NoiseStrength = .3f, NoiseSeed = 57, Smoothing = .2f, Minimum = 0, Maximum = side - 1 };
            job.Execute(); job.Density = b; job.Execute(); job.Density = round; job.NoiseStrength = 0; job.Execute();
            var differs = false;
            for (var i = 0; i < a.Length; i++)
            {
                Require(math.all(a[i] == b[i]), "Same blast seed produces identical voxel edits for late join");
                Require(a[i].y == -.75f, "Explosion preserves the existing bedrock rule");
                differs |= math.abs(a[i].x - round[i].x) > .02f;
            }
            Require(differs && a[0].x == 1 && a[(a.Length - 1) / 2].x < -2, "Noisy crater carves its center and leaves distant voxels intact");
        }

        private static void Fill(NativeArray<float2> density)
        { for (var i = 0; i < density.Length; i++) density[i] = new float2(1, -.75f); }

        private static void CheckBallistics()
        {
            var type = typeof(DotsCannonProjectile).Assembly.GetType("WaveByWave.Combat.MoveCannonProjectiles", true);
            var job = Activator.CreateInstance(type); type.GetField("DeltaTime").SetValue(job, .02f);
            var execute = type.GetMethod("Execute", Private);
            var ball = new DotsCannonProjectile { Position = new float3(2, 4, 6), Velocity = new float3(3, 12, 8), Gravity = new float3(0, -12, 0) };
            var start = ball.Position; var velocity = ball.Velocity;
            for (var i = 0; i < 50; i++) { object[] args = { ball }; execute.Invoke(job, args); ball = (DotsCannonProjectile)args[0]; }
            Require(math.distance(ball.Position, start + velocity + new float3(0, -6, 0)) < .001f, "DOTS flight agrees with the visual analytic trajectory");
        }

        private static void CheckPhysicsAndWater()
        {
            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var origin = new Vector3(30000, 10000, 30000);
            var material = new Material(AssetDatabase.LoadAssetAtPath<Material>("Assets/Stylized Water 3/Materials/StylizedWater3_Ocean.mat"));
            material.DisableKeyword(ShaderParams.Keywords.Waves);
            var water = new EquipmentWaterQuery(null);
            var oldWater = typeof(LootStressTest).GetField("_water", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            using var world = new World("Bomb collision checks");
            try
            {
                var system = world.GetOrCreateSystemManaged<DotsCannonProjectileSystem>();
                var impact = typeof(DotsCannonProjectileSystem).GetMethod("TryImpact", Private);
                var trigger = New(scene, "Interaction trigger", origin + Vector3.forward);
                trigger.AddComponent<BoxCollider>().isTrigger = true;
                var target = New(scene, "Combat trigger", origin + Vector3.forward * 3);
                target.AddComponent<EquipmentHitbox>(); var collider = target.AddComponent<BoxCollider>(); collider.isTrigger = true;
                Physics.SyncTransforms();
                foreach (var team in new byte[] { 0, 1 })
                {
                    var ball = new DotsCannonProjectile { Previous = origin, Position = origin + Vector3.forward * 5,
                        Radius = .1f, Age = .3f, EnteredWater = 1, EnemyTeam = team };
                    object[] args = { ball, null, null, false, (byte)0, 0, null, null };
                    Require((bool)impact.Invoke(system, args) && (byte)args[4] == 3 && (Collider)args[6] == collider,
                        "Both cannon teams hit combat triggers and ignore interaction triggers");
                }
                Object.DestroyImmediate(trigger); Object.DestroyImmediate(target);
                var blastTarget = New(scene, "Blast receiver", origin + Vector3.forward * 3);
                var probe = blastTarget.AddComponent<BombDamageProbe>();
                blastTarget.AddComponent<BoxCollider>();
                var closest = New(scene, "Closer body part", origin + Vector3.forward * 2);
                closest.transform.SetParent(blastTarget.transform, true);
                closest.AddComponent<BoxCollider>();
                var interaction = New(scene, "Large pickup volume", origin);
                interaction.transform.SetParent(blastTarget.transform, true);
                interaction.AddComponent<BoxCollider>().isTrigger = true;
                Physics.SyncTransforms();
                typeof(ProjectileExplosion).GetMethod("DamagePhysics", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { origin, 4f, 40f });
                Require(probe.Hits == 1 && Mathf.Abs(probe.Damage - ProjectileExplosion.DamageAt(40, 1.5f, 4)) < .001f,
                    "An actor receives one blast hit from its closest body part; pickup triggers do not increase damage");
                Object.DestroyImmediate(blastTarget);
                var ocean = GameObject.CreatePrimitive(PrimitiveType.Cube); SceneManager.MoveGameObjectToScene(ocean, scene);
                ocean.transform.position = origin; ocean.transform.localScale = new Vector3(100, .01f, 100);
                Object.DestroyImmediate(ocean.GetComponent<Collider>()); ocean.GetComponent<MeshRenderer>().sharedMaterial = material;
                var surface = ocean.AddComponent<WaterObject>(); surface.meshRenderer = ocean.GetComponent<MeshRenderer>();
                surface.meshFilter = ocean.GetComponent<MeshFilter>(); surface.material = material;
                Physics.SyncTransforms();
                typeof(LootStressTest).GetField("_water", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, water);
                object[] dropArgs = { origin - Vector3.up * 1.2f, null, null, false, null, 0UL, true };
                // Seven arguments: origin, point, normal, water, support, surfaceId, allowWaterAboveOrigin.
                var resolve = typeof(LootStressTest).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                    .Single(m => m.Name == "TryResolveSurface" && m.GetParameters().Length == dropArgs.Length);
                Require((bool)resolve.Invoke(null, dropArgs) && (bool)dropArgs[3] && Mathf.Abs(((Vector3)dropArgs[1]).y - origin.y) < .03f,
                    "Swimming drops can resolve a water surface above their feet");
                var runtimeObject = New(scene, "Inactive crew fixture", origin); runtimeObject.SetActive(false);
                var runtime = runtimeObject.AddComponent<DotsEnemyRuntime>();
                typeof(DotsEnemyRuntime).GetField("_water", Private).SetValue(runtime, water);
                var submerged = typeof(DotsEnemyRuntime).GetMethod("CrewSubmerged", Private);
                Require((bool)submerged.Invoke(runtime, new object[] { origin - Vector3.up }) &&
                    !(bool)submerged.Invoke(runtime, new object[] { origin + Vector3.up }), "Crew drowning checks the head against water");
                var register = typeof(DotsEnemyRuntime).GetMethod("RegisterCrewMember", Private);
                var death = typeof(DotsEnemyRuntime).GetMethod("RecordCrewDeath", Private);
                register.Invoke(runtime, new object[] { 17 }); register.Invoke(runtime, new object[] { 17 });
                object[] first = { new DotsEnemyBrain { CrewShipId = 17 } };
                object[] last = { new DotsEnemyBrain { CrewShipId = 17 } };
                Require((int)death.Invoke(runtime, first) == 0 && (int)death.Invoke(runtime, last) == 17 &&
                    (int)death.Invoke(runtime, last) == 0, "Final crew death releases ship once, with no double decrement");
                typeof(DotsEnemyRuntime).GetField("_water", Private).SetValue(runtime, null);
            }
            finally
            {
                typeof(LootStressTest).GetField("_water", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, oldWater);
                water.Dispose(); Object.DestroyImmediate(material);
                EditorSceneManager.CloseScene(scene, true); SceneManager.SetActiveScene(previous);
            }
        }
        private static GameObject New(Scene scene, string name, Vector3 position)
        { var go = new GameObject(name); SceneManager.MoveGameObjectToScene(go, scene); go.transform.position = position; return go; }
    }

    internal sealed class BombDamageProbe : MonoBehaviour, IEquipmentDamageReceiver
    {
        public int Hits;
        public float Damage;
        public void ReceiveEquipmentHitServer(float damage, Vector3 attackerPosition, bool canBlock = true, Vector3? impactPoint = null)
        { Hits++; Damage += damage; }
    }
}
