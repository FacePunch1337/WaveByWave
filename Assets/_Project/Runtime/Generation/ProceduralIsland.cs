using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using WaveByWave.Collision;

namespace WaveByWave.Generation
{
    [DisallowMultipleComponent]
    public sealed class ProceduralIsland : MonoBehaviour
    {
        private sealed class Chunk
        {
            public int3 Minimum, Maximum;
            public Mesh Mesh;
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public MeshCollider Collider;
        }
        public int Id { get; private set; }
        public IslandSize Size { get; private set; }
        public uint Seed { get; private set; }
        public bool GeometryReady => _initialized && !_meshing && _dirty.Count == 0;
        public bool Ready => GeometryReady && _decorated && _hasCompletedInitialBuild;
        public bool InitialBuildComplete => _hasCompletedInitialBuild;
        public int LastDigRevision { get; private set; }
        public float Diameter => _parameters.Diameter;
        public float BedrockDepth => _parameters.SandDepth;
        public OceanGenerationSettings Settings { get; private set; }
        private IslandFieldParameters _parameters;
        private NativeArray<float2> _density;
        private JobHandle _job;
        private bool _initialized, _meshing, _decorated, _hasCompletedInitialBuild, _presentationRequested;
        private bool _staticCollisionDirty;
        private IslandMeshJob _meshJob;
        private int _activeChunk;
        private readonly List<Chunk> _chunks = new();
        private readonly Queue<int> _dirty = new();
        private readonly HashSet<int> _queued = new();
        private readonly List<Transform> _decorations = new();

        public static IslandFieldParameters CreateParameters(IslandSize size, OceanGenerationSettings settings)
        {
            var diameter = settings.Diameter(size);
            var cell = Mathf.Clamp(settings.VoxelSize, 0.2f, 0.6f);
            var horizontalCells = Mathf.CeilToInt((diameter + cell * 4f) / cell);
            var bottom = -settings.SandDepth - 2f;
            var verticalCells = Mathf.CeilToInt((settings.SandHeight + 1f - bottom) / cell);
            return new IslandFieldParameters
            {
                Points = new int3(horizontalCells + 1, verticalCells + 1, horizontalCells + 1),
                Origin = new float3(-horizontalCells * cell * 0.5f, bottom, -horizontalCells * cell * 0.5f),
                CellSize = cell, Diameter = diameter, SandHeight = settings.SandHeight,
                SandDepth = settings.SandDepth, NoiseScale = settings.NoiseScale,
                Irregularity = settings.ShoreIrregularity, Seed = 1u
            };
        }

        public void Initialize(int id, IslandSize size, uint seed, OceanGenerationSettings settings)
        {
            BeginInitialize(id, size, seed, settings);
            var horizontalCells = _parameters.Points.x - 1;
            var verticalCells = _parameters.Points.y - 1;
            var chunkSize = Mathf.Clamp(settings.ChunkCells, 8, 64);
            for (var z = 0; z < horizontalCells; z += chunkSize)
            for (var y = 0; y < verticalCells; y += chunkSize)
            for (var x = 0; x < horizontalCells; x += chunkSize)
            {
                _chunks.Add(new Chunk { Minimum = new int3(x, y, z),
                    Maximum = math.min(new int3(x, y, z) + chunkSize, _parameters.Points - 1) });
                Enqueue(_chunks.Count - 1);
            }
        }

        public void InitializeBaked(int id, IslandSize size, uint seed, OceanGenerationSettings settings,
            BakedIslandChunk[] bakedChunks, Transform decorationRoot)
        {
            BeginInitialize(id, size, seed, settings);
            _decorated = true;
            _hasCompletedInitialBuild = true;
            _presentationRequested = true;
            if (bakedChunks != null)
                foreach (var baked in bakedChunks)
                {
                    if (baked == null || !baked.TryGetComponents(out var filter, out var renderer, out var collider))
                        continue;
                    var source = filter.sharedMesh;
                    var mesh = source != null ? Instantiate(source) : new Mesh();
                    mesh.name = $"Baked island {id} chunk {baked.Minimum.x},{baked.Minimum.y},{baked.Minimum.z}";
                    mesh.indexFormat = IndexFormat.UInt32; mesh.MarkDynamic();
                    filter.sharedMesh = mesh; collider.sharedMesh = mesh.vertexCount > 0 ? mesh : null;
                    renderer.sharedMaterial = settings.GroundMaterial; renderer.enabled = mesh.vertexCount > 0;
                    _chunks.Add(new Chunk
                    {
                        Minimum = new int3(baked.Minimum.x, baked.Minimum.y, baked.Minimum.z),
                        Maximum = new int3(baked.Maximum.x, baked.Maximum.y, baked.Maximum.z),
                        Mesh = mesh, Filter = filter, Renderer = renderer, Collider = collider });
                    collider.enabled = mesh.vertexCount > 0;
                }
            if (decorationRoot != null)
                for (var i = 0; i < decorationRoot.childCount; i++)
                    _decorations.Add(decorationRoot.GetChild(i));
        }

