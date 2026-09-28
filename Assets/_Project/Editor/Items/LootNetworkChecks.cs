using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Networking.Transport;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using WaveByWave.Items;
using WaveByWave.Networking;

namespace WaveByWave.Editor.Items
{
    // Isolated worlds, no scenes or shared loot state are modified by these checks.
    public static class LootNetworkChecks
    {
        private const string Request = "Temp/LootNetworkChecks.request";
        private const string Result = "Temp/LootNetworkChecks.result";
        private static readonly StringBuilder Report = new();
        private static World _server, _client;
        private static double _time;
        private static int _ticks, _received;
        private static bool _sent, _initialReceived;
        private static string _error;
        private static bool _finished;
        private static object[] _sessionValues;
        private static readonly string[] SessionFields = { "_transport", "_virtualPort", "_hostSteamId", "_localAddress", "_driversNeedReset" };

        [InitializeOnLoadMethod]
        private static void Install()
        {
            EditorApplication.update += Poll;
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
        }

        private static void Poll()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (File.GetLastWriteTimeUtc("Assets/_Project/Editor/Items/LootNetworkChecks.cs") >
                File.GetLastWriteTimeUtc(typeof(LootNetworkChecks).Assembly.Location) ||
                File.GetLastWriteTimeUtc("Assets/_Project/Runtime/Items/LootStressTest.cs") >
                File.GetLastWriteTimeUtc(typeof(WorldLootSnapshotRpc).Assembly.Location))
            { AssetDatabase.Refresh(); return; }
            var command = File.ReadAllText(Request).Trim();
            File.Delete(Request);
            if (command == "build") BuildWindows();
            else Run();
        }

