using System;
using System.IO;
using System.Reflection;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WaveByWave.Enemies;
using WaveByWave.Player;
using Object = UnityEngine.Object;
using RaycastHit = UnityEngine.RaycastHit;

namespace WaveByWave.Editor
{
    public static class EnemyPursuitChecks
    {
        private const string Request = "Temp/EnemyPursuitChecks.request";
        private static int _safeEditorFrames;
        [InitializeOnLoadMethod]
        private static void Install() => EditorApplication.update += RunRequested;
        private static void RunRequested()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            { _safeEditorFrames = 0; return; }
            // isPlayingOrWillChangePlaymode can turn false one update before the editor
            // scene API leaves its play-mode transition state.
            if (++_safeEditorFrames < 3) return;
            if (File.GetLastWriteTimeUtc("Assets/_Project/Editor/EnemyPursuitChecks.cs") >
                File.GetLastWriteTimeUtc(typeof(EnemyPursuitChecks).Assembly.Location))
            { AssetDatabase.Refresh(); return; }
            var compiled = File.GetLastWriteTimeUtc(typeof(DotsEnemyShipRuntime).Assembly.Location);
            foreach (var file in new[] { "DotsEnemyShipRuntime.cs", "DotsEnemyShipRuntime.Collisions.cs", "DotsEnemyRuntime.cs", "DotsEnemyRuntime.Surface.cs", "DotsEnemyCatalog.cs", "DotsEnemyNetcode.cs", "EnemyShipDefinition.cs" })
                if (File.GetLastWriteTimeUtc("Assets/_Project/Runtime/Enemies/" + file) > compiled)
                { AssetDatabase.Refresh(); return; }
            File.Delete(Request);
            _safeEditorFrames = 0;
            try { Run(); File.WriteAllText("Temp/EnemyPursuitChecks.result", "PASS: targeting, hulls, render bounds, stable locomotion, crowd spacing, edge pursuit, moving-deck transfers and crew-defeat sinking."); }
            catch (Exception error) { File.WriteAllText("Temp/EnemyPursuitChecks.result", "FAIL: " + error); Debug.LogException(error); }
        }

        private static void Check(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException(reason); }

        [MenuItem("Tools/Wave by Wave/Enemies/Check pursuit, edges and hull contacts")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Run outside Play Mode.");
            CheckSteering();
            CheckSkeletonCrowd();
            CheckHullCast();
            CheckAuthoredHull();
            CheckEdgeBudget();
            CheckMovementStability();
            CheckRenderBounds();
            CheckEdges();
            CheckCrewSinking();
            Debug.Log("[Enemy pursuit checks] PASS");
        }