        private void BeginInitialize(int id, IslandSize size, uint seed, OceanGenerationSettings settings)
        {
            if (_density.IsCreated) throw new InvalidOperationException("Island is already initialized.");
            Id = id; Size = size; Seed = seed == 0 ? 1u : seed; Settings = settings;
            _parameters = CreateParameters(size, settings); _parameters.Seed = Seed;
            _density = new NativeArray<float2>(_parameters.Points.x * _parameters.Points.y * _parameters.Points.z,
                Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _job = new IslandInitializeJob { Parameters = _parameters, Density = _density }.Schedule(_density.Length, 128);
        }

        private void Update()
        {
            if (!_density.IsCreated) return;
            if (!_initialized)
            {
                if (!_job.IsCompleted) return;
                _job.Complete(); _initialized = true;
            }
            for (var i = 0; i < Mathf.Max(1, Settings.MeshUploadsPerFrame); i++)
            {
                if (_meshing)
                {
                    if (!_job.IsCompleted) break;
                    FinishMesh();
                }
                if (_dirty.Count == 0) break;
                _activeChunk = _dirty.Dequeue(); _queued.Remove(_activeChunk);
                var chunk = _chunks[_activeChunk];
                _meshJob = new IslandMeshJob { Density = _density, Parameters = _parameters,
                    Minimum = chunk.Minimum, Maximum = chunk.Maximum,
                    Vertices = new NativeList<float3>(1024, Allocator.Persistent),
                    Normals = new NativeList<float3>(1024, Allocator.Persistent),
                    Colors = new NativeList<Color32>(1024, Allocator.Persistent) };
                _job = _meshJob.Schedule(); _meshing = true;
            }
            if (GeometryReady && !_decorated)
            {
                _decorated = true;
                CreateDecorations();
            }
            if (GeometryReady && _decorated && !_hasCompletedInitialBuild)
            {
                _hasCompletedInitialBuild = true;
                ApplyPresentationVisibility();
            }
            if (GeometryReady && _staticCollisionDirty)
            {
                _staticCollisionDirty = false;
                KinematicShipCollision.InvalidateAllStaticObstacles();
            }
        }

        private void FinishMesh()
        {
            _job.Complete();
            var chunk = _chunks[_activeChunk];
            var isInitialBuild = !_hasCompletedInitialBuild;
            if (_meshJob.Vertices.Length > 0 || chunk.Mesh != null)
            {
                if (chunk.Mesh == null)
                {
                    var go = Instantiate(Settings.ChunkPrefab, transform);
                    go.name = $"Ground {chunk.Minimum.x},{chunk.Minimum.y},{chunk.Minimum.z}";
                    go.transform.localPosition = Vector3.zero; go.transform.localRotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one;
                    chunk.Filter = go.GetComponent<MeshFilter>(); chunk.Collider = go.GetComponent<MeshCollider>();
                    chunk.Renderer = go.GetComponent<MeshRenderer>();
                    chunk.Renderer.sharedMaterial = Settings.GroundMaterial;
                }
                var previousMesh = chunk.Mesh;
                Mesh replacementMesh = null;
                if (_meshJob.Vertices.Length > 0)
                {
                    replacementMesh = new Mesh
                    {
                        name = $"Island {Id} chunk",
                        indexFormat = IndexFormat.UInt32
                    };
                    replacementMesh.MarkDynamic();
                    replacementMesh.SetVertices(_meshJob.Vertices.AsArray());
                    replacementMesh.SetNormals(_meshJob.Normals.AsArray());
                    replacementMesh.SetColors(_meshJob.Colors.AsArray());
                    var indices = new int[_meshJob.Vertices.Length];
                    for (var n = 0; n < indices.Length; n++) indices[n] = n;
                    replacementMesh.SetIndices(indices, MeshTopology.Triangles, 0);
                    replacementMesh.RecalculateBounds();
                }

                // Keep the previous visual and PhysX shape alive until the complete
                // replacement is ready. Clearing the live mesh made the island vanish
                // for a frame and let players (and ships) fall through while digging.
                chunk.Mesh = replacementMesh;
                chunk.Filter.sharedMesh = replacementMesh;
                chunk.Collider.sharedMesh = replacementMesh;
                var hasGeometry = replacementMesh != null;
                if (chunk.Renderer != null)
                    chunk.Renderer.enabled = _hasCompletedInitialBuild && _presentationRequested &&
                                             hasGeometry;
                chunk.Collider.enabled = _hasCompletedInitialBuild && _presentationRequested &&
                                         hasGeometry;
                if (previousMesh != null)
                    Destroy(previousMesh);
                if (isInitialBuild)
                    _staticCollisionDirty = true;
            }
            _meshJob.Vertices.Dispose(); _meshJob.Normals.Dispose(); _meshJob.Colors.Dispose();
            _meshing = false;
        }

        public bool ApplyDig(int revision, Vector3 localCenter, float radius)
        {
            if (!_density.IsCreated || revision <= LastDigRevision) return false;
            _job.Complete();
            if (_meshing) FinishMesh();
            _initialized = true;
            var grid = ((float3)localCenter - _parameters.Origin) / _parameters.CellSize;
            var extent = (radius + Mathf.Max(0f, Settings.DigSmoothing)) / _parameters.CellSize + 1f;
            var min = math.clamp((int3)math.floor(grid - extent), int3.zero, _parameters.Points - 1);
            var max = math.clamp((int3)math.ceil(grid + extent), int3.zero, _parameters.Points - 1);
            _job = new IslandDigJob { Density = _density, Parameters = _parameters,
                Center = localCenter, Radius = radius, Smoothing = Settings.DigSmoothing,
                Minimum = min, Maximum = max }.Schedule();
            _job.Complete();
            LastDigRevision = revision;
            for (var i = 0; i < _chunks.Count; i++)
                if (math.all(_chunks[i].Maximum >= min - 1) && math.all(_chunks[i].Minimum <= max + 1)) Enqueue(i);
            // Decorations whose supporting sand has been removed must not float over a hole.
            for (var i = _decorations.Count - 1; i >= 0; i--)
            {
                var decoration = _decorations[i];
                if (decoration == null) { _decorations.RemoveAt(i); continue; }
                if (DensityAt(decoration.position - Vector3.up * 0.15f).x < -0.1f)
                { Destroy(decoration.gameObject); _decorations.RemoveAt(i); }
            }
            return true;
        }

        private void Enqueue(int i) { if (_queued.Add(i)) _dirty.Enqueue(i); }

        public void SetPresentationVisible(bool visible)
        {
            if (_presentationRequested == visible)
                return;
            _presentationRequested = visible;
            ApplyPresentationVisibility();
            KinematicShipCollision.InvalidateAllStaticObstacles();
        }

        private void ApplyPresentationVisibility()
        {
            var visible = _hasCompletedInitialBuild && _presentationRequested;
            foreach (var chunk in _chunks)
            {
                var hasGeometry = chunk.Mesh != null && chunk.Mesh.vertexCount > 0;
                if (chunk.Renderer != null) chunk.Renderer.enabled = visible && hasGeometry;
                if (chunk.Collider != null) chunk.Collider.enabled = visible && hasGeometry;
            }
            foreach (var decoration in _decorations)
                if (decoration != null) decoration.gameObject.SetActive(visible);
        }

        public float2 DensityAt(Vector3 worldPosition) => SampleDensity(worldPosition, false);

        private float2 SampleDensity(Vector3 worldPosition, bool original)
        {
            if (!_density.IsCreated) return new float2(-100f);
            _job.Complete(); _initialized = true;
            var g = ((float3)transform.InverseTransformPoint(worldPosition) - _parameters.Origin) / _parameters.CellSize;
            if (math.any(g < 0f) || math.any(g >= (float3)(_parameters.Points - 1))) return new float2(-100f);
            var p = (int3)math.floor(g); var t = math.frac(g);
            var a = math.lerp(Sample(p, original), Sample(p + new int3(1, 0, 0), original), t.x);
            var b = math.lerp(Sample(p + new int3(0, 1, 0), original), Sample(p + new int3(1, 1, 0), original), t.x);
            var c = math.lerp(Sample(p + new int3(0, 0, 1), original), Sample(p + new int3(1, 0, 1), original), t.x);
            var d = math.lerp(Sample(p + new int3(0, 1, 1), original), Sample(p + new int3(1, 1, 1), original), t.x);
            return math.lerp(math.lerp(a, b, t.y), math.lerp(c, d, t.y), t.z);
        }

        private float2 Sample(int3 p, bool original) => original
            ? _parameters.InitialDensity(_parameters.Position(p)) : _density[_parameters.Index(p)];

        public bool ContainsHorizontal(Vector3 position, float margin = 0f)
        {
            var local = transform.InverseTransformPoint(position);
            return new Vector2(local.x, local.z).sqrMagnitude < Mathf.Pow(Diameter * 0.5f + margin, 2f);
        }

        public bool TrySurface(float x, float z, out Vector3 point, out Vector3 normal, bool original = false)
        {
            point = Vector3.zero; normal = Vector3.up;
            if (!_density.IsCreated) return false;
            var local = new Vector3(x, Settings.SandHeight + 0.5f, z);
            var previous = local.y;
            for (; local.y > _parameters.Origin.y; local.y -= _parameters.CellSize * 0.5f)
            {
                var d = SampleDensity(transform.TransformPoint(local), original);
                if (math.cmax(d) > 0f)
                {
                    var lower = local.y; var upper = previous;
                    for (var n = 0; n < 8; n++)
                    {
                        local.y = (lower + upper) * 0.5f;
                        if (math.cmax(SampleDensity(transform.TransformPoint(local), original)) > 0f) lower = local.y;
                        else upper = local.y;
                    }
                    local.y = upper; point = transform.TransformPoint(local);
                    var h = _parameters.CellSize * 0.5f;
                    normal = new Vector3(
                        math.cmax(SampleDensity(point - Vector3.right * h, original)) - math.cmax(SampleDensity(point + Vector3.right * h, original)),
                        math.cmax(SampleDensity(point - Vector3.up * h, original)) - math.cmax(SampleDensity(point + Vector3.up * h, original)),
                        math.cmax(SampleDensity(point - Vector3.forward * h, original)) - math.cmax(SampleDensity(point + Vector3.forward * h, original))).normalized;
                    return true;
                }
                previous = local.y;
            }
            return false;
        }

        private void CreateDecorations()
        {
            var random = new Unity.Mathematics.Random(Seed == 0 ? 1u : Seed);
            var reserved = new List<Vector3>();
            foreach (var entry in Settings.Decorations)
            {
                if (entry?.Prefab == null) continue;
                var count = Mathf.RoundToInt(Diameter * Diameter * Mathf.Max(0f, entry.InstancesPerSquareMetre));
                for (var n = 0; n < count; n++)
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    var p = random.NextFloat2(-Diameter * 0.45f, Diameter * 0.45f);
                    // Choose from the original field, even after a late-join dig replay.
                    // Excavation removes decorations; it must not reroll the other trees.
                    if (!TrySurface(p.x, p.y, out var point, out var normal, true) ||
                        point.y < transform.position.y + entry.MinimumHeightAboveWater ||
                        Vector3.Angle(normal, Vector3.up) > entry.MaximumSlope) continue;
                    var crowded = false;
                    foreach (var existing in reserved)
                        if ((existing - point).sqrMagnitude < entry.Spacing * entry.Spacing) { crowded = true; break; }
                    if (crowded) continue;
                    reserved.Add(point);
                    var rotation = (entry.AlignToSurface ? Quaternion.FromToRotation(Vector3.up, normal) : Quaternion.identity) *
                        Quaternion.Euler(0f, random.NextFloat(0f, 360f), 0f);
                    var minimumScale = Mathf.Max(0.01f, entry.ScaleRange.x);
                    var scale = random.NextFloat(minimumScale, Mathf.Max(minimumScale + 0.001f, entry.ScaleRange.y));
                    if (DensityAt(point - Vector3.up * 0.15f).x < -0.1f) break;
                    var instance = Instantiate(entry.Prefab, point, rotation, transform);
                    instance.transform.localScale *= scale;
                    instance.SetActive(_hasCompletedInitialBuild && _presentationRequested);
                    _decorations.Add(instance.transform);
                    break;
                }
            }
        }

        private void OnDestroy()
        {
            KinematicShipCollision.InvalidateAllStaticObstacles();
            _job.Complete();
            if (_meshing) { _meshJob.Vertices.Dispose(); _meshJob.Normals.Dispose(); _meshJob.Colors.Dispose(); }
            if (_density.IsCreated) _density.Dispose();
            foreach (var chunk in _chunks) if (chunk.Mesh != null) Destroy(chunk.Mesh);
        }
    }
}
