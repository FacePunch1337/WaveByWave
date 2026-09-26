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
        private enum Kind : byte { Request, Create, Dig, Remove, Marker, RemoveMarker, Reset, Complete, HideMarker }
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
            public readonly HashSet<int> HiddenMarkers = new();
            public bool Populate, Populated, Test, ScenePlaced, WasPresented;
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
        private int _initialTargetCount;
        private float _nextLoot, _nextIslands, _nextRequest, _nextPresentation;
        private int _nextIslandId = 1;
        private Unity.Mathematics.Random _random;
        private NetworkShipController[] _ships = Array.Empty<NetworkShipController>();
        private readonly Dictionary<int, IslandRecord> _islands = new();
        private readonly HashSet<int> _floating = new();
        private readonly List<int> _removeIds = new();
        private readonly Queue<(ulong Client, Packet Packet)> _outgoing = new();
        private readonly Dictionary<ulong, float> _snapshotRequests = new();
        private readonly HashSet<ulong> _pendingSnapshotClients = new();
        private readonly HashSet<int> _initialIslands = new();
        private readonly HashSet<int> _streamedIslandIds = new();
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
            Instance._loadingCurtain?.HideImmediate();
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
            var loadingView = WaveByWave.UI.GameUiPrefabs.Create("Loading/OceanLoadingCurtain", owner: this);
            if (loadingView == null && settings.LoadingCurtainPrefab != null)
                loadingView = Instantiate(settings.LoadingCurtainPrefab, transform);
            if (loadingView != null) _loadingCurtain = loadingView.GetComponent<OceanLoadingCurtain>();
            SceneManager.sceneLoaded += OnSceneLoaded;
            if (SceneManager.GetActiveScene().name == GameScenes.Port)
                _loadingCurtain?.HideImmediate();
            LootStressTest.ServerItemRemoved += OnItemRemoved;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // The ocean curtain belongs exclusively to the voyage transition and Ocean.
            // A joining client can receive scene/snapshot callbacks in a different order,
            // so loading Port must always win and clear it immediately.
            if (scene.name != GameScenes.Port) return;
            _loadingComplete = true;
            _loadingCurtain?.HideImmediate();
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
                _initialTargetCount = scene == GameScenes.Ocean && settings.GenerateIslands
                    ? Mathf.Clamp(settings.InitialIslandCount, 0, 32) : 0;
                _nextLoot = Time.unscaledTime + 1f; _nextIslands = Time.unscaledTime + 0.1f;
                _nextPresentation = 0f;
                if (scene == GameScenes.Ocean) _loadingCurtain?.Show(0, 0);
                else _loadingCurtain?.HideImmediate();
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
                    if (!NightWaveController.BattleInProgress && record.Populate && record.Island != null && record.Island.Ready)
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
                        if (!_loadingComplete)
                            GenerateInitialIslands();
                        else
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
                // Initial generation updates the curtain continuously. Once ready, island
                // visibility and marker state do not need a 60 Hz managed traversal.
                if (!_loadingComplete || Time.unscaledTime >= _nextPresentation)
                {
                    _nextPresentation = Time.unscaledTime +
                        Mathf.Clamp(settings.PresentationRefreshInterval, 0.05f, 1f);
                    UpdateLoadingAndPresentation();
                }
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
            record.Digs.Add(packet); Apply(packet); Broadcast(packet);
            HideMarkerAboveDig(record, point);
            return true;
        }

        private void HideMarkerAboveDig(IslandRecord record, Vector3 point)
        {
            // A marker identifies the patch of sand above a chest, rather than a physical
            // painted stroke. Remove it on the first successful excavation below that patch,
            // including sloped island surfaces and deeper follow-up hits.
            var radius = settings.DigRadius + 0.1f;
            foreach (var pair in record.Markers)
            {
                if (record.HiddenMarkers.Contains(pair.Key)) continue;
                var markerPosition = pair.Value.Position;
                var horizontal = new Vector2(point.x - markerPosition.x, point.z - markerPosition.z);
                var depthBelowMarker = markerPosition.y - point.y;
                if (horizontal.sqrMagnitude > radius * radius || depthBelowMarker < -0.3f)
                    continue;
                var hidden = new Packet { Kind = Kind.HideMarker, Id = record.Island.Id,
                    ChestId = pair.Key, Scene = CurrentScene() };
                Apply(hidden); Broadcast(hidden);
                return;
            }
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
            var loot = settings.BuriedChests;
            if (loot == null || loot.Count == 0) return true;
            var rarityMask = OceanGenerationSettings.UnlockedRarities(settings.BuriedChestRarityUnlocks,
                NightWaveController.Active?.CurrentDay ?? 1);
            if (rarityMask == 0) return true;
            var random = new Unity.Mathematics.Random((island.Seed ^ 0x6E624EB7u) | 1u);
            var range = island.Size == IslandSize.Small ? settings.ChestCountSmall :
                island.Size == IslandSize.Medium ? settings.ChestCountMedium : settings.ChestCountLarge;
            var count = random.NextInt(Mathf.Clamp(range.x, 0, 16), Mathf.Clamp(range.y, Mathf.Clamp(range.x, 0, 16), 16) + 1);
            WeightedLootEntry batch = null;
            var batchRemaining = 0;
            for (var n = 0; n < count; n++)
            {
                if (batchRemaining <= 0)
                {
                    batch = ChestLootTable.ChooseEntry(loot, ref random, rarityMask: rarityMask, chestsOnly: true);
                    if (batch == null) break;
                    var minimum = Mathf.Clamp(batch.MinimumAmount, 1, 16);
                    batchRemaining = random.NextInt(minimum, Mathf.Clamp(batch.MaximumAmount, minimum, 16) + 1);
                }
                var definition = batch.Item;
                for (var attempt = 0; attempt < 24; attempt++)
                {
                    var p = random.NextFloat2(-island.Diameter * 0.32f, island.Diameter * 0.32f);
                    if (!island.TrySurface(p.x, p.y, out var surface, out var normal) ||
                        surface.y < island.transform.position.y + 0.15f || normal.y < 0.7f) continue;
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
                    if (id == 0) { batchRemaining = 0; break; }
                    batchRemaining--;
                    var packet = new Packet { Kind = Kind.Marker, Id = island.Id, ChestId = id,
                        Position = surface + Vector3.up * 0.04f, Scene = CurrentScene() };
                    Apply(packet); Broadcast(packet); break;
                }
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
            if (NightWaveController.BattleInProgress) return;
            var loot = settings.FloatingObjects;
            var rarityMask = OceanGenerationSettings.UnlockedRarities(settings.FloatingRarityUnlocks,
                NightWaveController.Active?.CurrentDay ?? 1);
            if (loot == null || loot.Count == 0 || rarityMask == 0) return;
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
                var definition = ChestLootTable.Choose(loot, ref _random, rarityMask: rarityMask);
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
                if (!LootStressTest.TryGetStreamingPosition(id, out var position)) { _removeIds.Add(id); continue; }
                if (!NearAnyPlayerOrShip(position, Mathf.Max(settings.LootRadius.y + 10f, settings.LootDespawnRadius)))
                    _removeIds.Add(id);
            }
            foreach (var id in _removeIds)
            {
                _floating.Remove(id);
                if (LootStressTest.TryGetStreamingPosition(id, out _)) LootStressTest.RemoveServerItem(id);
            }
        }
        private void GenerateInitialIslands()
        {
            _initialTargetCount = settings.GenerateIslands
                ? Mathf.Clamp(settings.InitialIslandCount, 0, 32) : 0;
            if (_initialTargetCount == 0)
            {
                _initialGenerationStarted = true;
                return;
            }
            if (_ships.Length == 0)
                return;

            _initialGenerationStarted = true;
            var missing = _initialTargetCount - _initialIslands.Count;
            for (var n = 0; n < missing; n++)
            {
                var ship = NextSpawnedShip(_initialIslands.Count);
                if (ship == null) break;
                var course = ShipCourse(ship);
                var anchor = ship.transform.position;
                var distance = settings.InitialIslandRadius;
                if (TryFindFurthestInitialIsland(ship.transform.position, course, out var previous))
                {
                    anchor = previous.transform.position;
                    distance = settings.IslandChainDistance;
                }
                if (!TryGenerateIslandAhead(ship, anchor, distance, course, out var id))
                    continue;
                _initialIslands.Add(id);
            }
        }

        private NetworkShipController NextSpawnedShip(int offset)
        {
            if (_ships.Length == 0) return null;
            for (var i = 0; i < _ships.Length; i++)
            {
                var ship = _ships[(offset + i) % _ships.Length];
                if (ship != null && ship.IsSpawned) return ship;
            }
            return null;
        }

        private bool TryFindFurthestInitialIsland(Vector3 shipPosition, Vector3 course,
            out ProceduralIsland island)
        {
            island = null;
            var furthest = 0f;
            OrderedRange(settings.IslandChainDistance, out _, out var chainMaximum);
            var maximumDistance = Mathf.Max(settings.IslandRecoveryRadius, chainMaximum * 2f);
            var minimumDot = Mathf.Cos((Mathf.Clamp(settings.IslandForwardArc, 0f, 80f) + 10f) *
                                       Mathf.Deg2Rad);
            foreach (var id in _initialIslands)
            {
                if (!_islands.TryGetValue(id, out var record) || record.Island == null) continue;
                var offset = Vector3.ProjectOnPlane(record.Island.transform.position - shipPosition, Vector3.up);
                var distance = offset.magnitude;
                if (distance < 0.01f || distance > maximumDistance ||
                    Vector3.Dot(offset / distance, course) < minimumDot) continue;
                var projection = Vector3.Dot(offset, course);
                if (projection <= furthest) continue;
                furthest = projection;
                island = record.Island;
            }
            return island != null;
        }

        private void MaintainIslands()
        {
            if (!settings.GenerateIslands || _ships.Length == 0 || NightWaveController.BattleInProgress) return;
            _streamedIslandIds.Clear();
            var generatedThisTick = false;
            foreach (var ship in _ships)
            {
                if (ship == null || !ship.IsSpawned) continue;
                var course = ShipCourse(ship);
                var currentId = FindClosestStreamingIsland(ship.transform.position);
                if (currentId == 0 && !generatedThisTick &&
                    TryGenerateIslandAhead(ship, ship.transform.position, settings.InitialIslandRadius,
                        course, out currentId))
                    generatedThisTick = true;
                if (currentId == 0) continue;
                _streamedIslandIds.Add(currentId);

                var nextId = FindNextStreamingIsland(ship, currentId, course);
                if (nextId == 0 && !generatedThisTick &&
                    _islands.TryGetValue(currentId, out var current) && current.Island != null &&
                    TryGenerateIslandAhead(ship, current.Island.transform.position,
                        settings.IslandChainDistance, course, out nextId))
                    generatedThisTick = true;
                if (nextId != 0) _streamedIslandIds.Add(nextId);
            }

            _removeIds.Clear();
            foreach (var pair in _islands)
            {
                var record = pair.Value;
                if (record.Test || record.ScenePlaced || record.Island == null ||
                    _streamedIslandIds.Contains(pair.Key) || HasPlayerNear(record.Island) ||
                    NearAnyPlayerOrShip(record.Island.transform.position,
                        Mathf.Max(settings.IslandRemovalRadius, settings.IslandHideRadius + 50f,
                            settings.IslandRevealRadius + 100f))) continue;
                _removeIds.Add(pair.Key);
            }
            foreach (var id in _removeIds) RemoveIsland(id);
        }

        private int FindClosestStreamingIsland(Vector3 position)
        {
            var result = 0;
            OrderedRange(settings.IslandChainDistance, out _, out var chainMaximum);
            var maximumDistance = Mathf.Max(settings.IslandRecoveryRadius, chainMaximum * 1.25f);
            var best = maximumDistance * maximumDistance;
            foreach (var pair in _islands)
            {
                var record = pair.Value;
                if (record.Test || record.ScenePlaced || record.Island == null) continue;
                var offset = Vector3.ProjectOnPlane(record.Island.transform.position - position, Vector3.up);
                var squared = offset.sqrMagnitude;
                if (squared >= best) continue;
                best = squared;
                result = pair.Key;
            }
            return result;
        }

        private int FindNextStreamingIsland(NetworkShipController ship, int currentId, Vector3 course)
        {
            if (!_islands.TryGetValue(currentId, out var current) || current.Island == null) return 0;
            OrderedRange(settings.IslandChainDistance, out var minimum, out var maximum);
            // A little hysteresis keeps an already generated island from being discarded
            // when the helm changes the course by only a few degrees.
            var acceptanceArc = Mathf.Min(89f, Mathf.Clamp(settings.IslandForwardArc, 0f, 80f) + 12f);
            var minimumDot = Mathf.Cos(acceptanceArc * Mathf.Deg2Rad);
            var anchor = current.Island.transform.position;
            var result = 0;
            var best = float.PositiveInfinity;
            foreach (var pair in _islands)
            {
                if (pair.Key == currentId) continue;
                var record = pair.Value;
                if (record.Test || record.ScenePlaced || record.Island == null) continue;
                var separation = Vector3.ProjectOnPlane(record.Island.transform.position - anchor, Vector3.up).magnitude;
                if (separation < minimum - 0.5f || separation > maximum + 0.5f) continue;
                var fromShip = Vector3.ProjectOnPlane(record.Island.transform.position - ship.transform.position,
                    Vector3.up);
                var distance = fromShip.magnitude;
                if (distance < 0.01f || Vector3.Dot(fromShip / distance, course) + 0.0001f < minimumDot)
                    continue;
                if (distance >= best) continue;
                best = distance;
                result = pair.Key;
            }
            return result;
        }

        private bool TryGenerateIslandAhead(NetworkShipController ship, Vector3 anchor,
            Vector2 distanceRange, Vector3 course, out int id)
        {
            id = 0;
            if (NightWaveController.BattleInProgress ||
                _islands.Count >= Mathf.Max(settings.InitialIslandCount, settings.MaximumResidentIslands)) return false;
            OrderedRange(distanceRange, out var minimum, out var maximum);
            var arc = Mathf.Clamp(settings.IslandForwardArc, 0f, 80f);
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var size = (IslandSize)_random.NextInt(3);
                var distance = maximum - minimum <= 0.001f
                    ? minimum : _random.NextFloat(minimum, maximum);
                var direction = Quaternion.AngleAxis(_random.NextFloat(-arc, arc), Vector3.up) * course;
                var position = anchor + direction * distance;
                if (Vector3.Dot(Vector3.ProjectOnPlane(position - ship.transform.position, Vector3.up),
                        course) <= 1f) continue;
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

        private static void OrderedRange(Vector2 range, out float minimum, out float maximum)
        {
            minimum = Mathf.Max(1f, Mathf.Min(range.x, range.y));
            maximum = Mathf.Max(minimum, Mathf.Max(range.x, range.y));
        }

        private static Vector3 ShipCourse(NetworkShipController ship)
        {
            var velocity = Vector3.ProjectOnPlane(ship.GetPlanarPointVelocity(ship.transform.position), Vector3.up);
            if (velocity.sqrMagnitude > 0.04f) return velocity.normalized;
            var forward = Vector3.ProjectOnPlane(ship.transform.forward, Vector3.up);
            return forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
        }

        private bool HasPlayerNear(ProceduralIsland island)
        {
            if (_manager == null || !_manager.IsServer || island == null) return false;
            var radius = island.Diameter * 0.5f + 12f;
            var squared = radius * radius;
            foreach (var client in _manager.ConnectedClientsList)
                if (client.PlayerObject != null &&
                    (client.PlayerObject.transform.position - island.transform.position).sqrMagnitude <= squared)
                    return true;
            return false;
        }

        private void UpdateLoadingAndPresentation()
        {
            var ready = 0;
            var total = 0;
            if (IsAuthority)
            {
                total = _initialTargetCount;
                foreach (var id in _initialIslands)
                {
                    if (!_islands.TryGetValue(id, out var record) || record.Island == null)
                        continue;
                    if (record.Island.Ready && !record.Populate)
                        ready++;
                }
                if (!_loadingComplete && _initialGenerationStarted &&
                    _initialIslands.Count >= _initialTargetCount && ready >= total)
                {
                    _loadingComplete = true;
                    _loadingCurtain?.HideImmediate();
                    FlushPendingSnapshotRequests();
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
                    _loadingCurtain?.HideImmediate();
                }
            }

            if (!_loadingComplete)
                _loadingCurtain?.Show(ready, total);

            foreach (var record in _islands.Values)
            {
                if (record.Island == null) continue;
                // Ready becomes false briefly while a dig remesh is in flight. Once an
                // island has completed its initial build it must remain presented so its
                // existing render mesh and collider stay continuous during that rebuild.
                var visible = record.ScenePlaced || _loadingComplete && record.Island.InitialBuildComplete &&
                    NearAnyPlayerOrShip(record.Island.transform.position, record.WasPresented
                        ? Mathf.Max(settings.IslandHideRadius, settings.IslandRevealRadius + 30f)
                        : settings.IslandRevealRadius);
                record.WasPresented = visible;
                record.Island.SetPresentationVisible(visible);
                if (visible && _manager != null && _manager.IsServer &&
                    (!record.ScenePlaced || record.Island.TryGetComponent<SceneIsland>(out var sceneIsland) && sceneIsland.SpawnEnemyPoints))
                    record.Island.EnsureEnemySpawnPoints();
                foreach (var marker in record.MarkerViews.Values)
                    if (marker != null) marker.SetActive(visible);
            }
        }
        internal bool TryFindIslandGround(Vector3 position, out RaycastHit ground)
        {
            ground = default;
            var found = false;
            foreach (var record in _islands.Values)
            {
                var island = record.Island;
                if (island == null || !island.ShipCollisionActive || !island.ContainsHorizontal(position, 2f)) continue;
                var local = island.transform.InverseTransformPoint(position);
                // Start at the island's own top, even if the thrown item's destination is inside
                // a rising slope. The old short ray then started inside its collider and missed it.
                if (!island.TryMeshSurface(local.x, local.z, out var hit) ||
                    hit.normal.y < 0.3f || found && hit.point.y <= ground.point.y) continue;
                ground = hit;
                found = true;
            }
            return found;
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
            var min = Mathf.Max(1f, Mathf.Min(radius.x, radius.y));
            var max = Mathf.Max(min, Mathf.Max(radius.x, radius.y));
            var distance = max - min <= 0.001f
                ? min
                : Mathf.Sqrt(_random.NextFloat(min * min, max * max));
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
                if (SceneManager.GetActiveScene().name == GameScenes.Ocean && !_loadingComplete)
                {
                    _pendingSnapshotClients.Add(sender);
                    return;
                }
                if (_snapshotRequests.TryGetValue(sender, out var previous) && Time.unscaledTime - previous < 2f) return;
                _snapshotRequests[sender] = Time.unscaledTime;
                QueueSnapshot(sender);
                return;
            }
            if (sender == NetworkManager.ServerClientId) Apply(packet);
        }
        private void QueueSnapshot(ulong client)
        {
            Queue(client, new Packet { Kind = Kind.Reset, Scene = CurrentScene() });
            foreach (var record in _islands.Values)
            {
                if (!record.ScenePlaced) Queue(client, record.Creation);
                foreach (var dig in record.Digs) Queue(client, dig);
                foreach (var marker in record.Markers.Values)
                    if (!record.HiddenMarkers.Contains(marker.ChestId)) Queue(client, marker);
            }
            Queue(client, new Packet { Kind = Kind.Complete, Scene = CurrentScene() });
        }
        private void FlushPendingSnapshotRequests()
        {
            if (_manager == null || !_manager.IsServer || _pendingSnapshotClients.Count == 0)
                return;
            foreach (var client in _pendingSnapshotClients)
            {
                if (!_manager.ConnectedClients.ContainsKey(client)) continue;
                _snapshotRequests[client] = Time.unscaledTime;
                QueueSnapshot(client);
            }
            _pendingSnapshotClients.Clear();
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
            if (packet.Kind == Kind.Complete)
            {
                _snapshotReady = true;
                if (SceneManager.GetActiveScene().name != GameScenes.Ocean)
                {
                    _loadingComplete = true;
                    _loadingCurtain?.HideImmediate();
                }
                return;
            }
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
                if (record.HiddenMarkers.Contains(packet.ChestId)) return;
                if (settings.BuriedMarkerPrefab != null)
                {
                    var marker = Instantiate(settings.BuriedMarkerPrefab,
                        packet.Position, record.Island.transform.rotation, record.Island.transform);
                    marker.GetComponent<BuriedChestMarker>()?.Configure(record.Island);
                    marker.SetActive(record.ScenePlaced);
                    record.MarkerViews[packet.ChestId] = marker;
                }
            }
            if (packet.Kind == Kind.RemoveMarker)
            {
                record.Markers.Remove(packet.ChestId);
                record.HiddenMarkers.Remove(packet.ChestId);
                if (record.MarkerViews.Remove(packet.ChestId, out var marker) && marker != null) Destroy(marker);
            }
            if (packet.Kind == Kind.HideMarker)
            {
                if (!record.Markers.ContainsKey(packet.ChestId)) return;
                record.HiddenMarkers.Add(packet.ChestId);
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
            _pendingSnapshotClients.Clear();
            _initialIslands.Clear(); _streamedIslandIds.Clear();
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
                record.Digs.Clear(); record.Markers.Clear(); record.HiddenMarkers.Clear();
                foreach (var marker in record.MarkerViews.Values)
                    if (marker != null) Destroy(marker);
                record.MarkerViews.Clear();
            }
            foreach (var id in _removeIds) _islands.Remove(id);
            _floating.Clear(); _outgoing.Clear(); _snapshotRequests.Clear();
            _pendingSnapshotClients.Clear();
            _initialIslands.Clear(); _streamedIslandIds.Clear();
            var isOcean = SceneManager.GetActiveScene().name == GameScenes.Ocean;
            _loadingComplete = !isOcean;
            if (isOcean) _loadingCurtain?.Show(0, 0);
            else _loadingCurtain?.HideImmediate();
        }
        private void Unbind()
        {
            _messages?.UnregisterNamedMessageHandler(Channel); _messages = null;
        }
        private void OnDestroy()
        {
            if (Instance != this) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (_loadingCurtain != null) WaveByWave.UI.GameUiPrefabs.Release(_loadingCurtain.gameObject, this);
            Unbind(); ClearWorld(false); _water?.Dispose();
            LootStressTest.ServerItemRemoved -= OnItemRemoved; Instance = null;
        }
    }
}