        private static void CheckCrewSinking()
        {
            var root = new GameObject("Crew sinking regression") { hideFlags = HideFlags.HideAndDontSave };
            root.SetActive(false);
            var definition = ScriptableObject.CreateInstance<EnemyShipDefinition>();
            try
            {
                var runtime = root.AddComponent<DotsEnemyRuntime>();
                var ships = root.AddComponent<DotsEnemyShipRuntime>();
                typeof(DotsEnemyShipRuntime).GetProperty("Definition").SetValue(ships, definition);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var register = typeof(DotsEnemyRuntime).GetMethod("RegisterCrewMember", flags);
                var death = typeof(DotsEnemyRuntime).GetMethod("RecordCrewDeath", flags);
                var board = typeof(DotsEnemyRuntime).GetMethod("ReleaseBoardedCrew", BindingFlags.Static | BindingFlags.NonPublic);
                var begin = typeof(DotsEnemyShipRuntime).GetMethod("BeginSinking", flags);
                int Kill(ref DotsEnemyBrain brain)
                {
                    object[] args = { brain };
                    var defeatedShip = (int)death.Invoke(runtime, args);
                    brain = (DotsEnemyBrain)args[0];
                    return defeatedShip;
                }
                register.Invoke(runtime, new object[] { 1 });
                register.Invoke(runtime, new object[] { 1 });
                register.Invoke(runtime, new object[] { 2 });
                var first = new DotsEnemyBrain { CrewShipId = 1 };
                var boarder = new DotsEnemyBrain { CrewShipId = 1,
                    SpawnGroup = DotsEnemyRuntime.CrewGroupForShip(1) };
                var otherShip = new DotsEnemyBrain { CrewShipId = 2 };
                var islandEnemy = new DotsEnemyBrain();
                var neverSpawned = new DotsEnemyBrain { CrewShipId = 3 };
                Check(Kill(ref islandEnemy) == 0 && Kill(ref neverSpawned) == 0,
                    "An island enemy or an unspawned/disabled crew triggered sinking.");
                Check(Kill(ref first) == 0 && Kill(ref first) == 0,
                    "An early or duplicate crew death sank the ship.");
                object[] boardingArgs = { boarder, new DotsEnemyState { SupportId = 0 } };
                board.Invoke(null, boardingArgs);
                boarder = (DotsEnemyBrain)boardingArgs[0];
                Check(boarder.SpawnGroup == 0 && boarder.CrewShipId == 1,
                    "Leaving the original deck lost the crew's home ship.");
                Check(Kill(ref boarder) == 1 && Kill(ref boarder) == 0,
                    "The last boarder's death did not signal exactly one sinking.");
                Check(Kill(ref otherShip) == 2, "One ship's crew count affected another ship.");
                register.Invoke(runtime, new object[] { 4 });
                typeof(DotsEnemyRuntime).GetMethod("ClearServer", flags).Invoke(runtime, null);
                var previousSession = new DotsEnemyBrain { CrewShipId = 4 };
                Check(Kill(ref previousSession) == 0, "Crew tracking survived a server/scene reset.");

                var ship = new DotsEnemyShipState { Id = 1, Health = 500,
                    Position = new float3(12, 3, -7), Rotation = quaternion.RotateY(0.7f) };
                object[] sinkArgs = { ship };
                Check((bool)begin.Invoke(ships, sinkArgs), "Crew defeat failed to begin sinking.");
                ship = (DotsEnemyShipState)sinkArgs[0];
                Check(ship.Health == 0 && math.all(ship.DeathPosition == ship.Position) &&
                    math.all(ship.DeathRotation.value == ship.Rotation.value),
                    "Sinking did not preserve the replicated death pose.");
                var deathAt = ship.DeathAt;
                Check(!(bool)begin.Invoke(ships, sinkArgs) && ((DotsEnemyShipState)sinkArgs[0]).DeathAt == deathAt,
                    "Repeated defeat restarted sinking.");
                var brainState = new DotsEnemyShipBrain { LastTick = deathAt };
                object[] simulateArgs = { Entity.Null, ship, brainState, deathAt + definition.SinkDuration * 0.5f };
                typeof(DotsEnemyShipRuntime).GetMethod("Simulate", flags).Invoke(ships, simulateArgs);
                var sinking = (DotsEnemyShipState)simulateArgs[1];
                Check(sinking.Position.y < ship.Position.y && math.all(sinking.Position.xz == ship.Position.xz),
                    "The defeated ship did not follow its existing sinking animation.");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(definition);
            }
        }

        private static void CheckSkeletonCrowd()
        {
            using var world = new World("Skeleton crowd regression");
            var system = world.GetOrCreateSystemManaged<EnemyServerSystem>();
            var targets = new NativeArray<EnemyTarget>(1, Allocator.TempJob);
            try
            {
                targets[0] = new EnemyTarget { Position = new float3(10, 0, 0), Index = 0 };
                var left = world.EntityManager.CreateEntity(typeof(DotsEnemyState), typeof(DotsEnemyBrain));
                var right = world.EntityManager.CreateEntity(typeof(DotsEnemyState), typeof(DotsEnemyBrain));
                world.EntityManager.SetComponentData(left, new DotsEnemyState
                    { Id = 1, Health = 60, Scene = 1, SupportId = 9, Position = float3.zero });
                world.EntityManager.SetComponentData(right, new DotsEnemyState
                    { Id = 2, Health = 60, Scene = 1, SupportId = 9, Position = new float3(0.4f,0,0) });
                system.Seek(targets, 0, 0.82f, 1.2f);
                var leftSeparation = world.EntityManager.GetComponentData<DotsEnemyBrain>(left).Separation;
                var rightSeparation = world.EntityManager.GetComponentData<DotsEnemyBrain>(right).Separation;
                Check(leftSeparation.x < 0 && rightSeparation.x > 0,
                    $"Close skeletons on one surface were not directed apart: {leftSeparation}, {rightSeparation}.");
                var separatedSurface = world.EntityManager.GetComponentData<DotsEnemyState>(right);
                separatedSurface.SupportId = 10;
                world.EntityManager.SetComponentData(right, separatedSurface);
                system.Seek(targets, 0, 0.82f, 1.2f);
                Check(math.lengthsq(world.EntityManager.GetComponentData<DotsEnemyBrain>(left).Separation) == 0,
                    "Skeletons on different surfaces repelled each other.");
            }
            finally { targets.Dispose(); }
        }

