using System;
using System.Reflection;
using StylizedWater3;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WaveByWave.Enemies;
using WaveByWave.Player;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    public static partial class EnemySpeciesSetup
    {
        private static void Require(bool success, string reason)
        { if (!success) throw new InvalidOperationException(reason); }

        private static void CheckSpeciesMovement(DotsEnemyCatalog[] profiles)
        {
            CheckSpeciesTargets();
            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var origin = new Vector3(20000, 10000, 20000);
            var root = new GameObject("Species movement checks"); root.SetActive(false);
            var ocean = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var shore = new GameObject("Shore", typeof(MeshCollider));
            var mesh = new Mesh();
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            var water = new EquipmentWaterQuery(null);
            var navigation = ScriptableObject.CreateInstance<DotsEnemyCatalog>();
            try
            {
                foreach (var go in new[] { root, ocean, shore }) SceneManager.MoveGameObjectToScene(go, scene);
                var runtime = root.AddComponent<DotsEnemyRuntime>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(DotsEnemyRuntime).GetProperty("Catalog").SetValue(runtime, navigation);
                typeof(DotsEnemyRuntime).GetField("_water", flags).SetValue(runtime, water);
                var catalogs = (DotsEnemyCatalog[])typeof(DotsEnemyRuntime).GetField("_species", flags).GetValue(runtime);
                foreach (var catalog in profiles) catalogs[(int)catalog.Kind] = catalog;
                ocean.transform.position = origin; ocean.transform.localScale = new Vector3(100, .01f, 100);
                Object.DestroyImmediate(ocean.GetComponent<Collider>());
                ocean.GetComponent<MeshRenderer>().sharedMaterial = material;
                var surface = ocean.AddComponent<WaterObject>();
                surface.meshRenderer = ocean.GetComponent<MeshRenderer>(); surface.meshFilter = ocean.GetComponent<MeshFilter>();
                surface.material = material;
                // Continuous beach: x=-8 at y=-2.4; x=8 at y=0.8. Waterline is x=4.
                mesh.vertices = new[] { new Vector3(-8,-2.4f,-20), new Vector3(-8,-2.4f,20),
                    new Vector3(8,.8f,-20), new Vector3(8,.8f,20) };
                mesh.triangles = new[] { 0,1,2,2,1,3 }; mesh.RecalculateNormals(); mesh.RecalculateBounds();
                shore.GetComponent<MeshCollider>().sharedMesh = mesh; shore.transform.position = origin;
                Physics.SyncTransforms();
                var spawn = typeof(DotsEnemyRuntime).GetMethod("TrySpawnPosition", flags);
                var move = typeof(DotsEnemyRuntime).GetMethod("MoveInWater", flags);
                var ground = typeof(DotsEnemyRuntime).GetMethod("TryGround", flags);
                bool Spawn(DotsEnemyCatalog catalog, Vector3 point, out DotsEnemyState state)
                {
                    object[] args = { point, catalog, default(Vector3), default(RaycastHit), (byte)0 };
                    var success = (bool)spawn.Invoke(runtime, args);
                    state = new DotsEnemyState { Id = 1, Kind = catalog.Kind, Position = (Vector3)args[2],
                        Swimming = (byte)args[4], Rotation = quaternion.identity, Health = catalog.MaximumHealth };
                    return success;
                }
                var troll = profiles[0]; var shark = profiles[1]; var amphibian = profiles[2];
                Require(!Spawn(troll, origin + Vector3.left * 12, out _), "Troll spawned in deep ocean.");
                Require(Spawn(troll, origin + new Vector3(7,2,0), out _), "Troll rejected dry land.");
                Require(!Spawn(shark, origin + new Vector3(7,2,0), out _), "Shark spawned on land.");
                Require(!Spawn(shark, origin, out _), "Shark spawned in shallow water.");
                Require(Spawn(shark, origin + Vector3.left * 12, out var fish), "Shark rejected deep water.");
                Require(Spawn(amphibian, origin + Vector3.left * 12, out var walker), "Amphibian rejected water.");
                void Step(ref DotsEnemyState state, DotsEnemyCatalog catalog, float direction)
                {
                    var brain = new DotsEnemyBrain { Target = -1 };
                    var delta = new float3(direction * .15f,0,0);
                    object[] args = { state, brain, delta, .05f, true, 0f, catalog };
                    if ((bool)move.Invoke(runtime,args)) { state = (DotsEnemyState)args[0]; return; }
                    var next = (Vector3)(state.Position + delta);
                    object[] dry = { next + Vector3.up * .9f, 3f, default(RaycastHit) };
                    if ((bool)ground.Invoke(runtime,dry)) state.Position = ((RaycastHit)dry[2]).point;
                }
                for (var i = 0; i < 130; i++) Step(ref walker, amphibian, 1);
                Require(walker.Position.x > origin.x + 6 && walker.Swimming == 0,
                    "Amphibian could not cross shallow water onto shore: " + (walker.Position - (float3)origin));
                for (var i = 0; i < 130; i++) Step(ref walker, amphibian, -1);
                Require(walker.Position.x < origin.x - 10 && walker.Swimming == 1,
                    "Amphibian could not return from shore to water: " + (walker.Position - (float3)origin));
                for (var i = 0; i < 100; i++) Step(ref fish, shark, 1);
                Require(fish.Position.x < origin.x - 4 && fish.Swimming == 1,
                    "Shark crossed shallow shoreline: " + (fish.Position - (float3)origin));
            }
            finally
            {
                Object.DestroyImmediate(root); Object.DestroyImmediate(shore); Object.DestroyImmediate(ocean);
                Object.DestroyImmediate(mesh); Object.DestroyImmediate(material); Object.DestroyImmediate(navigation);
                water.Dispose(); EditorSceneManager.CloseScene(scene,true);
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
            }
        }

        private static void CheckSpeciesTargets()
        {
            using var world = new World("Species targeting checks");
            using var targets = new NativeArray<EnemyTarget>(new[] {
                new EnemyTarget { Position = new float3(1,0,0), Index = 0, InWater = 0 },
                new EnemyTarget { Position = new float3(4,0,0), Index = 1, InWater = 1 }
            }, Allocator.TempJob);
            var manager = world.EntityManager;
            var system = world.GetOrCreateSystemManaged<EnemyServerSystem>();
            foreach (EnemyKind kind in Enum.GetValues(typeof(EnemyKind)))
            {
                var entity = manager.CreateEntity(typeof(DotsEnemyState),typeof(DotsEnemyBrain));
                manager.SetComponentData(entity,new DotsEnemyState { Kind = kind, Health = 100 });
                system.Seek(targets,0,0,1);
                Require(manager.GetComponentData<DotsEnemyBrain>(entity).Target == (kind == EnemyKind.Shark ? 1 : 0),
                    "Invalid target habitat for " + kind);
                manager.DestroyEntity(entity);
            }
        }
    }
}
