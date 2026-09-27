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
            if (File.GetLastWriteTimeUtc("Assets/_Project/Editor/EnemyPursuitChecks.cs") >
                File.GetLastWriteTimeUtc(typeof(EnemyPursuitChecks).Assembly.Location))
            { AssetDatabase.Refresh(); return; }
            foreach (var source in new[] { "EnemyDeckNavigationBaker.cs", "OceanGenerationChecks.cs" })
                if (File.GetLastWriteTimeUtc("Assets/_Project/Editor/" + source) >
                    File.GetLastWriteTimeUtc(typeof(EnemyPursuitChecks).Assembly.Location))
                { AssetDatabase.Refresh(); return; }
            foreach (var source in new[] { "Enemies/DotsEnemyRuntime.cs", "Enemies/DotsEnemyRuntime.Navigation.cs",
                "Enemies/DotsEnemyRuntime.Surface.cs", "Enemies/DotsEnemyRuntime.Species.cs", "Enemies/DotsEnemyCatalog.cs",
                "Enemies/DotsEnemyNetcode.cs", "Enemies/EnemyDeckNavigationData.cs",
                "Enemies/EnemyDeckNavigation.cs", "Ships/ShipHoldSabotage.cs", "Ships/ShipFlooding.cs" })
                if (File.GetLastWriteTimeUtc("Assets/_Project/Runtime/" + source) >
                    File.GetLastWriteTimeUtc(typeof(DotsEnemyRuntime).Assembly.Location))
                { AssetDatabase.Refresh(); return; }
            string command;
            try { command = File.ReadAllText(Request).Trim(); }
            catch (IOException) { return; }
            if (command == "palm-grounding")
            {
                File.Delete(Request);
                try { OceanGenerationChecks.CheckDecorationPlacement(); File.WriteAllText("Temp/IslandPalmGrounding.result", "PASS: actual configured palm mesh, seeded island generation, child offsets, rotation, random scale and support points."); }
                catch (Exception error) { File.WriteAllText("Temp/IslandPalmGrounding.result", "FAIL: " + error); }
                return;
            }
            if (command == "catalog-binding")
            {
                File.Delete(Request);
                try
                {
                    CheckSkeletonCatalogBinding();
                    var catalog = Resources.Load<DotsEnemyCatalog>(DotsEnemyCatalog.SkeletonResourcePath);
                    File.WriteAllText("Temp/EnemyCatalogBinding.result", $"PASS: {AssetDatabase.GetAssetPath(catalog)}; step={catalog.StepHeight}; drop={catalog.MaximumDrop}; gap={catalog.MaximumSurfaceGap}; transferHeight={catalog.SurfaceTransferHeight}; edgeFollowing={catalog.EnableSurfaceEdgeFollowing}; edgeDetours={catalog.EnableSurfaceEdgeDetours}");
                }
                catch (Exception error) { File.WriteAllText("Temp/EnemyCatalogBinding.result", "FAIL: " + error); }
                return;
            }
            if (command == "climbing-regression")
            {
                File.Delete(Request);
                try { CheckClimbing(); File.WriteAllText("Temp/EnemyClimbing.result", "PASS: mesh deck recovery, raised ledge, ship gap, disabled edge following, gap and height limits."); }
                catch (Exception error) { File.WriteAllText("Temp/EnemyClimbing.result", "FAIL: " + error); }
                return;
            }
            if (command == "arch-regression")
            {
                File.Delete(Request);
                try
                {
                    CheckArchPassages();
                    CheckClimbing();
                    File.WriteAllText("Temp/EnemyArch.result", "PASS: full movement batch through separate/combined arch colliders, low headroom, solid box/closed mesh, upper-floor support, raised ledge and ship-gap climbing.");
                }
                catch (Exception error) { File.WriteAllText("Temp/EnemyArch.result", "FAIL: " + error); }
                return;
            }
            if (command == "ship-routes-setup")
            {
                File.Delete(Request);
                try
                {
                    EnemyDeckNavigationBaker.BakeProjectShipRoutes();
                    File.WriteAllText("Temp/EnemyShipRoutes.result", "PASS: ship route maps built and assigned; navigation enabled.");
                }
                catch (Exception error) { File.WriteAllText("Temp/EnemyShipRoutes.result", "FAIL: " + error); }
                return;
            }
            if (command == "ship-routes-check")
            {
                File.Delete(Request);
                try { CheckShipRouteMaps(); }
                catch (Exception error) { File.WriteAllText("Temp/EnemyShipRouteChecks.result", "FAIL: " + error); }
                return;
            }
            if (command == "ship-species-report")
            {
                File.Delete(Request);
                try
                {
                    var report = new System.Text.StringBuilder();
                    var ship = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Ship.prefab");
                    var nav = ship.GetComponent<EnemyDeckNavigation>();
                    foreach (var species in new[] { "Skeleton", "Amphibian", "Troll" })
                    {
                        var profile = AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>($"Assets/_Project/Resources/Enemies/{species}EnemyCatalog.asset");
                        var minimum = Vector3.positiveInfinity; var maximum = Vector3.negativeInfinity;
                        foreach (var part in profile.BakedParts)
                            foreach (var v in part.Mesh.vertices)
                            { minimum = Vector3.Min(minimum,v*profile.VisualScale); maximum = Vector3.Max(maximum,v*profile.VisualScale); }
                        var map = nav.MapFor(profile.DeckAgentRadius,profile.DeckAgentHeight,profile.MaximumSlope);
                        var holdNodes=0;
                        if (map != null) foreach (var node in map.Nodes) if(node.Position.y<1.8f) holdNodes++;
                        report.AppendLine($"{species}: visual idle bounds {minimum}..{maximum}; body R={profile.BodyRadius} H={profile.BodyHeight}; navigation R={profile.DeckAgentRadius} H={profile.DeckAgentHeight}; map={map?.name}, nodes={map?.Nodes.Length}, hold nodes={holdNodes}");
                    }
                    File.WriteAllText("Temp/EnemyShipSpecies.result",report.ToString());
                }
                catch(Exception error) { File.WriteAllText("Temp/EnemyShipSpecies.result","FAIL: "+error); }
                return;
            }
            if (command == "ship-routes-verify")
            {
                File.Delete(Request);
                try
                {
                    EnemyDeckNavigationBaker.ConfigurePlayerHold();
                    CheckDeckRoutes();
                    CheckDeckMovement();
                    CheckDeckTargetPriority();
                    File.WriteAllText("Temp/EnemyShipRouteVerification.result", "PASS: ship routes, floor selection, shared cache, gap limits, hold slots and crowd damage cap.");
                }
                catch (Exception error) { File.WriteAllText("Temp/EnemyShipRouteVerification.result", "FAIL: " + error); }
                return;
            }
            if (command == "ship-species-verify")
            {
                File.Delete(Request);
                try
                {
                    foreach(var kind in new[] {EnemyKind.Skeleton,EnemyKind.Amphibian,EnemyKind.Troll})
                        CheckDeckMovement(kind);
                    CheckDeckTargetPriority();
                    File.WriteAllText("Temp/EnemyShipSpeciesVerification.result","PASS: all land species navigate decks/hold on level and rotated ships; baked/unbaked hand-offs and bow recovery; same-island, aboard-ship, water and remote-island target priorities; independent profiles and enemy-ship exclusion.");
                }
                catch(Exception error) { File.WriteAllText("Temp/EnemyShipSpeciesVerification.result","FAIL: "+error); }
                return;
            }
            if (command == "boarding-snapshot")
            {
                File.Delete(Request);
                try { CaptureBoardingSnapshot(); }
                catch (Exception error) { File.WriteAllText("Temp/EnemyBoardingSnapshot.result", error.ToString()); }
                return;
            }
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

        private static void CheckSkeletonCatalogBinding()
        {
            var authored = AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(
                "Assets/_Project/Resources/Enemies/SkeletonEnemyCatalog.asset");
            Check(authored != null && authored.IsBaked, "The authored skeleton profile is missing or not baked.");
            Check(Resources.Load<DotsEnemyCatalog>(DotsEnemyCatalog.SkeletonResourcePath) == authored,
                "Runtime loads a different skeleton profile than the one authored in Resources/Enemies.");
            Check(AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(EnemyContentSetup.CatalogPath) == authored,
                "Skeleton baking/editor tools address a different profile than runtime.");
        }

        private static void CheckShipRouteMaps()
        {
            var output = new System.Text.StringBuilder();
            var root = PrefabUtility.LoadPrefabContents("Assets/_Project/Prefabs/Ship.prefab");
            try
            {
                var nav = root.GetComponent<EnemyDeckNavigation>();
                Check(nav != null && nav.Data != null && nav.Data.IsBaked, "Player ship has no map.");
                foreach (var collider in root.GetComponentsInChildren<UnityEngine.Collider>())
                {
                    if (!collider.enabled || collider.isTrigger) continue;
                    if (!collider.name.Contains("Floor") && !collider.name.Contains("Stairs") &&
                        !collider.name.Contains("Mast") && !collider.name.Contains("Ladder")) continue;
                    output.AppendLine($"{collider.name}: {root.transform.InverseTransformPoint(collider.bounds.center)} size={collider.bounds.size}");
                }
                var map = nav.Data;
                var groups = new System.Collections.Generic.Dictionary<int, int>();
                for (var i = 0; i < map.Nodes.Length; i++)
                {
                    var bin = Mathf.RoundToInt(map.Nodes[i].Position.y * 2);
                    groups[bin] = groups.TryGetValue(bin, out var count) ? count + 1 : 1;
                }
                output.AppendLine($"nodes={map.Nodes.Length}; links={map.Links.Length}");
                foreach (var entry in groups) output.AppendLine($"y={entry.Key * 0.5f}: {entry.Value}");
                var visits = new bool[map.Nodes.Length];
                var queue = new System.Collections.Generic.Queue<int>();
                for (var start = 0; start < visits.Length; start++)
                {
                    if (visits[start]) continue;
                    queue.Enqueue(start); visits[start] = true;
                    var count = 0; var min = float.PositiveInfinity; var max = float.NegativeInfinity;
                    while (queue.Count > 0)
                    {
                        var index = queue.Dequeue(); count++;
                        var node = map.Nodes[index]; min = Mathf.Min(min, node.Position.y); max = Mathf.Max(max, node.Position.y);
                        for (var j = node.FirstLink; j < node.FirstLink + node.LinkCount; j++)
                        { var next = map.Links[j]; if (!visits[next]) { visits[next] = true; queue.Enqueue(next); } }
                    }
                    if (count >= 10) output.AppendLine($"component start={start} count={count} height={min}..{max} pos={map.Nodes[start].Position}");
                }
                File.WriteAllText("Temp/EnemyShipRouteChecks.result", output.ToString());
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static void CheckDeckRoutes()
        {
            var root = PrefabUtility.LoadPrefabContents("Assets/_Project/Prefabs/Ship.prefab");
            try
            {
                var nav = root.GetComponent<EnemyDeckNavigation>();
                var hold = root.GetComponent<WaveByWave.Ships.ShipHoldSabotage>();
                Check(hold != null, "Hold objective is not configured.");
                var map = nav.Data;
                var points = new[] { new Vector3(0.1f, 3f, 0), new Vector3(2.4f, 3f, 0),
                    new Vector3(1.2f, 0.95f, -1f), new Vector3(1.2f, 4.9f, -4f), new Vector3(1.2f, 3.52f, 4f) };
                var indexes = new int[points.Length];
                for (var i = 0; i < points.Length; i++)
                    Check(map.TryNearest(points[i], 0.9f, 0.45f, out indexes[i]), "No floor near " + points[i]);
                for (var a = 0; a < indexes.Length; a++)
                for (var b = 0; b < indexes.Length; b++)
                {
                    var budget = 1;
                    Check(map.TryRoute(998, b, indexes[a], indexes[b], map.StepHeight, map.MaximumDrop, 1,
                        ref budget, out var route, 5, 10), $"No ship route {a} -> {b}.");
                    var current = indexes[a]; var count = 0;
                    while (current != indexes[b] && count++ < map.Nodes.Length)
                    {
                        var next = route[current];
                        Check(next >= 0 && next != current, "Route stopped before target.");
                        Check(map.Nodes[next].Position.y < 6, "Deck pursuit climbed mast.");
                        if (!map.TryGap(current, next, out var gap, out var arc))
                            Check(map.MoveAlongRoute(current, map.Nodes[current].Position, route, 0.02f,
                                map.StepHeight, map.MaximumDrop, out _, out _), "Route cannot follow its own connection.");
                        else Check(gap <= 5.001f && Mathf.Abs(map.Nodes[next].Position.y - map.Nodes[current].Position.y) + arc <= 10,
                            "Route violated bridge limits.");
                        current = next;
                    }
                    Check(current == indexes[b], "Route contains a cycle.");
                    budget = 0;
                    Check(map.TryRoute(998, b, indexes[a], indexes[b], map.StepHeight, map.MaximumDrop, 1,
                        ref budget, out var reused, 5, 10) && ReferenceEquals(route, reused), "Bots did not share route field.");
                }
                for (var id = 0; id < hold.AttackPositions; id++)
                {
                    Check(hold.TryAttackPosition(map, id, out _, out var feet, out var outward), "Missing hold attack slot.");
                    Check(hold.HoldBounds.Contains(feet) && Mathf.Abs(outward.x) > 0.9f, "Hold attacker faces wrong way.");
                    Check(map.TryLocate(feet, 0.1f, out var goal), "Hold slot is off map.");
                    var budget = 1;
                    Check(map.TryRoute(998, -2-id, indexes[0], goal, map.StepHeight, map.MaximumDrop, 1,
                        ref budget, out _, 5, 10), "Hold slot is unreachable.");
                }
                // A route across erased cells must disappear when transfers are disabled
                // or their allowed gap/height is too small.
                var bridge = ScriptableObject.CreateInstance<EnemyDeckNavigationData>();
                try
                {
                    bridge.Width = 2; bridge.Depth = 1; bridge.CellSize = 1;
                    bridge.Columns = new[] { new EnemyDeckColumn { FirstNode=0,Count=1 }, new EnemyDeckColumn { FirstNode=1,Count=1 } };
                    bridge.Nodes = new[] { new EnemyDeckNode { Position=Vector3.zero,Normal=Vector3.up,FirstLink=0,LinkCount=1 },
                        new EnemyDeckNode { Position=new Vector3(2,1,0),Normal=Vector3.up,FirstLink=1,LinkCount=0 } };
                    bridge.Links = new[] {1}; bridge.LinkGaps = new[] {2f}; bridge.LinkArcs = new[] {0.5f};
                    foreach (var limit in new[] { 0f, 1f, 2f })
                    {
                        var budget=1;
                        Check(bridge.TryRoute(1,0,0,1,10,10,1,ref budget,out _,limit,2) == (limit==2), "Bridge ignored runtime gap toggle/limit.");
                    }
                    var heightBudget=1;
                    Check(!bridge.TryRoute(1,0,0,1,10,10,1,ref heightBudget,out _,2,1), "Bridge ignored transfer height.");
                }
                finally { Object.DestroyImmediate(bridge); }
                foreach (var attackers in new[] { 1, 4, 3000 })
                {
                    var damage = new WaveByWave.Ships.HoldBreachBudget(); var hits = 0; var last = -100f;
                    for (var tick = 1; tick <= 3000; tick++)
                        if (damage.Tick(0.02f, attackers, true, 3, 12, 3, 4))
                        { Check(tick * 0.02f - last >= 2.99f, "Crowd bypassed minimum breach interval."); last = tick * 0.02f; hits++; }
                    Check(attackers == 1 ? hits == 4 : hits >= 18 && hits <= 19, "Unexpected shared damage rate: " + hits);
                    Check(!damage.Tick(0.02f, 0, true, 3, 12, 3, 4), "Damage continued without attackers.");
                    Check(!damage.Tick(0.02f, attackers, true, 3, 12, 3, 4), "Damage did not reset after defense.");
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static void CheckDeckTargetPriority()
        {
            using var world = new World("Ship target priority regression");
            var system = world.GetOrCreateSystemManaged<EnemyServerSystem>();
            var entity = world.EntityManager.CreateEntity(typeof(DotsEnemyState), typeof(DotsEnemyBrain));
            world.EntityManager.SetComponentData(entity,new DotsEnemyState { Id=1,Health=60,Scene=1,SupportId=9,Position=float3.zero });
            world.EntityManager.SetComponentData(entity,new DotsEnemyBrain {CanAttackShip=1});
            var targets=new NativeArray<EnemyTarget>(2,Allocator.TempJob);
            var ships=new NativeArray<PlayerShipObjective>(1,Allocator.TempJob);
            try
            {
                ships[0]=new PlayerShipObjective {Position=new float3(20,0,0),SupportId=50};
                targets[0]=new EnemyTarget { Index=0,Position=new float3(1,0,0),SupportId=10 };
                targets[1]=new EnemyTarget { Index=1,Position=new float3(0,-3,8),SupportId=9 };
                system.Seek(targets,0,0,1,shipTargets:ships);
                Check(world.EntityManager.GetComponentData<DotsEnemyBrain>(entity).Target==1,
                    "A nearby player on another island overrode the player on the enemy's island.");
                var outside=targets[1]; outside.SupportId=10; targets[1]=outside;
                system.Seek(targets,1,0,1,shipTargets:ships);
                var brain=world.EntityManager.GetComponentData<DotsEnemyBrain>(entity);
                Check(brain.Target<0 && brain.TargetingShip!=0 && brain.TargetSupport==50,
                    "Enemies on an undefended island did not prefer the player ship.");
                var aboard=targets[0]; aboard.SupportId=50; aboard.AboardAttackableShip=1;
                aboard.Position=new float3(30,0,0); targets[0]=aboard;
                system.Seek(targets,2,0,1,shipTargets:ships);
                brain=world.EntityManager.GetComponentData<DotsEnemyBrain>(entity);
                Check(brain.Target==0 && brain.TargetingShip==0,
                    "A player aboard the ship was not preferred over attacking the hull.");
                aboard.SupportId=0; aboard.AboardAttackableShip=0; aboard.InWater=1;
                aboard.Position=new float3(1,0,0); targets[0]=aboard;
                system.Seek(targets,3,0,1,shipTargets:ships);
                brain=world.EntityManager.GetComponentData<DotsEnemyBrain>(entity);
                Check(brain.Target<0 && brain.TargetingShip!=0,
                    "A player in the water lured a hull-attacking enemy away from the ship.");
                var state = world.EntityManager.GetComponentData<DotsEnemyState>(entity);
                state.SupportId = DotsEnemyRuntime.ShipSurfaceKey(99);
                world.EntityManager.SetComponentData(entity, state);
                var incapable=world.EntityManager.GetComponentData<DotsEnemyBrain>(entity);
                incapable.CanAttackShip=0; world.EntityManager.SetComponentData(entity,incapable);
                targets[0]=new EnemyTarget {Index=0,Position=new float3(1,0,0),SupportId=10};
                outside.SupportId = state.SupportId; outside.Position=new float3(0,0,8); targets[1] = outside;
                system.Seek(targets,4,0,1,shipTargets:ships);
                Check(world.EntityManager.GetComponentData<DotsEnemyBrain>(entity).Target==0,
                    "A species without hull attacks no longer uses its original nearest-player pursuit.");
            }
            finally { targets.Dispose(); ships.Dispose(); }

            var enemyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Enemies/EnemyShip.prefab");
            Check(enemyPrefab.GetComponent<EnemyDeckNavigation>() == null &&
                enemyPrefab.GetComponent<WaveByWave.Ships.ShipHoldSabotage>() == null,
                "Enemy ship still contains player-ship navigation or hold sabotage.");
            var root = new GameObject("Enemy ship navigation exclusion check"); root.SetActive(false);
            var map = ScriptableObject.CreateInstance<EnemyDeckNavigationData>();
            try
            {
                var runtime = root.AddComponent<DotsEnemyRuntime>();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var support = DotsEnemyRuntime.ShipSurfaceKey(99);
                var maps = (System.Collections.Generic.Dictionary<(ulong,EnemyKind),EnemyDeckNavigationData>)
                    typeof(DotsEnemyRuntime).GetField("_deckMaps",flags).GetValue(runtime);
                maps[(support,EnemyKind.Skeleton)] = map;
                Check(typeof(DotsEnemyRuntime).GetMethod("DeckMap",flags).Invoke(runtime,
                    new object[] {support,EnemyKind.Skeleton,Vector3.one}) == null,
                    "Enemy ship reused a stale baked navigation map.");
                maps[(9,EnemyKind.Shark)] = map;
                Check(typeof(DotsEnemyRuntime).GetMethod("DeckMap",flags).Invoke(runtime,
                    new object[] {9UL,EnemyKind.Shark,Vector3.one}) == null,
                    "Shark received a cached ship navigation map.");
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(map); }
        }

        private static void CheckDeckMovement(EnemyKind kind = EnemyKind.Skeleton)
        {
            var previousScene = SceneManager.GetActiveScene();
            var scene = Application.isPlaying ? SceneManager.CreateScene("Deck route regression " + Guid.NewGuid()) :
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var prefab = PrefabUtility.LoadPrefabContents("Assets/_Project/Prefabs/Ship.prefab");
            var fixture = new GameObject("Deck route fixture");
            var runtimeObject = new GameObject("Deck route runtime"); runtimeObject.SetActive(false);
            var master = Object.Instantiate(AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(EnemyContentSetup.CatalogPath));
            var catalog = kind == EnemyKind.Skeleton ? master : Object.Instantiate(AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(
                $"Assets/_Project/Resources/Enemies/{kind}EnemyCatalog.asset"));
            var water = new EquipmentWaterQuery(null);
            using var world = new World("Deck route regression");
            using var query = world.EntityManager.CreateEntityQuery(typeof(DotsEnemyState), typeof(DotsEnemyBrain));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            DotsEnemyRuntime runtime = null;
            try
            {
                SceneManager.MoveGameObjectToScene(fixture, scene); SceneManager.MoveGameObjectToScene(runtimeObject, scene);
                fixture.transform.position = new Vector3(20000, 15000, 20000);
                fixture.AddComponent<EnemySurfaceAnchor>().Key = "Deck route fixture";
                var navigation = fixture.AddComponent<EnemyDeckNavigation>();
                navigation.Data = prefab.GetComponent<EnemyDeckNavigation>().Data;
                navigation.AdditionalAgentMaps = prefab.GetComponent<EnemyDeckNavigation>().AdditionalAgentMaps;
                foreach (var source in prefab.GetComponentsInChildren<UnityEngine.Collider>())
                {
                    if (!source.enabled || source.isTrigger || !source.gameObject.activeInHierarchy) continue;
                    if (source is not UnityEngine.MeshCollider && source is not UnityEngine.BoxCollider) continue;
                    var child = new GameObject(source.name); child.transform.SetParent(fixture.transform, false);
                    var matrix = prefab.transform.worldToLocalMatrix * source.transform.localToWorldMatrix;
                    child.transform.localPosition = matrix.GetColumn(3); child.transform.localRotation = matrix.rotation;
                    child.transform.localScale = matrix.lossyScale; child.layer = source.gameObject.layer;
                    if (source is UnityEngine.MeshCollider mesh)
                    { var c = child.AddComponent<UnityEngine.MeshCollider>(); c.sharedMesh = mesh.sharedMesh; c.convex = mesh.convex; }
                    else if (source is UnityEngine.BoxCollider box)
                    { var c = child.AddComponent<UnityEngine.BoxCollider>(); c.center = box.center; c.size = box.size; }
                }
                runtime = runtimeObject.AddComponent<DotsEnemyRuntime>();
                typeof(DotsEnemyRuntime).GetProperty("Catalog").SetValue(runtime, master);
                ((DotsEnemyCatalog[])typeof(DotsEnemyRuntime).GetField("_species",flags).GetValue(runtime))[(int)kind] = catalog;
                typeof(DotsEnemyRuntime).GetField("_water", flags).SetValue(runtime, water);
                typeof(DotsEnemyRuntime).GetField("_serverWorld", flags).SetValue(runtime, world);
                typeof(DotsEnemyRuntime).GetField("_enemies", flags).SetValue(runtime, query);
                typeof(DotsEnemyRuntime).GetField("_scene", flags).SetValue(runtime, 991);
                Check(catalog.UsesShipNavigation,kind+" ship navigation is not enabled in the authored profile.");
                Check(catalog.CanAttackPlayerShip,kind+" cannot select the player ship as an objective.");
                master.UseBakedDeckNavigation = kind == EnemyKind.Skeleton;
                master.EnableCrowdCollisions = catalog.EnableCrowdCollisions = false;
                master.EnableCrowdAvoidance = false; master.EnableTargetSlots = false;
                runtime.RegisterSurface(fixture.transform);
                var map = navigation.MapFor(catalog.DeckAgentRadius,catalog.DeckAgentHeight,catalog.MaximumSlope);
                Check(map != null,kind+" has no suitable navigation map.");
                var hold = prefab.GetComponent<WaveByWave.Ships.ShipHoldSabotage>();
                Check(hold.TryAttackPosition(map,0,out _,out var attackPosition,out _),kind+" has no hold attack position.");
                var points = new[] { new Vector3(0.1f,3f,0), new Vector3(1.2f,0.95f,-1),
                    new Vector3(1.2f,4.9f,-4), new Vector3(1.2f,3.52f,4) };
                var move = typeof(DotsEnemyRuntime).GetMethod("MoveBatch", flags);
                var attach = typeof(DotsEnemyRuntime).GetMethod("AttachSurface", flags);
                var ground = typeof(DotsEnemyRuntime).GetMethod("TransferGroundAtWithProps", flags);
                if (kind == EnemyKind.Troll)
                {
                    // A low ceiling must not blind an agent deliberately allowed to fit below it.
                    var ceiling = new GameObject("Navigation height visibility check");
                    ceiling.transform.SetParent(fixture.transform, false);
                    ceiling.transform.localPosition = new Vector3(100,1.8f,0);
                    var ceilingCollider = ceiling.AddComponent<UnityEngine.BoxCollider>();
                    ceilingCollider.size = new Vector3(10,0.1f,10);
                    try
                    {
                        UnityEngine.Physics.SyncTransforms();
                        var origin = fixture.transform.TransformPoint(new Vector3(100,0,0));
                        object[] args = { new DotsEnemyState { Kind=kind,Position=origin,Rotation=quaternion.identity }, ceilingCollider };
                        attach.Invoke(runtime,args);
                        var state = (DotsEnemyState)args[0];
                        var see = typeof(DotsEnemyRuntime).GetMethod("CanSee",flags);
                        Check((bool)see.Invoke(runtime,new object[]{state,origin+Vector3.right,catalog}),
                            "Low ceiling blocked the troll's navigation-height attack sight.");
                        catalog.UseBakedDeckNavigation = false;
                        Check(!(bool)see.Invoke(runtime,new object[]{state,origin+Vector3.right,catalog}),
                            "Visibility fixture did not intersect the full-size troll's sight ray.");
                        object[] movementArgs = {state,new DotsEnemyBrain(),Vector3.zero,0.04f,false,0f,1};
                        Check(!(bool)typeof(DotsEnemyRuntime).GetMethod("MoveOnBakedDeck",flags).Invoke(runtime,movementArgs),
                            "Disabled species navigation still handled movement.");
                        catalog.UseBakedDeckNavigation = true;
                    }
                    finally { Object.DestroyImmediate(ceiling); }
                }
                var hybridNode = -1;
                var hybridTarget = Vector3.zero;
                var hybridDirections = new[] {Vector3.right,Vector3.left,Vector3.forward,Vector3.back};
                for (var n = 0; n < map.Nodes.Length && hybridNode < 0; n++)
                {
                    if (!map.Nodes[n].Boundary) continue;
                    foreach (var direction in hybridDirections)
                    {
                        var candidate = map.Nodes[n].Position + direction * 1.5f;
                        if (map.TryLocate(candidate,0.8f,out _) ||
                            !map.TryNearest(candidate,Mathf.Max(map.Width,map.Depth)*map.CellSize,0.8f,out var nearest) ||
                            nearest != n) continue;
                        hybridNode=n; hybridTarget=candidate; break;
                    }
                }
                Check(hybridNode>=0,kind+" map has no testable baked/unbaked boundary.");
                var hybridPosition=fixture.transform.TransformPoint(map.Nodes[hybridNode].Position);
                object[] hybridGround={hybridPosition,default(RaycastHit),fixture.transform,false};
                Check((bool)ground.Invoke(runtime,hybridGround),kind+" hybrid boundary has no supporting collider.");
                object[] hybridAttach={new DotsEnemyState {Id=9700+(int)kind,Scene=991,Health=100,Kind=kind,
                    Position=hybridPosition,Rotation=quaternion.identity},((RaycastHit)hybridGround[1]).collider};
                attach.Invoke(runtime,hybridAttach);
                var hybridState=(DotsEnemyState)hybridAttach[0];
                var hybridBrain=new DotsEnemyBrain {Target=9000+(int)kind,DeckNode=hybridNode,
                    DeckSupport=hybridState.SupportId,MoveTarget=fixture.transform.TransformPoint(hybridTarget)};
                hybridBrain.MoveDirection=hybridBrain.Direction=math.normalizesafe(
                    (float3)((Vector3)hybridBrain.MoveTarget-hybridPosition));
                typeof(DotsEnemyRuntime).GetField("_deckRouteBuildsRemaining",flags).SetValue(runtime,1);
                object[] leaveArgs={hybridState,hybridBrain,(Vector3)hybridBrain.MoveDirection*0.08f,0.04f,true,20f,32};
                Check(!(bool)typeof(DotsEnemyRuntime).GetMethod("MoveOnBakedDeck",flags).Invoke(runtime,leaveArgs),
                    kind+" did not hand movement from the baked map to an unbaked ship surface.");
                hybridState=(DotsEnemyState)leaveArgs[0]; hybridBrain=(DotsEnemyBrain)leaveArgs[1];
                Check(hybridBrain.DeckNode<0 && hybridBrain.DeckSupport==0,
                    kind+" retained stale baked-map state after the unbaked hand-off.");
                hybridState.Position=fixture.transform.TransformPoint(hybridTarget);
                hybridBrain.Target=9100+(int)kind; hybridBrain.MoveTarget=hybridPosition;
                hybridBrain.MoveDirection=hybridBrain.Direction=math.normalizesafe((float3)(hybridPosition-(Vector3)hybridState.Position));
                var recover=typeof(DotsEnemyRuntime).GetMethod("TryDeckRecovery",BindingFlags.Static|BindingFlags.NonPublic);
                object[] recoverArgs={map,hybridState.SupportId,hybridTarget,catalog,hybridBrain,Vector3.zero};
                Check((bool)recover.Invoke(null,recoverArgs),kind+" could not select a baked-deck return point.");
                hybridBrain=(DotsEnemyBrain)recoverArgs[4];
                Check(hybridBrain.DeckNode==hybridNode && hybridBrain.DeckSupport==hybridState.SupportId &&
                    Vector3.Distance((Vector3)recoverArgs[5],map.Nodes[hybridNode].Position)<0.001f,
                    kind+" did not steer from the unbaked bow back to the nearest deck boundary.");
                object[] returnArgs={hybridState,hybridBrain,(Vector3)hybridBrain.MoveDirection*0.08f,0.04f,true,21f,32};
                Check(!(bool)typeof(DotsEnemyRuntime).GetMethod("MoveOnBakedDeck",flags).Invoke(runtime,returnArgs),
                    kind+" did not retain collider movement while returning from a distant unbaked surface.");
                for (var run = 0; run < 2; run++)
                {
                    fixture.transform.rotation = run == 0 ? Quaternion.identity : Quaternion.Euler(3, 37, 4);
                    UnityEngine.Physics.SyncTransforms();
                    for (var a = 0; a < points.Length; a++)
                    for (var b = 0; b < points.Length; b++)
                    {
                        if (a == b) continue;
                        Check(map.TryNearest(points[a], 0.9f, 0.45f, out var start) &&
                            map.TryNearest(points[b], 0.9f, 0.45f, out _), kind+" route fixture floor missing.");
                        var position = fixture.transform.TransformPoint(map.Nodes[start].Position);
                        object[] groundArgs = { position, default(RaycastHit), fixture.transform, false };
                        Check((bool)ground.Invoke(runtime, groundArgs), "Fixture ground missing.");
                        object[] attachArgs = { new DotsEnemyState { Id = 9800 + a * 4 + b, Scene = 991, Health = 100, Kind = kind,
                            Position = position, Rotation = quaternion.identity }, ((RaycastHit)groundArgs[1]).collider };
                        attach.Invoke(runtime, attachArgs);
                        var entity = world.EntityManager.CreateEntity(typeof(DotsEnemyState), typeof(DotsEnemyBrain));
                        world.EntityManager.SetComponentData(entity, (DotsEnemyState)attachArgs[0]);
                        using var entities = new NativeArray<Entity>(new[] { entity }, Allocator.Temp);
                        var reached = false; var lastPosition = Vector3.zero;
                        for (var tick = 1; tick <= 3000 && !reached; tick++)
                        {
                            var state = world.EntityManager.GetComponentData<DotsEnemyState>(entity);
                            var brain = world.EntityManager.GetComponentData<DotsEnemyBrain>(entity);
                            var target = fixture.transform.TransformPoint(points[b]);
                            var delta = target - (Vector3)state.Position;
                            brain.Target = b; brain.MoveTarget = target; brain.TargetDistance = delta.magnitude;
                            brain.MoveDirection = brain.Direction = new Vector3(delta.x, 0, delta.z).normalized;
                            world.EntityManager.SetComponentData(entity, brain);
                            move.Invoke(runtime, new object[] { entities, tick * 0.04f });
                            state = world.EntityManager.GetComponentData<DotsEnemyState>(entity);
                            lastPosition = fixture.transform.InverseTransformPoint(state.Position);
                            reached = Vector2.Distance(new Vector2(lastPosition.x,lastPosition.z),new Vector2(points[b].x,points[b].z)) < catalog.MeleeRange * 0.82f + 0.1f &&
                                Mathf.Abs(lastPosition.y - points[b].y) < 0.45f;
                            Check(lastPosition.y < 6, "Runtime pursued deck target up the mast.");
                        }
                        if (!reached)
                        {
                            var state = world.EntityManager.GetComponentData<DotsEnemyState>(entity);
                            var brain = world.EntityManager.GetComponentData<DotsEnemyBrain>(entity);
                            map.TryNearest(points[b],1.5f,0.8f,out var goal);
                            map.TryLocate(lastPosition,map.StepHeight,out var node);
                            var budget=1; map.TryRoute(state.SupportId,b,node,goal,map.StepHeight,map.MaximumDrop,120,ref budget,out var route,5,10);
                            var next=route != null && node>=0 ? route[node] : -1;
                            var nextPoint=next>=0 ? map.Nodes[next].Position : Vector3.zero;
                            map.TryGap(node,next,out var gap,out var arc);
                            object[] landingArgs={fixture.transform.TransformPoint(nextPoint),default(RaycastHit),fixture.transform,false};
                            var found=(bool)ground.Invoke(runtime,landingArgs);
                            var hit=(RaycastHit)landingArgs[1];
                            var selected=typeof(DotsEnemyRuntime).GetMethod("DeckMap",flags).Invoke(runtime,new object[]{state.SupportId,state.Kind,Vector3.one});
                            typeof(DotsEnemyRuntime).GetField("_deckRouteBuildsRemaining",flags).SetValue(runtime,1);
                            object[] movementArgs={state,brain,(Vector3)brain.MoveDirection*0.08f,0.04f,true,121f,32};
                            var handled=typeof(DotsEnemyRuntime).GetMethod("MoveOnBakedDeck",flags).Invoke(runtime,movementArgs);
                            var resulting=(DotsEnemyState)movementArgs[0]; var resultingBrain=(DotsEnemyBrain)movementArgs[1];
                            throw new InvalidOperationException($"{kind} runtime route {a}->{b}, rotation {run}, stuck at {lastPosition}; support={state.SupportId}/{brain.DeckSupport}, swim={state.Swimming}, node={node}/{brain.DeckNode} next={next} {nextPoint} gap={gap} arc={arc}, landing={found} {hit.collider?.name} {fixture.transform.InverseTransformPoint(hit.point)} normal={hit.normal}, local={state.LocalPosition}, time={brain.LastSurfaceTime}, root={runtime.ResolveSurface(state.SupportId)?.name}, selected={selected}, handled={handled}, after={fixture.transform.InverseTransformPoint(resulting.Position)}/{resultingBrain.DeckNode}, slope={catalog.MaximumSlope}.");
                        }
                        world.EntityManager.DestroyEntity(entity);
                    }
                }
            }
            finally
            {
                if (runtime != null) typeof(DotsEnemyRuntime).GetMethod("DisposeProbes", flags).Invoke(runtime, null);
                Object.DestroyImmediate(runtimeObject); Object.DestroyImmediate(fixture); Object.DestroyImmediate(catalog); water.Dispose();
                if (master != catalog) Object.DestroyImmediate(master);
                PrefabUtility.UnloadPrefabContents(prefab);
                if (Application.isPlaying) SceneManager.UnloadSceneAsync(scene); else EditorSceneManager.CloseScene(scene, true);
                if (previousScene.IsValid() && previousScene.isLoaded) SceneManager.SetActiveScene(previousScene);
            }
        }

        private static void CheckClimbing()
        {
            // An inactive runtime avoids Awake/bootstrap and never touches live entities.
            // Fixtures are far from gameplay, exist only during this synchronous check,
            // and are removed without changing the active scene or Play Mode.
            var activeScene = SceneManager.GetActiveScene();
            var scene = Application.isPlaying ? SceneManager.CreateScene("Enemy climbing regression " + Guid.NewGuid()) :
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var root = new GameObject("Climbing check runtime");
            root.SetActive(false);
            var hull = new GameObject("Single mesh hull and deck");
            var destination = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var mesh = new Mesh();
            var catalog = ScriptableObject.CreateInstance<DotsEnemyCatalog>();
            var water = new EquipmentWaterQuery(null);
            try
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                SceneManager.MoveGameObjectToScene(hull, scene);
                SceneManager.MoveGameObjectToScene(destination, scene);
                var runtime = root.AddComponent<DotsEnemyRuntime>();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(DotsEnemyRuntime).GetProperty("Catalog").SetValue(runtime, catalog);
                typeof(DotsEnemyRuntime).GetField("_water", flags).SetValue(runtime, water);
                catalog.StepHeight = 10;
                catalog.MaximumDrop = 10;
                catalog.SurfaceTransferHeight = 20;
                catalog.MaximumSurfaceGap = 20;
                catalog.SurfaceVerticalSpeed = 4;
                catalog.MaximumSlope = 70;
                catalog.IgnoredObstacleWidth = 0;
                catalog.EnableSurfaceEdgeFollowing = false;
                catalog.EnableSurfaceEdgeDetours = false;
                catalog.EnableSurfaceTransfers = true;
                var origin = new Vector3(10000, 10000, 10000);
                hull.transform.position = origin;
                hull.AddComponent<EnemySurfaceAnchor>().Key = "Climbing source";
                destination.AddComponent<EnemySurfaceAnchor>().Key = "Climbing destination";
                mesh.vertices = new[]
                {
                    new Vector3(-1,0,-2), new Vector3(-1,0,2), new Vector3(1,0,2), new Vector3(1,0,-2),
                    new Vector3(-1,1,-2), new Vector3(-1,1,2), new Vector3(1,9,2), new Vector3(1,9,-2)
                };
                mesh.triangles = new[] { 0,1,2,0,2,3,4,5,6,4,6,7 };
                mesh.RecalculateNormals(); mesh.RecalculateBounds();
                var collider = hull.AddComponent<UnityEngine.MeshCollider>();
                collider.sharedMesh = mesh;
                destination.transform.localScale = new Vector3(3,1,4);
                destination.transform.position = origin + new Vector3(4.5f,3.5f,0);
                UnityEngine.Physics.SyncTransforms();
                runtime.RegisterSurface(hull.transform);
                runtime.RegisterSurface(destination.transform);
                Check(collider.Raycast(new UnityEngine.Ray(origin + Vector3.up * 10, Vector3.down), out var first, 20) &&
                    first.normal.y < Mathf.Cos(catalog.MaximumSlope * Mathf.Deg2Rad), "Fixture did not hit the steep hull first.");
                var recover = typeof(DotsEnemyRuntime).GetMethod("TryWalkableFace", flags);
                object[] faceArgs = { first, 20f, catalog };
                Check((bool)recover.Invoke(runtime, faceArgs) &&
                    Mathf.Abs(((RaycastHit)faceArgs[0]).point.y - origin.y) < 0.01f,
                    "The steep hull hid its own walkable deck.");
                var groundAt = typeof(DotsEnemyRuntime).GetMethod("GroundAt", flags);
                object[] groundArgs = { origin, default(RaycastHit) };
                Check((bool)groundAt.Invoke(runtime, groundArgs) &&
                    Mathf.Abs(((RaycastHit)groundArgs[1]).point.y - origin.y) < 0.01f,
                    "GroundAt missed a deck inside a compound ship mesh.");
                using (var commands = new NativeArray<RaycastCommand>(new[] {
                    new RaycastCommand(origin + Vector3.up * 10, Vector3.down,
                        new QueryParameters(~0, false, QueryTriggerInteraction.Ignore, false), 20)
                }, Allocator.TempJob))
                using (var hits = new NativeArray<RaycastHit>(8, Allocator.TempJob))
                {
                    RaycastCommand.ScheduleBatch(commands, hits, 1, 8).Complete();
                    var selected = (RaycastHit)typeof(DotsEnemyRuntime).GetMethod("SelectBatchGround", flags)
                        .Invoke(runtime, new object[] { hits, 0, false });
                    Check(selected.collider == collider && Mathf.Abs(selected.point.y - origin.y) < 0.01f,
                        "The production ground batch still rejected the deck below the hull.");
                }
                var attach = typeof(DotsEnemyRuntime).GetMethod("AttachSurface", flags);
                var begin = typeof(DotsEnemyRuntime).GetMethod("TryBeginSurfaceTransfer", flags);
                var advance = typeof(DotsEnemyRuntime).GetMethod("AdvanceSurfaceTransfer", flags);
                DotsEnemyState Start(int id)
                {
                    object[] args = { new DotsEnemyState { Id = id, Health = 60,
                        Position = origin + Vector3.right * 0.98f, Rotation = quaternion.identity }, collider };
                    attach.Invoke(runtime, args);
                    return (DotsEnemyState)args[0];
                }
                void Finish(ref DotsEnemyState state, float minimumY)
                {
                    var brain = new DotsEnemyBrain();
                    for (var frame = 0; frame < 500; frame++)
                    {
                        var previous = state.Position;
                        object[] args = { state, brain, 0.02f, 10f };
                        if (!(bool)advance.Invoke(runtime, args)) break;
                        state = (DotsEnemyState)args[0]; brain = (DotsEnemyBrain)args[1];
                        Check(math.abs(state.Position.y - previous.y) <= catalog.SurfaceVerticalSpeed * 0.02f + 0.005f,
                            "Climbing exceeded the configured vertical speed.");
                    }
                    Check(state.Position.y >= minimumY - 0.01f, "The bot failed to finish its climb.");
                }
                var state = Start(9901);
                Check((bool)begin.Invoke(runtime, new object[] { state, destination.transform.position }),
                    "Gap transfer did not start with a steep hull above the departure deck and edge following OFF.");
                Finish(ref state, origin.y + 4);
                Check(state.Position.x > origin.x + 3, "The bot did not reach the far side of the gap.");
                catalog.MaximumSurfaceGap = 1;
                Check(!(bool)begin.Invoke(runtime, new object[] { Start(9902), destination.transform.position }),
                    "Transfer ignored the maximum gap.");
                catalog.MaximumSurfaceGap = 20;
                catalog.SurfaceTransferHeight = 1;
                Check(!(bool)begin.Invoke(runtime, new object[] { Start(9903), destination.transform.position }),
                    "Transfer ignored the maximum height.");
                // A high ledge directly ahead must stay selected while the feet climb.
                destination.transform.position = origin + new Vector3(2.5f,3.5f,0);
                UnityEngine.Physics.SyncTransforms();
                Check(destination.GetComponent<UnityEngine.Collider>().Raycast(
                    new UnityEngine.Ray(origin + new Vector3(1.05f,10,0), Vector3.down), out var landing, 20), "Missing ledge fixture.");
                state = Start(9904);
                Check((bool)typeof(DotsEnemyRuntime).GetMethod("BeginGroundStep", flags)
                    .Invoke(runtime, new object[] { state, landing, 0.02f }), "Raised ledge was not committed.");
                Finish(ref state, origin.y + 4);
                Check(state.Position.x >= origin.x + 1, "Climbing did not reach the ledge.");
            }
            finally
            {
                Object.DestroyImmediate(root); Object.DestroyImmediate(hull); Object.DestroyImmediate(destination);
                Object.DestroyImmediate(mesh); Object.DestroyImmediate(catalog); water.Dispose();
                if (Application.isPlaying) SceneManager.UnloadSceneAsync(scene);
                else EditorSceneManager.CloseScene(scene, true);
                if (activeScene.IsValid() && activeScene.isLoaded) SceneManager.SetActiveScene(activeScene);
            }
        }

        private static void CheckArchPassages()
        {
            var activeScene = SceneManager.GetActiveScene();
            var scene = Application.isPlaying ? SceneManager.CreateScene("Enemy arch regression " + Guid.NewGuid()) :
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var root = new GameObject("Arch check runtime");
            root.SetActive(false);
            var fixture = new GameObject("Arch fixture");
            var origin = new Vector3(10000, 15000, 10000);
            fixture.transform.position = origin;
            var catalog = ScriptableObject.CreateInstance<DotsEnemyCatalog>();
            var water = new EquipmentWaterQuery(null);
            var mesh = new Mesh();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            DotsEnemyRuntime runtime = null;
            using var world = new World("Arch movement regression");
            using var query = world.EntityManager.CreateEntityQuery(typeof(DotsEnemyState), typeof(DotsEnemyBrain));
            try
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                SceneManager.MoveGameObjectToScene(fixture, scene);
                runtime = root.AddComponent<DotsEnemyRuntime>();
                typeof(DotsEnemyRuntime).GetProperty("Catalog").SetValue(runtime, catalog);
                typeof(DotsEnemyRuntime).GetField("_water", flags).SetValue(runtime, water);
                typeof(DotsEnemyRuntime).GetField("_serverWorld", flags).SetValue(runtime, world);
                typeof(DotsEnemyRuntime).GetField("_enemies", flags).SetValue(runtime, query);
                typeof(DotsEnemyRuntime).GetField("_scene", flags).SetValue(runtime, 991);
                catalog.StepHeight = catalog.MaximumDrop = 10;
                catalog.BodyHeight = 1.7f; catalog.BodyRadius = 0.1f;
                catalog.MoveSpeed = 2; catalog.SurfaceVerticalSpeed = 4;
                catalog.IgnoredObstacleWidth = 3;
                catalog.EnableCrowdCollisions = catalog.EnableCrowdAvoidance = false;
                catalog.EnableSurfaceContinuityChecks = true;
                catalog.UseBakedDeckNavigation = false;
                GameObject Box(string name, Vector3 position, Vector3 size)
                {
                    var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    box.name = name;
                    box.transform.SetParent(fixture.transform, false);
                    box.transform.localPosition = position;
                    box.transform.localScale = size;
                    return box;
                }
                var floor = Box("Ground", new Vector3(0, -0.5f, 0), new Vector3(20, 1, 20));
                var left = Box("Left post", new Vector3(-2.5f, 1.8f, 0), new Vector3(0.25f, 3.6f, 1));
                var right = Box("Right post", new Vector3(2.5f, 1.8f, 0), new Vector3(0.25f, 3.6f, 1));
                var beam = Box("Overhead beam", new Vector3(0, 3.8f, 0), new Vector3(5.25f, 0.4f, 1));
                var entity = world.EntityManager.CreateEntity(typeof(DotsEnemyState), typeof(DotsEnemyBrain));
                using var entities = new NativeArray<Entity>(new[] { entity }, Allocator.Temp);
                var move = typeof(DotsEnemyRuntime).GetMethod("MoveBatch", flags);
                var transfers = (System.Collections.IDictionary)typeof(DotsEnemyRuntime)
                    .GetField("_surfaceTransfers", flags).GetValue(runtime);
                var peakHeight = 0f;
                DotsEnemyState Traverse(float startHeight, bool keepHeight, string label, float slope = 0)
                {
                    peakHeight = startHeight;
                    transfers.Clear();
                    world.EntityManager.SetComponentData(entity, new DotsEnemyState {
                        Id = 99001, Scene = 991, Health = 60, Rotation = quaternion.identity,
                        Position = origin + new Vector3(0, startHeight, -2) });
                    world.EntityManager.SetComponentData(entity, new DotsEnemyBrain {
                        Target = -1, TargetDistance = 100, Direction = new float3(0, 0, 1),
                        MoveDirection = new float3(0, 0, 1) });
                    UnityEngine.Physics.SyncTransforms();
                    for (var frame = 1; frame <= 20; frame++)
                    {
                        move.Invoke(runtime, new object[] { entities, frame * 0.1f });
                        var state = world.EntityManager.GetComponentData<DotsEnemyState>(entity);
                        peakHeight = Mathf.Max(peakHeight, state.Position.y - origin.y);
                        var expectedHeight = startHeight - (state.Position.z - origin.z + 2) * slope;
                        if (keepHeight) Check(Mathf.Abs(state.Position.y - origin.y - expectedHeight) < 0.02f,
                            label + ": climbed overhead geometry or lost the current floor.");
                    }
                    return world.EntityManager.GetComponentData<DotsEnemyState>(entity);
                }
                Check(Traverse(0, true, "Separate colliders").Position.z > origin.z + 1.5f,
                    "The bot did not walk through the arch.");
                floor.transform.localRotation = Quaternion.Euler(45, 0, 0);
                floor.transform.localPosition = Vector3.down * (0.5f / Mathf.Cos(45 * Mathf.Deg2Rad));
                Check(Traverse(2, true, "Arch on a slope", 1).Position.z > origin.z + 1.5f,
                    "The supporting hillside was mistaken for an obstruction under the arch.");
                floor.transform.localRotation = Quaternion.identity;
                floor.transform.localPosition = Vector3.down * 0.5f;
                var parts = new[] { floor, left, right, beam };
                var combined = new CombineInstance[parts.Length];
                for (var i = 0; i < parts.Length; i++)
                {
                    combined[i] = new CombineInstance { mesh = parts[i].GetComponent<MeshFilter>().sharedMesh,
                        transform = Matrix4x4.TRS(parts[i].transform.localPosition, Quaternion.identity,
                            parts[i].transform.localScale) };
                    parts[i].GetComponent<UnityEngine.Collider>().enabled = false;
                }
                mesh.CombineMeshes(combined);
                var compound = fixture.AddComponent<UnityEngine.MeshCollider>();
                compound.sharedMesh = mesh;
                Check(Traverse(0, true, "Single mesh collider").Position.z > origin.z + 1.5f,
                    "The bot did not pass below the beam in the same mesh as the floor.");
                compound.enabled = false;
                foreach (var part in parts) part.GetComponent<UnityEngine.Collider>().enabled = true;

                // A low passage and a solid block still require climbing. The ground
                // under a closed mesh must not be mistaken for an accessible tunnel.
                beam.transform.localPosition = new Vector3(0, 1.6f, 0);
                Traverse(0, false, "Low doorway");
                Check(peakHeight > 1.7f,
                    "A low doorway was treated as a free passage.");
                beam.transform.localPosition = new Vector3(0, 2, 0);
                beam.transform.localScale = new Vector3(5.25f, 4, 1);
                Traverse(0, false, "Solid box");
                Check(peakHeight > 3.9f,
                    "The bot walked inside a solid box.");
                var closedMesh = beam.AddComponent<UnityEngine.MeshCollider>();
                closedMesh.sharedMesh = beam.GetComponent<MeshFilter>().sharedMesh;
                beam.GetComponent<UnityEngine.BoxCollider>().enabled = false;
                Traverse(0, false, "Closed mesh");
                Check(peakHeight > 3.9f,
                    "The bot walked through the wall into a closed mesh.");
                // Extend the upper platform across the entire path. A bot already on
                // it must stay on top, despite the lower floor beneath the platform.
                beam.transform.localPosition = new Vector3(0, 3.8f, 0);
                beam.transform.localScale = new Vector3(5.25f, 0.4f, 8);
                Check(Traverse(4, true, "Upper floor").Position.z > origin.z + 1.5f,
                    "The bot failed to keep walking on its upper floor.");
            }
            finally
            {
                if (runtime != null) typeof(DotsEnemyRuntime).GetMethod("DisposeProbes", flags).Invoke(runtime, null);
                Object.DestroyImmediate(root); Object.DestroyImmediate(fixture);
                Object.DestroyImmediate(mesh); Object.DestroyImmediate(catalog); water.Dispose();
                if (Application.isPlaying) SceneManager.UnloadSceneAsync(scene);
                else EditorSceneManager.CloseScene(scene, true);
                if (activeScene.IsValid() && activeScene.isLoaded) SceneManager.SetActiveScene(activeScene);
            }
        }

        [MenuItem("Tools/Wave by Wave/Enemies/Capture boarding diagnostics")]
        public static void CaptureBoardingSnapshot()
        {
            var output = new System.Text.StringBuilder();
            var runtime = DotsEnemyRuntime.Instance;
            var world = Unity.NetCode.ClientServerBootstrap.ServerWorld;
            output.AppendLine($"Playing={EditorApplication.isPlaying}; server={runtime != null && runtime.CanSimulate}");
            if (runtime == null || world == null || !world.IsCreated)
            {
                File.WriteAllText("Temp/EnemyBoardingSnapshot.result", output.ToString());
                return;
            }
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var groundAt = typeof(DotsEnemyRuntime).GetMethod("GroundAt", flags);
            var transferAt = typeof(DotsEnemyRuntime).GetMethod("TransferGroundAt", flags);
            var water = (EquipmentWaterQuery)typeof(DotsEnemyRuntime).GetField("_water", flags).GetValue(runtime);
            var transfers = (System.Collections.IDictionary)typeof(DotsEnemyRuntime).GetField("_surfaceTransfers", flags).GetValue(runtime);
            var catalog = runtime.Catalog;
            output.AppendLine($"catalog={AssetDatabase.GetAssetPath(catalog)}");
            output.AppendLine($"time={runtime.Now}; step={catalog.StepHeight}; drop={catalog.MaximumDrop}; gap={catalog.MaximumSurfaceGap}; transferHeight={catalog.SurfaceTransferHeight}; activeTransfers={transfers.Count}");
            using var query = world.EntityManager.CreateEntityQuery(typeof(DotsEnemyState), typeof(DotsEnemyBrain));
            using var entities = query.ToEntityArray(Allocator.Temp);
            var sampled = 0;
            string Hit(RaycastHit hit) => hit.collider == null ? "none" :
                $"{hit.collider.name} y={hit.point.y:F3} normalY={hit.normal.y:F3} root={hit.collider.transform.root.name}";
            foreach (var entity in entities)
            {
                var state = world.EntityManager.GetComponentData<DotsEnemyState>(entity);
                if (state.Health <= 0 || !DotsEnemyRuntime.IsShipSurface(state.SupportId)) continue;
                var brain = world.EntityManager.GetComponentData<DotsEnemyBrain>(entity);
                output.AppendLine($"Bot={state.Id} type={state.CombatType} pos={state.Position} support={state.SupportId} target={brain.Target} distance={brain.TargetDistance:F3} attacking={brain.Attacking} stunnedUntil={state.StunUntil:F3} blocked={brain.CrowdBlockedTime:F3} moving={brain.MoveDirection} updated={state.MovementUpdatedAt:F3} transfer={transfers.Contains(state.Id)}");
                if (++sampled > 16) break;
                if (water != null && water.TryWaterLevel(state.Position, out var level)) output.AppendLine($"  water={level:F3}");
                var root = runtime.ResolveSurface(state.SupportId);
                object[] sourceArgs = { (Vector3)state.Position, default(RaycastHit), root };
                var source = (bool)transferAt.Invoke(runtime, sourceArgs);
                output.AppendLine($"  departure={source} {Hit((RaycastHit)sourceArgs[1])}; root={(root == null ? "none" : root.name)}");
                var direction = math.normalizesafe(brain.Direction);
                foreach (var distance in new[] { 0f, 0.2f, 0.5f, 1f, 2f, 5f, 10f })
                {
                    var point = (Vector3)(state.Position + direction * distance);
                    object[] args = { point, default(RaycastHit) };
                    var ground = (bool)groundAt.Invoke(runtime, args);
                    object[] transferArgs = { point, default(RaycastHit), null };
                    var landing = (bool)transferAt.Invoke(runtime, transferArgs);
                    output.AppendLine($"  d={distance:F2}: ground={ground} {Hit((RaycastHit)args[1])}; transfer={landing} {Hit((RaycastHit)transferArgs[1])}");
                }
            }
            output.AppendLine($"Living bots on DOTS ship sampled={sampled}; total entities={entities.Length}");
            File.WriteAllText("Temp/EnemyBoardingSnapshot.result", output.ToString());
        }

        [MenuItem("Tools/Wave by Wave/Enemies/Check pursuit, edges and hull contacts")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Run outside Play Mode.");
            CheckSkeletonCatalogBinding();
            CheckSteering();
            CheckSkeletonCrowd();
            CheckHullCast();
            CheckAuthoredHull();
            CheckEdgeBudget();
            CheckMovementStability();
            CheckRenderBounds();
            CheckClimbing();
            CheckArchPassages();
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
                        var brain = new DotsEnemyBrain();
                        for (var step = 0; step < 150; step++)
                        {
                            object[] args = { state, brain, goal, 0.05f, default(RaycastHit) };
                            if (!(bool)follow.Invoke(runtime, args)) break;
                            brain = (DotsEnemyBrain)args[1];
                            state.Position = ((RaycastHit)args[4]).point;
                            var arrived = Quaternion.Inverse(rotation) * ((Vector3)state.Position - origin);
                            if (Mathf.Abs(arrived.z - lateral) < 0.2f) break;
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
                var detour = new DotsEnemyState { Id = 8001, SupportId = key, Position = origin + Vector3.right * 0.999f };
                var detourBrain = new DotsEnemyBrain();
                var detourSign = 0f;
                for (var step = 0; step < 10; step++)
                {
                    var before = detour.Position;
                    object[] args = { detour, detourBrain, origin + Vector3.right * 5, 0.05f, default(RaycastHit) };
                    Check((bool)follow.Invoke(runtime, args), "Bot stopped at a local distance minimum instead of searching along the railing.");
                    detourBrain = (DotsEnemyBrain)args[1];
                    detour.Position = ((RaycastHit)args[4]).point;
                    var dz = detour.Position.z - before.z;
                    if (step == 0) detourSign = Mathf.Sign(dz);
                    Check(dz * detourSign > 0 && Mathf.Abs(detour.Position.x - origin.x) <= 1.002f,
                        "Edge detour reversed itself or left the supporting deck.");
                }
                Check(Mathf.Abs(detour.Position.z - origin.z) > 1.5f, "Edge detour did not make useful lateral progress.");
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
                destination.transform.position = origin + Vector3.right * 2 - Vector3.up * 0.5f;
                UnityEngine.Physics.SyncTransforms();
                Check((bool)begin.Invoke(runtime, new object[] { Start(id++, Quaternion.identity), origin + Vector3.right * 3 }),
                    "Touching decks required an empty water sample to change support.");
                destination.transform.position = origin + Vector3.right * 1.8f - Vector3.up * 0.5f;
                UnityEngine.Physics.SyncTransforms();
                Check((bool)begin.Invoke(runtime, new object[] { Start(id++, Quaternion.identity), origin + Vector3.right * 3 }),
                    "Overlapping deck edges could not change support.");
                foreach (var gap in new[] { 9.9f, 10f, 10.1f, 19.9f, 20f, 20.1f })
                {
                    catalog.MaximumSurfaceGap = gap < 15 ? 10 : 20;
                    destination.transform.position = origin + Vector3.right * (2 + gap) - Vector3.up * 0.5f;
                    UnityEngine.Physics.SyncTransforms();
                    Check((bool)begin.Invoke(runtime, new object[] { Start(id++, Quaternion.identity), destination.transform.position }) ==
                        (gap <= catalog.MaximumSurfaceGap), $"Extended gap limit failed at {gap} metres.");
                }
                catalog.MaximumSurfaceGap = 1;
                destination.transform.position = origin + Vector3.right * 2.7f - Vector3.up * 0.5f;
                destination.transform.position += Vector3.up * 1.5f;
                UnityEngine.Physics.SyncTransforms();
                Check(!(bool)begin.Invoke(runtime, new object[] { Start(id++, Quaternion.identity), origin + Vector3.right * 3 }),
                    "Walking transfer ignored its height limit.");
            }
            finally { Object.DestroyImmediate(destination); Object.DestroyImmediate(water); }
        }
    }
}