        [MenuItem("Tools/Wave by Wave/Items/Build Windows with clean network cache")]
        private static void BuildWindows()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            try
            {
                // CleanBuildCache invalidates Bee's player outputs. Also discard the AOT
                // compiler cache: a stale native RPC can otherwise survive a managed rebuild.
                var library = Path.GetFullPath("Library") + Path.DirectorySeparatorChar;
                var aotCache = Path.GetFullPath("Library/BurstCache/Windows-Intel");
                if (!aotCache.StartsWith(library, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Burst cache must be inside this project's Library.");
                if (Directory.Exists(aotCache)) Directory.Delete(aotCache, true);
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                    locationPathName = "WaveByWave-Windows/WaveByWave.exe",
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.CleanBuildCache
                });
                File.WriteAllText("Temp/LootNetworkBuild.result", report.summary.result + ": " +
                    report.summary.totalErrors + " errors, " + report.summary.totalTime);
                if (report.summary.result != BuildResult.Succeeded) Debug.LogError("Loot network build failed.");
            }
            catch (Exception e) { File.WriteAllText("Temp/LootNetworkBuild.result", "FAIL: " + e); }
        }

        [MenuItem("Tools/Wave by Wave/Items/Check loot network replication")]
        public static void Run()
        {
            if (_server != null || EditorApplication.isPlayingOrWillChangePlaymode) return;
            Report.Clear();
            _error = null;
            _ticks = _received = 0;
            _time = 0;
            _sent = _initialReceived = false;
            _finished = false;
            try
            {
                WireCheck(new WorldLootSnapshotRpc { Revision = 123, Count = 3000,
                    Center = new float3(13, -5, 81), Radius = 30, Seed = 5319,
                    SceneName = new FixedString64Bytes("Ocean") });
                WireCheck(new WorldLootSnapshotRpc { Revision = 124, Count = 0,
                    Center = new float3(-3, 7, 55), Radius = 15, Seed = 5319,
                    SceneName = new FixedString64Bytes("Port") });
                WireCheck(Delta(1));
                WireCheck(new WorldLootSnapshotRequestRpc { ProtocolVersion = 1 });
                File.WriteAllText(Result, Report + "RUNNING: isolated RPC connection");
                Application.logMessageReceived += CaptureError;
                _sessionValues = SessionFields.Select(n => typeof(SteamNetcodeSession)
                    .GetField(n, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).ToArray();
                SteamNetcodeSession.ConfigureLocal(23883, "127.0.0.1");
                _server = Create(true);
                _client = Create(false);
                using var sq = _server.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver));
                var endpoint = NetworkEndpoint.LoopbackIpv4.WithPort(23883);
                if (!sq.GetSingletonRW<NetworkStreamDriver>().ValueRW.Listen(endpoint))
                    throw new InvalidOperationException("Could not listen on loopback.");
                using var cq = _client.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver));
                cq.GetSingletonRW<NetworkStreamDriver>().ValueRW.Connect(_client.EntityManager, endpoint);
                EditorApplication.update += Update;
            }
            catch (Exception e) { Fail(e); }
        }

        private static void WireCheck<T>(T value) where T : unmanaged, IRpcCommand
        {
            var serializerType = typeof(T).Assembly.GetTypes().Single(t =>
                t.IsValueType && typeof(IRpcCommandSerializer<T>).IsAssignableFrom(t));
            var serializer = (IRpcCommandSerializer<T>)Activator.CreateInstance(serializerType);
            var writer = new DataStreamWriter(1024, Allocator.Temp);
            serializer.Serialize(ref writer, default, value);
            var reader = new DataStreamReader(writer.AsNativeArray());
            var decoded = default(T);
            serializer.Deserialize(ref reader, default, ref decoded);
            var hash = TypeManager.GetTypeInfo<T>().StableTypeHash;
            if (reader.HasFailedReads || reader.GetBitsRead() != writer.LengthInBits || !decoded.Equals(value))
                throw new InvalidOperationException($"{typeof(T).Name}: wire roundtrip failed: " +
                    $"{reader.GetBitsRead()}/{writer.LengthInBits} bits. {JsonUtility.ToJson(decoded)}");
            Report.AppendLine($"PASS: {typeof(T).Name}, hash={hash}, payload={writer.Length} B");
            using var world = new World("LootChecks.Wire");
            using var commands = new EntityCommandBuffer(Allocator.TempJob);
            var statePtr = Marshal.AllocHGlobal(UnsafeUtility.SizeOf<RpcDeserializerState>());
            try
            {
                Marshal.Copy(new byte[UnsafeUtility.SizeOf<RpcDeserializerState>()], 0, statePtr,
                    UnsafeUtility.SizeOf<RpcDeserializerState>());
                object boxed = new RpcExecutor.Parameters { Reader = new DataStreamReader(writer.AsNativeArray()),
                    CommandBuffer = commands.AsParallelWriter() };
                typeof(RpcExecutor.Parameters).GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(boxed, statePtr);
                var parameters = (RpcExecutor.Parameters)boxed;
                var pointer = typeof(PortableFunctionPointer<RpcExecutor.ExecuteDelegate>)
                    .GetField("Ptr", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(serializer.CompileExecute());
                var invoke = (RpcExecutor.ExecuteDelegate)pointer.GetType().GetProperty("Invoke").GetValue(pointer);
                invoke(ref parameters);
                if (parameters.Reader.HasFailedReads || parameters.Reader.GetBitsRead() != writer.LengthInBits)
                    throw new InvalidOperationException($"{typeof(T).Name}: compiled RPC read " +
                        $"{parameters.Reader.GetBitsRead()}/{writer.LengthInBits} bits.");
                commands.Playback(world.EntityManager);
                using var q = world.EntityManager.CreateEntityQuery(typeof(T));
                if (!q.GetSingleton<T>().Equals(value)) throw new InvalidOperationException("Compiled RPC payload mismatch.");
                Report.AppendLine($"PASS: {typeof(T).Name} compiled RPC executor");
            }
            finally { Marshal.FreeHGlobal(statePtr); }
        }

        private static WorldLootDeltaRpc Delta(int id) => new()
        {
            Revision = 123, Id = -id, Kind = 3, CatalogIndex = id % 80,
            StartPosition = new float3(1, 2, 3), Position = new float3(4, 5, 6),
            Rotation = quaternion.Euler(0.1f, 0.2f, 0.3f), BaseRotation = quaternion.identity,
            ArcUp = new float3(0, 1, 0), Duration = 0.5f, ArcHeight = 2,
            HookOwner = ulong.MaxValue, SupportId = 987654321, LocalPosition = new float3(-8, 2, 11),
            LocalRotation = quaternion.Euler(0, 1, 0), LocalStart = new float3(3, 2, 1),
            LocalArcUp = new float3(1, 0, 0), HookOffset = new float3(7, 8, 9),
            OnWater = true, IslandId = 42, FadeDuration = 1.5f, OpeningAt = 12345.6789
        };

        private static World Create(bool server)
        {
            var previous = NetworkStreamReceiveSystem.DriverConstructor;
            NetworkStreamReceiveSystem.DriverConstructor = (INetworkStreamDriverConstructor)Activator.CreateInstance(
                typeof(SteamNetcodeSession).Assembly.GetType("WaveByWave.Networking.SteamNetworkDriverConstructor"), true);
            var world = new World(server ? "LootChecks.Server" : "LootChecks.Client",
                server ? WorldFlags.GameServer : WorldFlags.GameClient);
            try
            {
                var systems = DefaultWorldInitialization.GetAllSystems(server
                        ? WorldSystemFilterFlags.ServerSimulation : WorldSystemFilterFlags.ClientSimulation)
                    .Where(t => t.Assembly.GetName().Name is "Unity.NetCode" or "Unity.Entities" ||
                        t.Assembly == typeof(WorldLootSnapshotRpc).Assembly &&
                        t.Name.Contains("WorldLoot") && t.Name.EndsWith("RpcCommandRequestSystem") ||
                        t.FullName == "Unity.Transforms.TransformSystemGroup").ToList();
                TypeManager.SortSystemTypesInCreationOrder(systems);
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);
                using var q = world.EntityManager.CreateEntityQuery(typeof(NetDebug));
                q.GetSingletonRW<NetDebug>().ValueRW.SuppressApplicationRunInBackgroundWarning = true;
                return world;
            }
            catch { world.Dispose(); throw; }
            finally { NetworkStreamReceiveSystem.DriverConstructor = previous; }
        }

        private static void Tick(World world)
        {
            world.SetTime(new TimeData(_time, 1f / 60));
            world.GetExistingSystemManaged<InitializationSystemGroup>()?.Update();
            world.GetExistingSystemManaged<SimulationSystemGroup>()?.Update();
        }

        private static void Send<T>(T value) where T : unmanaged, IRpcCommand
        {
            var e = _server.EntityManager.CreateEntity();
            _server.EntityManager.AddComponentData(e, value);
            _server.EntityManager.AddComponent<SendRpcCommandRequest>(e);
        }

        private static void Update()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Cleanup(); return; }
            try
            {
                _time += 1d / 60; _ticks++;
                Tick(_server); Tick(_client);
                if (_ticks % 100 == 0) File.WriteAllText(Result,
                    Report + $"RUNNING: tick={_ticks}, sent={_sent}, initial={_initialReceived}, received={_received}");
                if (_error != null) throw new InvalidOperationException(_error);
                using var connected = _client.EntityManager.CreateEntityQuery(typeof(NetworkId));
                if (!_sent && !connected.IsEmptyIgnoreFilter)
                {
                    Send(new WorldLootSnapshotRpc { Revision = 123, Count = 0, Seed = 5319,
                        SceneName = new FixedString64Bytes("Ocean") });
                    for (var i = 1; i <= 3000; i++) Send(Delta(i));
                    _sent = true;
                }
                using var initials = _client.EntityManager.CreateEntityQuery(typeof(WorldLootSnapshotRpc), typeof(ReceiveRpcCommandRequest));
                using var values = initials.ToComponentDataArray<WorldLootSnapshotRpc>(Allocator.Temp);
                foreach (var value in values)
                {
                    if (value.Revision != 123 || value.SceneName.ToString() != "Ocean")
                        throw new InvalidOperationException("Snapshot header mismatch.");
                    _initialReceived = true;
                }
                _client.EntityManager.DestroyEntity(initials);
                using var deltas = _client.EntityManager.CreateEntityQuery(typeof(WorldLootDeltaRpc), typeof(ReceiveRpcCommandRequest));
                using var items = deltas.ToComponentDataArray<WorldLootDeltaRpc>(Allocator.Temp);
                foreach (var item in items)
                {
                    if (!item.Equals(Delta(-item.Id))) throw new InvalidOperationException("Item payload mismatch.");
                    _received++;
                }
                _client.EntityManager.DestroyEntity(deltas);
                if (_initialReceived && _received == 3000)
                {
                    Report.AppendLine("PASS: initial snapshot and 3000 item RPCs received through the game's UDP bridge.");
                    File.WriteAllText(Result, Report.ToString());
                    _finished = true;
                }
                else if (_ticks > 1200) throw new TimeoutException($"Snapshot={_initialReceived}, items={_received}/3000.");
            }
            catch (Exception e) { Fail(e); }
            if (_finished) Cleanup();
        }

        private static void CaptureError(string message, string stack, LogType type)
        {
            if (type is LogType.Exception or LogType.Error or LogType.Assert) _error ??= message;
        }

        private static void Fail(Exception e)
        {
            File.WriteAllText(Result, Report + "FAIL: " + e);
            Cleanup();
        }

        private static void Cleanup()
        {
            EditorApplication.update -= Update;
            Application.logMessageReceived -= CaptureError;
            _client?.Dispose(); _server?.Dispose();
            _server = _client = null;
            if (_sessionValues != null)
            {
                for (var i = 0; i < SessionFields.Length; i++) typeof(SteamNetcodeSession)
                    .GetField(SessionFields[i], BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, _sessionValues[i]);
                _sessionValues = null;
            }
        }
    }
}
