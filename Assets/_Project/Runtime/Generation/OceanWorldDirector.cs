using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using StylizedWater3;
using WaveByWave.Core;
using WaveByWave.Items;
using WaveByWave.Player;
using WaveByWave.Ships;

namespace WaveByWave.Generation
{
    // NGO carries infrequent island seed/dig events; loose items continue through NFE.
    // Geometry never crosses the wire. A late join replays the same authoritative edit log.
    public sealed class OceanWorldDirector : MonoBehaviour
    {
        private const string Channel = "WaveByWave.Ocean.v1";
        private enum Kind : byte { Request, Create, Dig, Remove, Marker, RemoveMarker, Reset, Complete }
        private struct Packet : INetworkSerializable
        {
            public Kind Kind;
            public FixedString64Bytes Scene;
            public int Id, Revision, ChestId;
            public uint Seed;
            public byte Size;
            public Vector3 Position;
            public float Radius;
            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref Kind); s.SerializeValue(ref Scene); s.SerializeValue(ref Id);
                s.SerializeValue(ref Revision); s.SerializeValue(ref ChestId); s.SerializeValue(ref Seed);
                s.SerializeValue(ref Size); s.SerializeValue(ref Position); s.SerializeValue(ref Radius);
            }
        }
        private sealed class IslandRecord
        {
            public Packet Creation;
            public ProceduralIsland Island;
            public readonly List<Packet> Digs = new();
            public readonly Dictionary<int, Packet> Markers = new();
            public readonly Dictionary<int, GameObject> MarkerViews = new();
            public bool Populate, Populated, Test, ScenePlaced;
        }
        public static OceanWorldDirector Instance { get; private set; }
        [SerializeField] private OceanGenerationSettings settings;
        public OceanGenerationSettings Settings => settings;
        public bool IsAuthority => _manager == null || !_manager.IsListening || _manager.IsServer;
        private NetworkManager _manager;
        private CustomMessagingManager _messages;
        private EquipmentWaterQuery _water;
        private string _scene;
        private bool _wasListening, _snapshotReady;
        private bool _initialGenerationStarted, _loadingComplete;
        private float _nextLoot, _nextIslands, _nextRequest;
        private int _nextIslandId = 1;
        private Unity.Mathematics.Random _random;
        private NetworkShipController[] _ships = Array.Empty<NetworkShipController>();
        private readonly Dictionary<int, IslandRecord> _islands = new();
        private readonly HashSet<int> _floating = new();
        private readonly List<int> _removeIds = new();
        private readonly Queue<(ulong Client, Packet Packet)> _outgoing = new();
        private readonly Dictionary<ulong, float> _snapshotRequests = new();
        private readonly HashSet<int> _initialIslands = new();
        private readonly RaycastHit[] _surfaceHits = new RaycastHit[64];
        private OceanLoadingCurtain _loadingCurtain;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap() => EnsureInstance();
        public static OceanWorldDirector EnsureInstance()
        {
            if (Instance != null) return Instance;
            var prefab = Resources.Load<GameObject>("OceanWorld");
            if (prefab != null) Instantiate(prefab);
            return Instance;
        }
        public static void BeginOceanLoading()
        {
            var director = EnsureInstance();
            if (director == null) return;
            director._loadingComplete = false;
            director._loadingCurtain?.Show(0, 0);
        }
        public static void CancelOceanLoading()
        {
            if (Instance == null) return;
            Instance._loadingComplete = SceneManager.GetActiveScene().name != GameScenes.Ocean;
            Instance._loadingCurtain?.Hide();
        }
        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this; DontDestroyOnLoad(gameObject);
            if (settings == null) settings = Resources.Load<OceanGenerationSettings>("OceanGeneration");
            if (settings == null || settings.IslandPrefab == null || settings.ChunkPrefab == null)
            { Debug.LogError("Ocean generation settings/prefabs are missing.", this); enabled = false; return; }
            _water = new EquipmentWaterQuery(settings.WaterProfile);
            _random = new Unity.Mathematics.Random(unchecked((uint)settings.WorldSeed) | 1u);
            if (settings.LoadingCurtainPrefab != null)
                _loadingCurtain = Instantiate(settings.LoadingCurtainPrefab, transform)
                    .GetComponent<OceanLoadingCurtain>();
            LootStressTest.ServerItemRemoved += OnItemRemoved;
        }

        private void Update()
        {
            if (_manager != NetworkManager.Singleton) Unbind();
            _manager = NetworkManager.Singleton;
            var listening = _manager != null && _manager.IsListening;
            if (_messages == null && listening)
            {
                _messages = _manager.CustomMessagingManager;
                _messages.RegisterNamedMessageHandler(Channel, OnMessage);
                _snapshotReady = false; _nextRequest = 0f;
                LootStressTest.RegisterCatalog(settings.Catalog, settings.RarityEffectPrefab, settings.WaterProfile);
            }
            var scene = SceneManager.GetActiveScene().name;
            if (_scene != scene || (_wasListening && !listening))
            {
                ClearWorld(IsAuthority); _scene = scene; _snapshotReady = false; _nextRequest = 0f;
                _initialGenerationStarted = false; _loadingComplete = scene != GameScenes.Ocean;
                _nextLoot = Time.unscaledTime + 1f; _nextIslands = Time.unscaledTime + 0.1f;
                if (scene == GameScenes.Ocean) _loadingCurtain?.Show(0, 0);
                else _loadingCurtain?.Hide();
            }
            if (!listening && _messages != null) Unbind();
            _wasListening = listening;
            if (listening && !_manager.IsServer && _manager.IsConnectedClient && !_snapshotReady &&
                Time.unscaledTime >= _nextRequest)
            {
                _nextRequest = Time.unscaledTime + 3f;
                Send(NetworkManager.ServerClientId, new Packet { Kind = Kind.Request, Scene = new FixedString64Bytes(scene) });
            }
            if (IsAuthority)
            {
                foreach (var record in _islands.Values)
                    if (record.Populate && record.Island != null && record.Island.Ready)
                    {
                        if (!PopulateChests(record)) continue;
                        record.Populate = false; record.Populated = true;
                    }
                if (listening && _manager.IsServer && scene == GameScenes.Ocean)
                {
                    if (Time.unscaledTime >= _nextIslands)
                    {
                        _nextIslands = Time.unscaledTime + Mathf.Max(0.1f, settings.IslandStreamingInterval);
                        _ships = FindObjectsByType<NetworkShipController>(FindObjectsSortMode.None);
                        if (!_initialGenerationStarted && _ships.Length > 0)
                            GenerateInitialIslands();
                        else if (_loadingComplete)
                            MaintainIslands();
                        PruneLoot();
                    }
                    if (_loadingComplete && settings.GenerateOceanLoot && _ships.Length > 0 &&
                        Time.unscaledTime >= _nextLoot)
                    {
                        _nextLoot = Time.unscaledTime + Mathf.Max(0.1f, settings.LootInterval);
                        SpawnFloatingLoot();
                    }
                }
            }
            if (scene == GameScenes.Ocean)
            {
                if (!IsAuthority && Time.unscaledTime >= _nextIslands)
                {
                    _nextIslands = Time.unscaledTime + Mathf.Max(0.1f, settings.IslandStreamingInterval);
                    _ships = FindObjectsByType<NetworkShipController>(FindObjectsSortMode.None);
                }
                UpdateLoadingAndPresentation();
            }
            // Bound snapshot catch-up traffic, including deep digging histories.
            for (var i = 0; i < 64 && _outgoing.Count > 0; i++)
            {
                var next = _outgoing.Dequeue();
                if (listening && _manager.ConnectedClients.ContainsKey(next.Client)) Send(next.Client, next.Packet);
            }
        }

        public int GenerateIsland(Vector3 position, IslandSize size, uint seed, bool snapToWater, bool test = false,
            bool populateChests = true)
        {
            if (!IsAuthority) return 0;
            if (snapToWater && _water.TryWaterLevel(position, out var level)) position.y = level;
            var packet = new Packet { Kind = Kind.Create, Id = _nextIslandId++, Size = (byte)size,
                Seed = seed == 0 ? 1u : seed, Position = position, Scene = CurrentScene() };
            Apply(packet);
            _islands[packet.Id].Test = test; _islands[packet.Id].Populate = populateChests;
            Broadcast(packet);
            return packet.Id;
        }
        public void RegisterSceneIsland(ProceduralIsland island, bool populateChests)
        {
            if (island == null || island.Id >= 0) return;
            if (_islands.TryGetValue(island.Id, out var existing))
            {
                if (existing.Island != island)
                {
                    Debug.LogError($"Scene islands share Network Island Id {island.Id}. Assign a unique negative id.", island);
                    return;
                }
                if (populateChests && !existing.Populated) existing.Populate = true;
                return;
            }
            _islands.Add(island.Id, new IslandRecord
            {
                Creation = new Packet { Kind = Kind.Create, Id = island.Id, Size = (byte)island.Size,
                    Seed = island.Seed, Position = island.transform.position, Scene = CurrentScene() },
                Island = island, ScenePlaced = true, Test = true, Populate = populateChests
            });
            island.SetPresentationVisible(true);
        }
        public void RemoveIsland(int id)
        {
            if (!IsAuthority || !_islands.TryGetValue(id, out var record)) return;
            if (record.ScenePlaced) return;
            var chests = new List<int>(record.Markers.Keys);
            foreach (var chest in chests) LootStressTest.RemoveServerItem(chest);
            var packet = new Packet { Kind = Kind.Remove, Id = id, Scene = CurrentScene() };
            _initialIslands.Remove(id);
            Apply(packet); Broadcast(packet);
        }
        public bool DigServer(ProceduralIsland island, Vector3 point, Vector3 normal)
        {
            if (!IsAuthority || island == null || !_islands.TryGetValue(island.Id, out var record)) return false;
            var center = point - normal.normalized * settings.DigPenetration;
            var density = island.DensityAt(center);
            if (density.y >= density.x || density.x < -settings.DigRadius) return false;
            var packet = new Packet { Kind = Kind.Dig, Id = island.Id, Revision = record.Digs.Count + 1,
                Position = island.transform.InverseTransformPoint(center), Radius = settings.DigRadius, Scene = CurrentScene() };
            record.Digs.Add(packet); Apply(packet); Broadcast(packet); return true;
        }
        public bool IsChestExposed(int islandId, Vector3 position)
        {
            if (!_islands.TryGetValue(islandId, out var record) || record.Island == null) return false;
            return math.cmax(record.Island.DensityAt(position + Vector3.up * 0.24f)) < 0.02f;
        }

        private bool PopulateChests(IslandRecord record)
        {
            LootStressTest.RegisterCatalog(settings.Catalog, settings.RarityEffectPrefab, settings.WaterProfile);
            if (Unity.NetCode.ClientServerBootstrap.ServerWorld is not { IsCreated: true }) return false;
            var island = record.Island;
            var random = new Unity.Mathematics.Random((island.Seed ^ 0x6E624EB7u) | 1u);
            var range = island.Size == IslandSize.Small ? settings.ChestCountSmall :
                island.Size == IslandSize.Medium ? settings.ChestCountMedium : settings.ChestCountLarge;
            var count = random.NextInt(Mathf.Clamp(range.x, 0, 16), Mathf.Clamp(range.y, Mathf.Clamp(range.x, 0, 16), 16) + 1);
            for (var n = 0; n < count; n++)
            for (var attempt = 0; attempt < 24; attempt++)
            {
                var p = random.NextFloat2(-island.Diameter * 0.32f, island.Diameter * 0.32f);
                if (!island.TrySurface(p.x, p.y, out var surface, out var normal) ||
                    surface.y < island.transform.position.y + 0.15f || normal.y < 0.7f) continue;
                var definition = ChestLootTable.Choose(settings.BuriedChests, ref random);
                if (definition == null || !definition.IsChest) break;
                var minimumDepth = Mathf.Max(0.4f, settings.BurialDepth.x);
                var depth = random.NextFloat(minimumDepth, Mathf.Max(minimumDepth + 0.01f, settings.BurialDepth.y));
                depth = Mathf.Min(depth, island.BedrockDepth * 0.7f);
                var position = surface - Vector3.up * depth;
                if (island.DensityAt(position - Vector3.up * 0.3f).y > -0.05f) continue;
                var tooClose = false;
                foreach (var existing in record.Markers.Values)
                    if ((existing.Position - surface).sqrMagnitude < 1.2f) { tooClose = true; break; }
                if (tooClose) continue;
                var id = LootStressTest.SpawnPlacedServer(definition, position,
                    Quaternion.Euler(0f, random.NextFloat(0f, 360f), 0f) * definition.RestingRotation, false, island.Id);
                if (id == 0) break;
                var packet = new Packet { Kind = Kind.Marker, Id = island.Id, ChestId = id,
                    Position = surface + Vector3.up * 0.04f, Scene = CurrentScene() };
                Apply(packet); Broadcast(packet); break;
            }
            return true;
        }

        private void OnItemRemoved(int id)
        {
            _floating.Remove(id);
            foreach (var pair in _islands)
            {
                if (!pair.Value.Markers.ContainsKey(id)) continue;
                var packet = new Packet { Kind = Kind.RemoveMarker, Id = pair.Key, ChestId = id, Scene = CurrentScene() };
                Apply(packet); Broadcast(packet); break;
            }
        }
        private void SpawnFloatingLoot()
        {
            var target = Mathf.Clamp(settings.FloatingLootPerShip, 1, 3000) * _ships.Length;
            for (var n = 0; n < Mathf.Clamp(settings.LootPerInterval, 1, 16) && _floating.Count < target; n++)
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var ship = _ships[_random.NextInt(_ships.Length)];
                if (ship == null || !ship.IsSpawned) continue;
                var position = InRing(ship.transform.position, settings.LootRadius);
                if (!IsOpenWater(position, 0.8f, out var level)) continue;
                position.y = level;
                if (_water.TryHeight(position, out var wave)) position.y = wave;
                var definition = ChestLootTable.Choose(settings.FloatingLoot, ref _random);
                if (definition == null) return;
                var id = LootStressTest.SpawnPlacedServer(definition, position,
                    Quaternion.Euler(0f, _random.NextFloat(0f, 360f), 0f) * definition.RestingRotation,
                    true, 0, settings.LootFadeDuration);
                if (id != 0) _floating.Add(id);
                break;
            }
        }
        private void PruneLoot()
        {
            _removeIds.Clear();
            foreach (var id in _floating)
            {
                if (!LootStressTest.HasServerItem(id)) { _removeIds.Add(id); continue; }
                // Once a hook claims procedural loot, it becomes persistent gameplay loot.
                if (!LootStressTest.TryGetServerItem(id, out _, out var position)) { _removeIds.Add(id); continue; }
                if (!NearAnyPlayerOrShip(position, Mathf.Max(settings.LootRadius.y + 10f, settings.LootDespawnRadius)))
                    _removeIds.Add(id);
            }
            foreach (var id in _removeIds)
            {
                _floating.Remove(id);
                if (LootStressTest.TryGetServerItem(id, out _, out _)) LootStressTest.RemoveServerItem(id);
            }
        }
        private void GenerateInitialIslands()
        {
            _initialGenerationStarted = true;
            if (!settings.GenerateIslands || _ships.Length == 0)
                return;

            var target = Mathf.Clamp(settings.IslandsPerShip, 1, 16) * _ships.Length;
            for (var n = CountStreamingIslands(); n < target; n++)
            {
                if (!TryGenerateIsland(settings.InitialIslandRadius, out var id))
                    continue;
                _initialIslands.Add(id);
            }
        }

        private void MaintainIslands()
        {
            if (!settings.GenerateIslands || _ships.Length == 0) return;
            _removeIds.Clear();
            var despawnRadius = Mathf.Max(settings.IslandDespawnRadius, settings.StreamingIslandRadius.y + 20f);
            foreach (var pair in _islands)
                if (!pair.Value.Test && pair.Value.Island != null &&
                    !NearAnyPlayerOrShip(pair.Value.Island.transform.position, despawnRadius)) _removeIds.Add(pair.Key);
            foreach (var id in _removeIds) RemoveIsland(id);
            if (CountStreamingIslands() >= Mathf.Clamp(settings.IslandsPerShip, 1, 16) * _ships.Length) return;
            TryGenerateIsland(settings.StreamingIslandRadius, out _);
        }

        private int CountStreamingIslands()
        {
            var count = 0;
            foreach (var record in _islands.Values)
                if (!record.Test && !record.ScenePlaced)
                    count++;
            return count;
        }

        private bool TryGenerateIsland(Vector2 generationRadius, out int id)
        {
            id = 0;
            var ship = _ships[_random.NextInt(_ships.Length)];
            if (ship == null || !ship.IsSpawned) return false;
            for (var attempt = 0; attempt < 24; attempt++)
            {
                var size = (IslandSize)_random.NextInt(3);
                var position = InRing(ship.transform.position, generationRadius);
                var radius = settings.Diameter(size) * 0.5f;
                if (!IsOpenWater(position, radius + settings.IslandSpacing * 0.5f, out var level)) continue;
                var valid = true;
                for (var i = 0; i < 8; i++)
                {
                    var angle = i * Mathf.PI * 0.25f;
                    if (!IsOpenWater(position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius, 1f, out _))
                    { valid = false; break; }
                }
                if (!valid) continue;
                position.y = level;
                id = GenerateIsland(position, size, _random.NextUInt() | 1u, false);
                return id != 0;
            }
            return false;
        }

        private void UpdateLoadingAndPresentation()
        {
            var ready = 0;
            var total = 0;
            if (IsAuthority)
            {
                foreach (var id in _initialIslands)
                {
                    if (!_islands.TryGetValue(id, out var record) || record.Island == null)
                        continue;
                    total++;
                    if (record.Island.Ready && !record.Populate)
                        ready++;
                }
                if (!_loadingComplete && _initialGenerationStarted && ready >= total)
                {
                    _loadingComplete = true;
                    _loadingCurtain?.Hide();
                }
            }
            else
            {
                foreach (var record in _islands.Values)
                {
                    if (record.ScenePlaced || record.Island == null) continue;
                    total++;
                    if (record.Island.Ready) ready++;
                }
                if (!_loadingComplete && _snapshotReady && ready >= total)
                {
                    _loadingComplete = true;
                    _loadingCurtain?.Hide();
                }
            }

            if (!_loadingComplete)
                _loadingCurtain?.Show(ready, total);

            foreach (var record in _islands.Values)
            {
                if (record.Island == null) continue;
                var visible = record.ScenePlaced || _loadingComplete && record.Island.Ready &&
                    NearAnyPlayerOrShip(record.Island.transform.position, settings.IslandRevealRadius);
                record.Island.SetPresentationVisible(visible);
                foreach (var marker in record.MarkerViews.Values)
                    if (marker != null) marker.SetActive(visible);
            }
        }
        private bool NearAnyPlayerOrShip(Vector3 point, float radius)
        {
            var squared = radius * radius;
            foreach (var ship in _ships) if (ship != null && (ship.transform.position - point).sqrMagnitude < squared) return true;
            if (_manager != null && _manager.IsServer)
                foreach (var client in _manager.ConnectedClientsList)
                    if (client.PlayerObject != null && (client.PlayerObject.transform.position - point).sqrMagnitude < squared) return true;
            if (_manager != null && _manager.IsClient && _manager.LocalClient?.PlayerObject != null &&
                (_manager.LocalClient.PlayerObject.transform.position - point).sqrMagnitude < squared) return true;
            return false;
        }
        private bool IsOpenWater(Vector3 position, float margin, out float level)
        {
            if (!_water.TryWaterLevel(position, out level)) return false;
            foreach (var record in _islands.Values)
                if (record.Island != null && record.Island.ContainsHorizontal(position, margin)) return false;
            foreach (var ship in _ships)
                if (ship != null && new Vector2(ship.transform.position.x - position.x,
                        ship.transform.position.z - position.z).sqrMagnitude < Mathf.Pow(10f + margin, 2f)) return false;
            var count = UnityEngine.Physics.RaycastNonAlloc(new Vector3(position.x, level + 64f, position.z),
                Vector3.down, _surfaceHits, 65f, ~0, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
                if (_surfaceHits[i].collider.GetComponentInParent<WaterObject>() == null) return false;
            return true;
        }
        private Vector3 InRing(Vector3 center, Vector2 radius)
        {
            var min = Mathf.Max(1f, radius.x); var max = Mathf.Max(min + 1f, radius.y);
            var distance = Mathf.Sqrt(_random.NextFloat(min * min, max * max));
            var angle = _random.NextFloat(0f, Mathf.PI * 2f);
            return center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;
        }
        private FixedString64Bytes CurrentScene() => new(SceneManager.GetActiveScene().name);

        private void OnMessage(ulong sender, FastBufferReader reader)
        {
            reader.ReadValueSafe(out Packet packet);
            if (packet.Scene.ToString() != SceneManager.GetActiveScene().name) return;
            if (_manager.IsServer)
            {
                if (packet.Kind != Kind.Request || !_manager.ConnectedClients.ContainsKey(sender)) return;
                if (_snapshotRequests.TryGetValue(sender, out var previous) && Time.unscaledTime - previous < 2f) return;
                _snapshotRequests[sender] = Time.unscaledTime;
                Queue(sender, new Packet { Kind = Kind.Reset, Scene = CurrentScene() });
                foreach (var record in _islands.Values)
                {
                    if (!record.ScenePlaced) Queue(sender, record.Creation);
                    foreach (var dig in record.Digs) Queue(sender, dig);
                    foreach (var marker in record.Markers.Values) Queue(sender, marker);
                }
                Queue(sender, new Packet { Kind = Kind.Complete, Scene = CurrentScene() });
                return;
            }
            if (sender == NetworkManager.ServerClientId) Apply(packet);
        }
        private void Apply(Packet packet)
        {
            if (packet.Kind == Kind.Reset)
            {
                ClearForSnapshot();
                // Reliable ordered delivery is now catching up. Do not restart a long
                // snapshot every three seconds while its dig history is still arriving.
                _nextRequest = float.PositiveInfinity;
                return;
            }
            if (packet.Kind == Kind.Complete) { _snapshotReady = true; return; }
            if (packet.Kind == Kind.Create)
            {
                if (_islands.ContainsKey(packet.Id)) return;
                var instance = Instantiate(settings.IslandPrefab, packet.Position, Quaternion.identity);
                instance.name = $"Island {packet.Id} ({(IslandSize)packet.Size})";
                var island = instance.GetComponent<ProceduralIsland>();
                island.Initialize(packet.Id, (IslandSize)packet.Size, packet.Seed, settings);
                island.SetPresentationVisible(false);
                _islands.Add(packet.Id, new IslandRecord { Creation = packet, Island = island }); return;
            }
            if (!_islands.TryGetValue(packet.Id, out var record)) return;
            if (packet.Kind == Kind.Dig)
            { record.Island.ApplyDig(packet.Revision, packet.Position, packet.Radius); return; }
            if (packet.Kind == Kind.Remove)
            {
                if (record.Island != null) Destroy(record.Island.gameObject);
                _initialIslands.Remove(packet.Id);
                _islands.Remove(packet.Id);
                return;
            }
            if (packet.Kind == Kind.Marker)
            {
                if (record.Markers.ContainsKey(packet.ChestId)) return;
                record.Markers[packet.ChestId] = packet;
                if (settings.BuriedMarkerPrefab != null)
                {
                    var marker = Instantiate(settings.BuriedMarkerPrefab,
                        packet.Position, Quaternion.Euler(90f, 0f, 0f), record.Island.transform);
                    marker.SetActive(record.ScenePlaced);
                    record.MarkerViews[packet.ChestId] = marker;
                }
            }
            if (packet.Kind == Kind.RemoveMarker)
            {
                record.Markers.Remove(packet.ChestId);
                if (record.MarkerViews.Remove(packet.ChestId, out var marker) && marker != null) Destroy(marker);
            }
        }
        private void Queue(ulong client, Packet packet) => _outgoing.Enqueue((client, packet));
        private void Broadcast(Packet packet)
        {
            if (_messages == null || _manager == null || !_manager.IsServer) return;
            foreach (var id in _manager.ConnectedClientsIds)
                if (id != _manager.LocalClientId) Queue(id, packet);
        }
        private void Send(ulong target, Packet packet)
        {
            if (_messages == null) return;
            using var writer = new FastBufferWriter(256, Allocator.Temp);
            writer.WriteValueSafe(packet);
            _messages.SendNamedMessage(Channel, target, writer, NetworkDelivery.ReliableSequenced);
        }
        private void ClearWorld(bool removeLoot)
        {
            if (removeLoot)
            {
                var ids = new List<int>(_floating);
                foreach (var record in _islands.Values) ids.AddRange(record.Markers.Keys);
                foreach (var id in ids) LootStressTest.RemoveServerItem(id);
            }
            foreach (var record in _islands.Values)
                if (!record.ScenePlaced && record.Island != null) Destroy(record.Island.gameObject);
            _islands.Clear(); _floating.Clear(); _outgoing.Clear(); _snapshotRequests.Clear();
            _initialIslands.Clear();
        }
        private void ClearForSnapshot()
        {
            _removeIds.Clear();
            foreach (var pair in _islands)
            {
                var record = pair.Value;
                if (!record.ScenePlaced)
                {
                    if (record.Island != null) Destroy(record.Island.gameObject);
                    _removeIds.Add(pair.Key);
                    continue;
                }
                record.Digs.Clear(); record.Markers.Clear();
                foreach (var marker in record.MarkerViews.Values)
                    if (marker != null) Destroy(marker);
                record.MarkerViews.Clear();
            }
            foreach (var id in _removeIds) _islands.Remove(id);
            _floating.Clear(); _outgoing.Clear(); _snapshotRequests.Clear();
            _initialIslands.Clear();
            _loadingComplete = false;
            _loadingCurtain?.Show(0, 0);
        }
        private void Unbind()
        {
            _messages?.UnregisterNamedMessageHandler(Channel); _messages = null;
        }
        private void OnDestroy()
        {
            if (Instance != this) return;
            Unbind(); ClearWorld(false); _water?.Dispose();
            LootStressTest.ServerItemRemoved -= OnItemRemoved; Instance = null;
        }
    }
}