        private static void CheckEdgeBudget()
        {
            var method = typeof(DotsEnemyRuntime).GetMethod("NextSurfaceCursor", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var population in new[] { 33, 73, 200, 500 })
            {
                var seen = new bool[population];
                var cursor = 0;
                for (var frame = 0; frame < population * 2; frame++)
                {
                    for (var i = 0; i < 32; i++) seen[(cursor + i) % population] = true;
                    cursor = (int)method.Invoke(null, new object[] { cursor, System.Math.Min(256, population), population, 32 });
                }
                Check(Array.TrueForAll(seen, value => value), $"Edge searches starved enemies in population {population}.");
            }
        }

        private static void CheckMovementStability()
        {
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var smooth = typeof(DotsEnemyRuntime).GetMethod("SmoothSurfaceHeight", flags);
            var limitedHeight = (float)smooth.Invoke(null, new object[] { 0f, 10f, 4f, 0.05f });
            Check(Mathf.Abs(limitedHeight - 0.2f) < 0.0001f,
                "Surface height snapped instead of moving on the vertical axis at its configured speed.");
            var locomotion = typeof(DotsEnemyRuntime).GetMethod("ResolveLocomotionAnimation", flags);
            var animation = EnemyAnimationState.Run;
            var idleTime = 0f;
            for (var frame = 0; frame < 20; frame++)
            {
                object[] args = { animation, false, false, 0f, 0.05f, frame % 2 == 0, true, idleTime };
                animation = (EnemyAnimationState)locomotion.Invoke(null, args);
                idleTime = (float)args[7];
            }
            Check(animation == EnemyAnimationState.Idle,
                "A blocked skeleton kept running despite evaluated zero movement.");
            for (var frame = 0; frame < 6; frame++)
            {
                object[] args = { animation, false, false, 0f, 0.05f, true, false, idleTime };
                animation = (EnemyAnimationState)locomotion.Invoke(null, args);
                idleTime = (float)args[7];
            }
            Check(animation == EnemyAnimationState.Idle,
                "Skeleton did not return to idle after movement intent ended.");

            var instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            Check(typeof(DotsEnemyRuntime).GetMethod("LimitCrowdStep", instanceFlags) == null,
                "Hard crowd step limiter returned and can reintroduce contact jerks.");
            Check(typeof(DotsEnemyRuntime).GetMethod("SteerCrowdStep", instanceFlags) != null,
                "Smooth personal-space steering is missing.");
        }

