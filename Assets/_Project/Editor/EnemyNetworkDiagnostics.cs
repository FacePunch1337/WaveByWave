using System;
using System.IO;
using System.Linq;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using WaveByWave.Enemies;

namespace WaveByWave.Editor
{
    // Isolated edit-mode worlds: does not start NGO/Steam, enter Play Mode or modify a scene.
    public static class EnemyNetworkDiagnostics
    {
        private static World _server, _client, _late;
        private static Entity _enemy;
        private static int _tick;
        private static double _time;
        private static NetworkEndpoint _endpoint;
        private static bool _running;
        private static string _error;
        private static bool _succeeded;
        private const int TestCount = 3000;
        [InitializeOnLoadMethod]
        private static void Schedule()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
        }

        public static void RunBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("RunBatch is for an isolated batch-mode editor only.");
            Start();
            while (_running) { Update(); System.Threading.Thread.Sleep(16); }
            EditorApplication.Exit(_succeeded ? 0 : 1);
        }

        [MenuItem("Tools/Wave by Wave/Enemies/Test local ghosts and late join")]
        public static void Start()
        {
            if (_running || EditorApplication.isPlayingOrWillChangePlaymode) return;
            try
            {
                _error = null; _succeeded = false;
                Directory.CreateDirectory("Temp/EnemyDiagnostics");
                File.WriteAllText("Temp/EnemyDiagnostics/network.txt", "RUNNING: 3000 ghosts");
                Application.logMessageReceived += CaptureError;
                _server = Create(true, "EnemyValidation.Server");
                _client = Create(false, "EnemyValidation.Client");
                if (_error != null) throw new InvalidOperationException(_error);
                _tick = 0; _time = 0;
                _endpoint = NetworkEndpoint.LoopbackIpv4.WithPort(23879);
                using var query = _server.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver));
                if (!query.GetSingletonRW<NetworkStreamDriver>().ValueRW.Listen(_endpoint))
                    throw new InvalidOperationException("Local enemy test could not bind port 23879.");
                Connect(_client);
                _running = true;
                EditorApplication.update += Update;
            }
            catch (Exception exception) { Fail(exception); }
        }
        private static World Create(bool server, string name)
        {
            var previous = NetworkStreamReceiveSystem.DriverConstructor;
            NetworkStreamReceiveSystem.DriverConstructor = null;
            var world = new World(name, server ? WorldFlags.GameServer : WorldFlags.GameClient);
            try
            {
                var systems = DefaultWorldInitialization.GetAllSystems(server ? WorldSystemFilterFlags.ServerSimulation :
                    WorldSystemFilterFlags.ClientSimulation).Where(t =>
                    (t.Assembly.GetName().Name == "Unity.NetCode" || t.Assembly.GetName().Name == "Unity.Entities" ||
                     t == typeof(EnemyGhostRegistrationSystem) || t.Name.Contains("DotsEnemyStateSerializer"))).ToArray();
                // Include the generated serialization registration system from the runtime assembly.
                systems = systems.Concat(DefaultWorldInitialization.GetAllSystems(server ? WorldSystemFilterFlags.ServerSimulation :
                    WorldSystemFilterFlags.ClientSimulation).Where(t => t.Assembly == typeof(DotsEnemyState).Assembly &&
                    typeof(IGhostComponentSerializerRegistration).IsAssignableFrom(t))).Distinct().ToArray();
                var sorted = systems.ToList();
                TypeManager.SortSystemTypesInCreationOrder(sorted);
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, sorted);
                using var debugQuery = world.EntityManager.CreateEntityQuery(typeof(NetDebug));
                debugQuery.GetSingletonRW<NetDebug>().ValueRW.SuppressApplicationRunInBackgroundWarning = true;
                return world;
            }
            catch { world.Dispose(); throw; }
            finally { NetworkStreamReceiveSystem.DriverConstructor = previous; }
        }
        private static void Connect(World world)
        {
            using var query = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver));
            query.GetSingletonRW<NetworkStreamDriver>().ValueRW.Connect(world.EntityManager, _endpoint);
        }
        private static void Tick(World world)
        {
            if (world == null) return;
            world.SetTime(new TimeData(_time, 1f / 60));
            world.GetExistingSystemManaged<InitializationSystemGroup>()?.Update();
            world.GetExistingSystemManaged<SimulationSystemGroup>()?.Update();
            using var q = world.EntityManager.CreateEntityQuery(new EntityQueryDesc
            { All = new[] { ComponentType.ReadOnly<NetworkId>() }, None = new[] { ComponentType.ReadOnly<NetworkStreamInGame>() } });
            world.EntityManager.AddComponent<NetworkStreamInGame>(q);
        }
        private static void Update()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Cleanup(); return; }
            try
            {
                _time += 1.0 / 60; _tick++;
                Tick(_server); Tick(_client); Tick(_late);
                if (_error != null) throw new InvalidOperationException(_error);
                if (_tick == 5)
                {
                    using var q = _server.EntityManager.CreateEntityQuery(typeof(DotsEnemyPrefab));
                    _enemy = _server.EntityManager.Instantiate(q.GetSingleton<DotsEnemyPrefab>().Value);
                    _server.EntityManager.SetComponentData(_enemy, new DotsEnemyState
                    { Id = 101, Scene = 7, Seed = 819, Health = 60, Rotation = quaternion.identity,
                        LocalRotation = quaternion.identity, Position = new float3(3, 4, 5), SupportId = 42 });
                    for (var i = 1; i < TestCount; i++)
                    {
                        var other = _server.EntityManager.Instantiate(q.GetSingleton<DotsEnemyPrefab>().Value);
                        var state = _server.EntityManager.GetComponentData<DotsEnemyState>(_enemy);
                        state.Id = i + 1000; state.Position.x += i * 0.1f;
                        _server.EntityManager.SetComponentData(other, state);
                    }
                }
                if (_tick == 600)
                {
                    Require(_client, 60, 42);
                    _late = Create(false, "EnemyValidation.LateClient"); Connect(_late);
                }
                if (_tick == 1200)
                {
                    Require(_late, 60, 42);
                    var state = _server.EntityManager.GetComponentData<DotsEnemyState>(_enemy);
                    state.Health = 25; state.HitRevision = 1; state.StunUntil = 22; state.SupportId = 77;
                    _server.EntityManager.SetComponentData(_enemy, state);
                }
                if (_tick == 1380)
                {
                    Require(_client, 25, 77); Require(_late, 25, 77);
                    _server.EntityManager.DestroyEntity(_enemy);
                }
                if (_tick >= 1560)
                {
                    using var q = _client.EntityManager.CreateEntityQuery(typeof(DotsEnemyState));
                    using var q2 = _late.EntityManager.CreateEntityQuery(typeof(DotsEnemyState));
                    if (q.CalculateEntityCount() != TestCount - 1 || q2.CalculateEntityCount() != TestCount - 1)
                        throw new InvalidOperationException("Enemy despawn did not replicate.");
                    Directory.CreateDirectory("Temp/EnemyDiagnostics");
                    File.WriteAllText("Temp/EnemyDiagnostics/network.txt", "PASS: 3000 loopback NFE ghosts, late join, damage/stun/support update, despawn.");
                    Debug.Log("[Enemies] PASS: 3000 isolated NFE ghosts, late join, damage/support update, despawn.");
                    _succeeded = true;
                    Cleanup();
                }
            }
            catch (Exception exception) { Fail(exception); }
        }
        private static void Require(World world, float health, ulong support)
        {
            using var q = world.EntityManager.CreateEntityQuery(typeof(DotsEnemyState));
            using var values = q.ToComponentDataArray<DotsEnemyState>(Allocator.Temp);
            var found = default(DotsEnemyState);
            foreach (var state in values) if (state.Id == 101) found = state;
            if (values.Length != TestCount || found.Id != 101 || found.Seed != 819 ||
                math.abs(found.Health - health) > 0.01f || found.SupportId != support ||
                health < 60 && (found.HitRevision != 1 || math.abs(found.StunUntil - 22) > 0.01f))
                throw new InvalidOperationException($"{world.Name}: snapshot mismatch at tick {_tick}, count={values.Length}. " +
                    JsonUtility.ToJson(found));
        }
        private static void Fail(Exception exception)
        {
            Directory.CreateDirectory("Temp/EnemyDiagnostics");
            File.WriteAllText("Temp/EnemyDiagnostics/network.txt", "FAIL: " + exception);
            Debug.LogException(exception); Cleanup();
        }
        private static void CaptureError(string message, string trace, LogType type)
        {
            if (type is LogType.Exception or LogType.Error or LogType.Assert) _error ??= message;
        }
        private static void Cleanup()
        {
            Application.logMessageReceived -= CaptureError;
            EditorApplication.update -= Update;
            _late?.Dispose(); _client?.Dispose(); _server?.Dispose();
            _server = _client = _late = null; _running = false;
        }
    }
}