        private static void CheckRenderBounds()
        {
            var job = typeof(DotsEnemyPresentation).Assembly.GetType("WaveByWave.Enemies.EnemyRenderJob");
            var stable = job.GetMethod("StableRenderBounds", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var bounds = new AABB { Center = float3.zero, Extents = new float3(0.1f) };
            var result = (AABB)stable.Invoke(null, new object[] { bounds });
            Check(math.all(result.Extents >= new float3(1.25f, 1.5f, 1.25f)),
                "Animated skeleton render bounds remained smaller than a full character.");
        }

        private static void CheckSteering()
        {
            using var world = new World("Pursuit regression");
            var definition = ScriptableObject.CreateInstance<EnemyShipDefinition>();
            var targets = new NativeArray<EnemyShipTarget>(1, Allocator.TempJob);
            try
            {
                var system = world.GetOrCreateSystemManaged<EnemyShipServerSystem>();
                var entity = world.EntityManager.CreateEntity(typeof(DotsEnemyShipState), typeof(DotsEnemyShipBrain));
                world.EntityManager.SetComponentData(entity, new DotsEnemyShipState { Id = 1, Health = 100, Rotation = quaternion.identity });
                foreach (var range in new[] { 0.1f, 1f, 5f, 20f, 32f, 1000f, 10000f })
                for (byte side = 0; side < 2; side++)
                {
                    targets[0] = new EnemyShipTarget { Position = new float3(range, 0, 0), Index = 0 };
                    world.EntityManager.SetComponentData(entity, new DotsEnemyShipBrain { OrbitSide = side });
                    system.Steer(targets, definition);
                    var brain = world.EntityManager.GetComponentData<DotsEnemyShipBrain>(entity);
                    Check(brain.Target == 0 && brain.DesiredDirection.x > 0,
                        $"Ship stopped closing at range {range}, side {side}.");
                }
            }
            finally { targets.Dispose(); Object.DestroyImmediate(definition); }
        }

        private static void CheckHullCast()
        {
            var hull = Unity.Physics.BoxCollider.Create(new BoxGeometry { Center = float3.zero,
                Size = new float3(2, 2, 12), Orientation = quaternion.identity, BevelRadius = 0 });
            var method = typeof(DotsEnemyShipRuntime).GetMethod("CastHull", BindingFlags.Static | BindingFlags.NonPublic);
            try
            {
                float Cast(float3 position, quaternion rotation, float3 movement)
                {
                    object[] args = { hull, hull, RigidTransform.identity, new RigidTransform(rotation, position), movement, 1f, float3.zero };
                    method.Invoke(null, args);
                    return (float)args[5];
                }
                Check(Cast(new float3(7,0,0), quaternion.identity, new float3(2,0,0)) == 1,
                    "Hull stopped before contact across a five-metre gap.");
                var hit = Cast(new float3(7,0,0), quaternion.identity, new float3(10,0,0));
                Check(math.abs(hit - 0.5f) < 0.01f, "Sweep did not use actual collider width.");
                Check(Cast(new float3(2,0,0), quaternion.identity, new float3(0,0,3)) == 1,
                    "Touching hulls blocked parallel movement.");
                Check(Cast(new float3(2,0,0), quaternion.identity, new float3(-3,0,0)) == 1,
                    "Touching hulls could not separate.");
                hit = Cast(new float3(10,0,0), quaternion.RotateY(math.PI / 2), new float3(10,0,0));
                Check(math.abs(hit - 0.3f) < 0.01f, "Sweep ignored hull rotation.");
            }
            finally { hull.Dispose(); }
        }

        private static void CheckAuthoredHull()
        {
            var definition = EnemyShipDefinition.Load();
            var factory = typeof(DotsEnemyShipRuntime).Assembly.GetType("WaveByWave.Collision.AuthoredShipHull")
                .GetMethod("Create", BindingFlags.Static | BindingFlags.Public);
            var hull = (BlobAssetReference<Unity.Physics.Collider>)factory.Invoke(null,
                new object[] { definition.ViewPrefab, (int)definition.CollisionLayers });
            try
            {
                var bounds = hull.Value.CalculateAabb();
                var span = math.length(bounds.Max - bounds.Min) * 2;
                var cast = typeof(DotsEnemyShipRuntime).GetMethod("CastHull", BindingFlags.Static | BindingFlags.NonPublic);
                object[] args = { hull, hull, new RigidTransform(quaternion.identity, new float3(-span,0,0)),
                    RigidTransform.identity, new float3(2*span,0,0), 1f, float3.zero };
                cast.Invoke(null,args);
                Check((float)args[5] > 0 && (float)args[5] < 0.5f,
                    "Authored compound sweep missed the actual prefab hull.");
            }
            finally { hull.Dispose(); }
        }

        private static void CheckEdges()
        {
            // Preview scenes do not contribute colliders to Physics.RaycastNonAlloc.
            // Use a temporary additive scene, preserving the user's active/dirty scenes.
            var activeScene = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var deck = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var root = new GameObject("Surface pursuit check");
            var catalog = ScriptableObject.CreateInstance<DotsEnemyCatalog>();
            var water = new EquipmentWaterQuery(null);
            try
            {
                SceneManager.MoveGameObjectToScene(deck, scene);
                SceneManager.MoveGameObjectToScene(root, scene);
                var runtime = root.AddComponent<DotsEnemyRuntime>();
                typeof(DotsEnemyRuntime).GetProperty("Catalog").SetValue(runtime, catalog);
                typeof(DotsEnemyRuntime).GetField("_water", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(runtime, water);
                var follow = typeof(DotsEnemyRuntime).GetMethod("TryFollowEdge", BindingFlags.Instance | BindingFlags.NonPublic);
                var continuous = typeof(DotsEnemyRuntime).GetMethod("HasContinuousGround", BindingFlags.Instance | BindingFlags.NonPublic);
                var origin = new Vector3(10000,10000,10000);
                deck.transform.localScale = new Vector3(2,1,10);
                deck.transform.position = origin - Vector3.up * 0.5f;
                // Register an explicit stable frame for deck-axis tangents.
                deck.AddComponent<EnemySurfaceAnchor>().Key = "Pursuit regression deck";
                runtime.RegisterSurface(deck.transform);
                var key = (ulong)typeof(DotsEnemyRuntime).GetMethod("SceneSurfaceKey", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { deck.transform });
                foreach (var yaw in new[] { 0f, 37f, 123f })
                {
                    var rotation = Quaternion.Euler(0,yaw,0);
                    deck.transform.rotation = rotation;
                    UnityEngine.Physics.SyncTransforms();
                    var state = new DotsEnemyState { Position = origin + rotation * new Vector3(0.98f,0,0), SupportId = key };
                    foreach (var lateral in new[] { 3f, -3f })
                    {
                        var goal = origin + rotation * new Vector3(5,-2,lateral);
                        for (var step = 0; step < 150; step++)
                        {
                            object[] args = { state, goal, 0.05f, default(RaycastHit) };
                            if (!(bool)follow.Invoke(runtime, args)) break;
                            state.Position = ((RaycastHit)args[3]).point;
                        }
                        var local = Quaternion.Inverse(rotation) * ((Vector3)state.Position - origin);
                        Check(local.x <= 1.002f && Mathf.Abs(local.z - lateral) < 0.3f,
                            $"Skeleton stuck or left deck: yaw={yaw}, target z={lateral}, actual={local}.");
                    }
                }
                deck.transform.rotation = Quaternion.identity;
                UnityEngine.Physics.SyncTransforms();
                Check(!(bool)continuous.Invoke(runtime, new object[] { origin, origin + Vector3.right * 3 }),
                    "Unsupported path was accepted after jump removal.");
                CheckSurfaceTransfers(runtime, deck, catalog, origin, key);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(deck);
                Object.DestroyImmediate(catalog);
                water.Dispose();
                EditorSceneManager.CloseScene(scene, true);
                if (activeScene.IsValid() && activeScene.isLoaded) SceneManager.SetActiveScene(activeScene);
            }
        }

        private static void CheckSurfaceTransfers(DotsEnemyRuntime runtime, GameObject deck,
            DotsEnemyCatalog catalog, Vector3 origin, ulong sourceKey)
        {
            var destination = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var water = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(destination, deck.scene);
            SceneManager.MoveGameObjectToScene(water, deck.scene);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var attach = typeof(DotsEnemyRuntime).GetMethod("AttachSurface", flags);
            var begin = typeof(DotsEnemyRuntime).GetMethod("TryBeginSurfaceTransfer", flags);
            var advance = typeof(DotsEnemyRuntime).GetMethod("AdvanceSurfaceTransfer", flags);
            var face = typeof(DotsEnemyRuntime).GetMethod("FaceTarget", flags);
            try
            {
                destination.transform.localScale = new Vector3(2, 1, 10);
                destination.AddComponent<EnemySurfaceAnchor>().Key = "Pursuit regression destination";
                runtime.RegisterSurface(destination.transform);
                var destinationKey = (ulong)typeof(DotsEnemyRuntime).GetMethod("SceneSurfaceKey", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { destination.transform });
                water.transform.localScale = new Vector3(20, 0.1f, 20);
                water.transform.position = origin - Vector3.up * 2;
                water.AddComponent<StylizedWater3.WaterObject>();

                DotsEnemyState Start(int id, Quaternion rotation)
                {
                    var state = new DotsEnemyState { Id = id, Health = 60, SupportId = sourceKey,
                        Position = origin + rotation * new Vector3(0.98f,0,0), Rotation = quaternion.identity };
                    object[] args = { state, deck.GetComponent<UnityEngine.Collider>() };
                    attach.Invoke(runtime, args);
                    return (DotsEnemyState)args[0];
                }
                bool Advance(ref DotsEnemyState state, ref DotsEnemyBrain brain)
                {
                    object[] args = { state, brain, 0.02f, 10f };
                    var active = (bool)advance.Invoke(runtime, args);
                    state = (DotsEnemyState)args[0]; brain = (DotsEnemyBrain)args[1];
                    return active;
                }

                var facing = Start(8000, Quaternion.identity);
                var intent = new DotsEnemyBrain { Target = 0, Direction = math.right() };
                for (var i = 0; i < 20; i++)
                {
                    object[] args = { facing, intent, 0.05f, 10f };
                    face.Invoke(runtime, args); facing = (DotsEnemyState)args[0];
                }
                Check(math.dot(math.forward(facing.Rotation), math.right()) > 0.99f,
                    "Blocked skeleton did not face its target without a successful ground step.");
                Check(math.distance(facing.Position, origin + Vector3.right * 0.98f) < 0.01f,
                    "Facing a target displaced the blocked skeleton.");

                var id = 8100;
                foreach (var yaw in new[] { 0f, 37f })
                foreach (var gap in new[] { 0.35f, 0.95f, 1f, 1.05f, 1.5f })
                {
                    var rotation = Quaternion.Euler(0, yaw, 0);
                    deck.transform.SetPositionAndRotation(origin - Vector3.up * 0.5f, rotation);
                    destination.transform.SetPositionAndRotation(origin + rotation * Vector3.right * (2 + gap) - Vector3.up * 0.5f, rotation);
                    UnityEngine.Physics.SyncTransforms();
                    var state = Start(id++, rotation);
                    var brain = new DotsEnemyBrain { Target = 0, Direction = rotation * Vector3.right };
                    var goal = origin + rotation * Vector3.right * (2 + gap);
                    var started = (bool)begin.Invoke(runtime, new object[] { state, goal });
                    Check(started == (gap <= catalog.MaximumSurfaceGap), $"Water gap limit failed at {gap} m, yaw {yaw}.");
                    if (!started) continue;
                    for (var step = 0; step < 100; step++)
                    {
                        var movingRotation = Quaternion.Euler(0, yaw + step * 0.2f, 0);
                        var offset = Vector3.forward * (step * 0.02f);
                        deck.transform.SetPositionAndRotation(origin + offset - Vector3.up * 0.5f, movingRotation);
                        destination.transform.SetPositionAndRotation(origin + offset + movingRotation * Vector3.right * (2 + gap) - Vector3.up * 0.5f, movingRotation);
                        var previous = state.Position;
                        if (!Advance(ref state, ref brain)) break;
                        Check(math.distance(previous, state.Position) <= catalog.MoveSpeed * 0.02f + 0.05f,
                            "Transfer teleported instead of advancing at walking speed.");
                        Check(math.abs(state.Position.y - origin.y) < 0.01f, "Transfer added a jump arc.");
                        if (state.SupportId == destinationKey) break;
                    }
                    Check(state.SupportId == destinationKey, "Skeleton did not attach to the moving destination deck.");
                }

                deck.transform.SetPositionAndRotation(origin - Vector3.up * 0.5f, Quaternion.identity);
                destination.transform.SetPositionAndRotation(origin + Vector3.right * 2.7f - Vector3.up * 0.5f, Quaternion.identity);
                UnityEngine.Physics.SyncTransforms();
                var returning = Start(id++, Quaternion.identity);
                var returnBrain = new DotsEnemyBrain { Target = 0 };
                Check((bool)begin.Invoke(runtime, new object[] { returning, origin + Vector3.right * 3 }), "Could not start retreat scenario.");
                Advance(ref returning, ref returnBrain);
                Advance(ref returning, ref returnBrain);
                destination.transform.position += Vector3.right * 2;
                for (var step = 0; step < 50; step++)
                {
                    var previous = returning.Position;
                    if (!Advance(ref returning, ref returnBrain)) break;
                    Check(math.distance(previous, returning.Position) <= catalog.MoveSpeed * 0.02f + 0.005f,
                        "Departing ship yanked the returning skeleton across the gap.");
                }
                Check(returning.SupportId == sourceKey && math.distance(returning.Position, origin + Vector3.right * 0.98f) < 0.02f,
                    "Skeleton followed a stale bridge after the ships separated.");

                destination.transform.position = origin + Vector3.right * 2.7f - Vector3.up * 0.5f;
                catalog.MaximumSurfaceGap = 0;
                UnityEngine.Physics.SyncTransforms();
                Check(!(bool)begin.Invoke(runtime, new object[] { Start(id++, Quaternion.identity), origin + Vector3.right * 3 }),
                    "Zero gap limit did not disable walking transfers.");
                catalog.MaximumSurfaceGap = 1;
                destination.transform.position += Vector3.up * 1.5f;
                UnityEngine.Physics.SyncTransforms();
                Check(!(bool)begin.Invoke(runtime, new object[] { Start(id++, Quaternion.identity), origin + Vector3.right * 3 }),
                    "Walking transfer ignored its height limit.");
            }
            finally { Object.DestroyImmediate(destination); Object.DestroyImmediate(water); }
        }
    }
}
