using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace WaveByWave.Enemies
{
    [MaterialProperty("_EnemyFrame")]
    public struct EnemyFrameProperty : IComponentData { public float4 Value; }
    public struct EnemyVisualPose : IComponentData
    {
        public float4x4 Matrix;
        public float4 Frame;
        public float Time;
        public byte Stunned;
    }
    public struct EnemyPartOwner : IComponentData { public Entity Root; public byte Orbit; }

    [BurstCompile]
    internal struct EnemyRenderJob : IJobChunk
    {
        [ReadOnly] public ComponentLookup<EnemyVisualPose> Poses;
        [ReadOnly] public ComponentTypeHandle<EnemyPartOwner> Owners;
        [ReadOnly] public ComponentTypeHandle<RenderBounds> LocalBounds;
        public ComponentTypeHandle<LocalToWorld> Transforms;
        public ComponentTypeHandle<EnemyFrameProperty> Animations;
        public ComponentTypeHandle<WorldRenderBounds> WorldBounds;
        public ComponentTypeHandle<ChunkWorldRenderBounds> ChunkBounds;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex,
            bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var owners = chunk.GetNativeArray(ref Owners);
            var transforms = chunk.GetNativeArray(ref Transforms);
            var animations = chunk.GetNativeArray(ref Animations);
            var localBounds = chunk.GetNativeArray(ref LocalBounds);
            var worldBounds = chunk.GetNativeArray(ref WorldBounds);
            var combined = MinMaxAABB.Empty;
            for (var i = 0; i < chunk.Count; i++)
            {
                var owner = owners[i];
                if (!Poses.TryGetComponent(owner.Root, out var pose))
                { combined.Encapsulate(worldBounds[i].Value); continue; }
                var matrix = pose.Matrix;
                var animation = pose.Frame;
                if (owner.Orbit != 0)
                {
                    animation = float4.zero;
                    if (pose.Stunned != 0)
                    {
                        var phase = pose.Time * 4 + (owner.Orbit - 1) * math.PI * 2 / 3;
                        var local = new float3(math.cos(phase) * 0.28f,
                            1.15f + math.sin(phase * 2) * 0.03f, math.sin(phase) * 0.28f);
                        matrix = math.mul(matrix, float4x4.TRS(local, quaternion.RotateY(phase), new float3(0.05f)));
                    }
                    else
                    {
                        matrix.c0 = matrix.c1 = matrix.c2 = float4.zero;
                    }
                }
                transforms[i] = new LocalToWorld { Value = matrix };
                animations[i] = new EnemyFrameProperty { Value = animation };
                var bounds = AABB.Transform(matrix, localBounds[i].Value);
                worldBounds[i] = new WorldRenderBounds { Value = bounds };
                combined.Encapsulate(bounds);
            }
            // The ship's rendered pose is final only in LateUpdate. Publish both bounds
            // here, in the same pass as the matrices, so culling never uses an older pose.
            chunk.SetChunkComponentData(ref ChunkBounds, new ChunkWorldRenderBounds { Value = combined });
        }

        internal static AABB StableRenderBounds(AABB source)
        {
            // Vertex-animation textures deform vertices after CPU culling. Every part needs
            // bounds large enough for the whole animated skeleton, not its bind-pose fragment.
            source.Extents = math.max(source.Extents, new float3(1.25f, 1.5f, 1.25f));
            return source;
        }
    }

    // Driven once from MonoBehaviour.LateUpdate, after the ship's rendered pose is final.
    // Do not also add this system to an automatically updated ECS group.
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial class EnemyRenderSystem : SystemBase
    {
        private EntityQuery _parts;
        private NativeArray<EnemyVisualUpdate> _updates;
        private float _now, _scale;

        public void SetFrame(NativeArray<EnemyVisualUpdate> updates, float now, float scale)
        { _updates = updates; _now = now; _scale = scale; }

        protected override void OnCreate() => _parts = GetEntityQuery(new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<EnemyPartOwner>(), ComponentType.ReadOnly<RenderBounds>(),
                ComponentType.ReadWrite<LocalToWorld>(), ComponentType.ReadWrite<EnemyFrameProperty>(),
                ComponentType.ReadWrite<WorldRenderBounds>(), ComponentType.ChunkComponent<ChunkWorldRenderBounds>()
            }
        });

        protected override void OnUpdate()
        {
            Dependency = new EnemyVisualPoseJob
            {
                Updates = _updates,
                Interpolations = GetComponentLookup<EnemyVisualInterpolation>(),
                Poses = GetComponentLookup<EnemyVisualPose>(),
                Now = _now, RenderTime = UnityEngine.Time.unscaledTime, EffectTime = UnityEngine.Time.time,
                RotationBlend = 1f - math.exp(-18f * UnityEngine.Time.unscaledDeltaTime), Scale = _scale
            }.Schedule(_updates.Length, 64, Dependency);
            Dependency = new EnemyRenderJob
            {
                Poses = GetComponentLookup<EnemyVisualPose>(true),
                Owners = GetComponentTypeHandle<EnemyPartOwner>(true),
                LocalBounds = GetComponentTypeHandle<RenderBounds>(true),
                Transforms = GetComponentTypeHandle<LocalToWorld>(),
                Animations = GetComponentTypeHandle<EnemyFrameProperty>(),
                WorldBounds = GetComponentTypeHandle<WorldRenderBounds>(),
                ChunkBounds = GetComponentTypeHandle<ChunkWorldRenderBounds>()
            }.ScheduleParallel(_parts, Dependency);
            Dependency.Complete();
        }
    }

    public static class DotsEnemyPresentation
    {
        private sealed class View
        {
            public Entity Root;
            public readonly List<Entity> Parts = new();
            public List<Entity> Stars;
            public uint Hit;
            public float FlashUntil;
            public int Seen;
            public bool Dead;
        }
        private sealed class Template
        {
            public readonly List<Entity> Entities = new();
        }
        private sealed class Smoke
        {
            public GameObject Object;
            public ParticleSystem[] Particles;
            public GameObject Prefab;
            public float Until;
        }
        private struct SurfaceFrame
        {
            public Matrix4x4 Matrix;
            public bool Valid;
        }
        private static readonly Dictionary<int, View> Views = new();
        private static readonly Dictionary<int, Template> Templates = new();
        private static readonly Dictionary<int, Template> CombinedTemplates = new();
        private static readonly List<Material> Materials = new();
        private static readonly List<int> Removed = new();
        private static readonly List<Smoke> Smokes = new();
        private static readonly List<int> Selected = new();
        private static readonly Stack<Entity> StarPool = new();
        private static readonly Dictionary<ulong, SurfaceFrame> SurfaceFrames = new();
        private static NativeList<EnemyVisualUpdate> VisualUpdates;
        private static World _world, _source;
        private static EntityQuery _query;
        private static DotsEnemyCatalog _catalog;
        private static int _generation, _scene;
        private static Entity _starTemplate;
        private static int _smokesThisFrame;

        internal static void Update(DotsEnemyRuntime runtime)
        {
            var world = ClientServerBootstrap.ClientWorld;
            if (world == null || !world.IsCreated || runtime.Catalog == null || !runtime.Catalog.IsBaked) return;
            if (_world != world || _catalog != runtime.Catalog)
            {
                Dispose(); _world = world; _catalog = runtime.Catalog;
                _scene = DotsEnemyRuntime.SceneKey(SceneManager.GetActiveScene().name);
            }
            var scene = DotsEnemyRuntime.SceneKey(SceneManager.GetActiveScene().name);
            if (_scene != scene) { Clear(); _scene = scene; }
            var source = runtime.CanSimulate && runtime.ServerWorld != null && runtime.ServerWorld.IsCreated
                ? runtime.ServerWorld : world;
            if (_source != source)
            {
                if (_source != null && _source.IsCreated) _query.Dispose();
                _source = source;
                _query = source.EntityManager.CreateEntityQuery(typeof(DotsEnemyState));
            }
            _generation++;
            _smokesThisFrame = 0;
            using var states = _query.ToComponentDataArray<DotsEnemyState>(Allocator.Temp);
            var now = runtime.Now;
            if (!VisualUpdates.IsCreated) VisualUpdates = new NativeList<EnemyVisualUpdate>(256, Allocator.Persistent);
            VisualUpdates.Clear();
            var creationBudget = Mathf.Clamp(_catalog.SpawnsPerFrame, 1, 512);
            var clips = _catalog.BakedParts[0].Clips;
            // Only share frames within this LateUpdate, after ship presentation is final.
            // Never reuse last frame's ship pose or the server's physics pose here.
            SurfaceFrames.Clear();
            foreach (var state in states)
            {
                if (state.Id == 0 || state.Scene != scene) continue;
                if (!Views.TryGetValue(state.Id, out var view))
                {
                    if (state.Health <= 0 || creationBudget <= 0) continue;
                    creationBudget--;
                    view = Create(state);
                    Views.Add(state.Id, view);
                    PlaySmoke(_catalog.SpawnSmokePrefab, state.Position);
                }
                view.Seen = _generation;
                if (state.Health <= 0)
                {
                    if (!view.Dead)
                    {
                        PlaySmoke(_catalog.DeathSmokePrefab, state.Position);
                        DestroyParts(view); view.Dead = true;
                    }
                    continue;
                }
                if (view.Dead) continue;
                if (view.Hit != state.HitRevision)
                {
                    view.Hit = state.HitRevision; view.FlashUntil = Time.unscaledTime + _catalog.DamageFlashDuration;
                }
                var stunned = state.StunUntil > now;
                UpdateStars(view, stunned);
                var hasSurface = false;
                var surfaceFrame = Matrix4x4.identity;
                if (state.SupportId != 0)
                {
                    if (!SurfaceFrames.TryGetValue(state.SupportId, out var cachedFrame))
                    {
                        cachedFrame.Valid = runtime.TryGetSurfaceFrame(state.SupportId, false, out cachedFrame.Matrix);
                        SurfaceFrames.Add(state.SupportId, cachedFrame);
                    }
                    hasSurface = cachedFrame.Valid;
                    surfaceFrame = cachedFrame.Matrix;
                }
                var support = hasSurface ? state.SupportId : 0;
                var position = support != 0 ? state.LocalPosition : state.Position;
                var rotation = support != 0 ? state.LocalRotation : state.Rotation;
                var clipIndex = state.Animation == EnemyAnimationState.Stunned ? 0 : (int)state.Animation;
                var clip = clips[Mathf.Clamp(clipIndex, 0, clips.Length - 1)];
                var loop = state.Animation == EnemyAnimationState.Idle || state.Animation == EnemyAnimationState.Run ||
                           state.Animation == EnemyAnimationState.Stunned;
                VisualUpdates.Add(new EnemyVisualUpdate
                {
                    Root = view.Root, Position = position, Rotation = rotation, Support = support,
                    Surface = surfaceFrame, Seed = state.Seed, SampleTime = state.MovementUpdatedAt,
                    AnimationStarted = state.AnimationStarted,
                    Duration = state.AnimationDuration > 0 ? state.AnimationDuration : clip.Duration,
                    FirstRow = clip.FirstRow, FrameCount = clip.Count, FlashUntil = view.FlashUntil,
                    Loop = loop ? (byte)1 : (byte)0,
                    Stunned = stunned ? (byte)1 : (byte)0
                });
            }
            Removed.Clear();
            foreach (var pair in Views)
                if (pair.Value.Seen != _generation) { DestroyParts(pair.Value); Removed.Add(pair.Key); }
            foreach (var id in Removed) Views.Remove(id);
            // SystemBase.Update refreshes input dependencies and publishes output dependencies.
            // Calling a custom method directly bypasses that lifecycle and races LocalToWorldSystem.
            var renderSystem = _world.GetOrCreateSystemManaged<EnemyRenderSystem>();
            renderSystem.SetFrame(VisualUpdates.AsArray(), now, _catalog.VisualScale);
            renderSystem.Update();
            foreach (var smoke in Smokes)
                if (smoke.Object != null && smoke.Object.activeSelf && Time.unscaledTime >= smoke.Until)
                    smoke.Object.SetActive(false);
        }
        private static View Create(DotsEnemyState state)
        {
            var manager = _world.EntityManager;
            var view = new View { Root = manager.CreateEntity(typeof(EnemyVisualPose), typeof(EnemyVisualInterpolation)),
                Hit = state.HitRevision };
            if (_catalog.UseCombinedVariants)
            {
                var count = 0;
                foreach (var variant in _catalog.CombinedVariants)
                    if (variant.CombatType == state.CombatType && variant.Visual?.Mesh != null) count++;
                // Spawn seeds are always odd; skip that fixed bit so every variant is reachable.
                var chosen = count > 0 ? (int)((state.Seed >> 1) % (uint)count) : -1;
                for (var i = 0; chosen >= 0 && i < _catalog.CombinedVariants.Count; i++)
                {
                    var variant = _catalog.CombinedVariants[i];
                    if (variant.CombatType != state.CombatType || variant.Visual?.Mesh == null || chosen-- != 0) continue;
                    if (!CombinedTemplates.TryGetValue(i, out var combined))
                    {
                        combined = CreateTemplate(variant.Visual, -1);
                        CombinedTemplates.Add(i, combined);
                    }
                    InstantiateParts(combined, view);
                    return view;
                }
            }
            SelectParts(state.Seed, state.CombatType, _catalog, Selected);
            var skin = (int)(state.Seed % (uint)Mathf.Max(1, _catalog.SkeletonMaterials.Length));
            foreach (var index in Selected)
            {
                InstantiateParts(GetTemplate(index, skin), view);
            }
            return view;
        }

        private static void InstantiateParts(Template template, View view)
        {
            var manager = _world.EntityManager;
            foreach (var source in template.Entities)
            {
                var entity = manager.Instantiate(source);
                manager.SetComponentData(entity, new EnemyPartOwner { Root = view.Root });
                view.Parts.Add(entity);
            }
        }

        private static void UpdateStars(View view, bool stunned)
        {
            if (!stunned) { ReleaseStars(view); return; }
            if (view.Stars != null && view.Stars.Count != 0) return;
            EnsureStar();
            if (_starTemplate == Entity.Null) return;
            view.Stars ??= new List<Entity>(3);
            var manager = _world.EntityManager;
            for (byte i = 1; i <= 3; i++)
            {
                var entity = StarPool.Count > 0 ? StarPool.Pop() : manager.Instantiate(_starTemplate);
                manager.SetComponentData(entity, new EnemyPartOwner { Root = view.Root, Orbit = i });
                manager.SetEnabled(entity, true);
                view.Stars.Add(entity);
            }
        }

        private static void ReleaseStars(View view)
        {
            if (view.Stars == null || view.Stars.Count == 0) return;
            if (_world != null && _world.IsCreated)
            {
                var manager = _world.EntityManager;
                foreach (var entity in view.Stars)
                {
                    if (!manager.Exists(entity)) continue;
                    // Disabled pooled entities are absent from rendering and transform
                    // jobs, unlike the old always-updated zero-scale stars on every enemy.
                    manager.SetEnabled(entity, false);
                    StarPool.Push(entity);
                }
            }
            view.Stars.Clear();
        }
        public static void SelectParts(uint seed, EnemyCombatType type, DotsEnemyCatalog catalog, List<int> selected)
        {
            selected.Clear();
            var random = new Unity.Mathematics.Random(seed | 1u);
            var categories = new[] { EnemyBakedPartCategory.Bandana, EnemyBakedPartCategory.Hat,
                EnemyBakedPartCategory.Coat, EnemyBakedPartCategory.EyePatch, EnemyBakedPartCategory.Earring,
                EnemyBakedPartCategory.GloveLeft, EnemyBakedPartCategory.BootLeft };
            var selectedSources = new HashSet<int>();
            var handPair = 0; var legPair = 0; var hook = false;
            foreach (var category in categories)
            {
                if (random.NextFloat() < 0.4f) continue;
                var choices = new List<int>();
                for (var i = 0; i < catalog.PartSources.Count; i++)
                    if (catalog.PartSources[i].Category == category ||
                        category == EnemyBakedPartCategory.BootLeft &&
                        catalog.PartSources[i].Category == EnemyBakedPartCategory.WoodenLegLeft ||
                        category == EnemyBakedPartCategory.GloveLeft &&
                        catalog.PartSources[i].Category == EnemyBakedPartCategory.HookLeft) choices.Add(i);
                if (choices.Count == 0) continue;
                var source = choices[random.NextInt(choices.Count)];
                selectedSources.Add(source);
                if (category == EnemyBakedPartCategory.GloveLeft) handPair = catalog.PartSources[source].Pair;
                if (catalog.PartSources[source].Category == EnemyBakedPartCategory.HookLeft) hook = true;
                if (category == EnemyBakedPartCategory.BootLeft) legPair = catalog.PartSources[source].Pair;
            }
            var weapon = type == EnemyCombatType.Pistol ? EnemyBakedPartCategory.PistolWeapon :
                type == EnemyCombatType.Rifle ? EnemyBakedPartCategory.RifleWeapon : EnemyBakedPartCategory.MeleeWeapon;
            var weapons = new List<int>();
            for (var i = 0; i < catalog.PartSources.Count; i++)
            {
                var source = catalog.PartSources[i];
                if (source.Category == weapon) weapons.Add(i);
                if (handPair != 0 && source.Pair == handPair && source.Category == EnemyBakedPartCategory.GloveRight ||
                    legPair != 0 && source.Pair == legPair && (source.Category == EnemyBakedPartCategory.BootRight ||
                    source.Category == EnemyBakedPartCategory.WoodenLegRight)) selectedSources.Add(i);
            }
            if (weapons.Count > 0) selectedSources.Add(weapons[random.NextInt(weapons.Count)]);
            for (var i = 0; i < catalog.BakedParts.Count; i++)
            {
                var part = catalog.BakedParts[i];
                if (part.Category == EnemyBakedPartCategory.Body)
                {
                    if (handPair != 0 && part.BodySlot.StartsWith("Hand_", StringComparison.Ordinal) ||
                        hook && part.BodySlot == "Hand_Left" ||
                        legPair != 0 && part.BodySlot.StartsWith("Leg_", StringComparison.Ordinal)) continue;
                    selected.Add(i);
                }
                else if (selectedSources.Contains(part.SourceIndex)) selected.Add(i);
            }
        }
        private static Template GetTemplate(int index, int skin)
        {
            if (_catalog.BakedParts[index].Category != EnemyBakedPartCategory.Body) skin = 0;
            var key = index * 16 + skin;
            if (Templates.TryGetValue(key, out var template)) return template;
            var part = _catalog.BakedParts[index];
            template = CreateTemplate(part, skin);
            Templates.Add(key, template);
            return template;
        }

        private static Template CreateTemplate(EnemyBakedPart part, int skin)
        {
            var materials = new Material[part.Materials.Length];
            for (var i = 0; i < materials.Length; i++)
            {
                var material = new Material(part.Materials[i]) { enableInstancing = true };
                material.SetFloat("_DayMinimumLight", _catalog.DayMinimumLight);
                material.SetFloat("_NightMinimumLight", _catalog.NightMinimumLight);
                if (part.Category == EnemyBakedPartCategory.Body && skin >= 0 && skin < _catalog.SkeletonMaterials.Length)
                {
                    var source = _catalog.SkeletonMaterials[skin];
                    if (source != null)
                    {
                        var texture = source.HasProperty("_BaseMap") ? source.GetTexture("_BaseMap") : source.mainTexture;
                        material.SetTexture("_BaseMap", texture);
                    }
                }
                materials[i] = material;
                Materials.Add(material);
            }
            var array = new RenderMeshArray(materials, new[] { part.Mesh });
            var template = new Template();
            var manager = _world.EntityManager;
            var description = new RenderMeshDescription(ShadowCastingMode.On, true,
                MotionVectorGenerationMode.ForceNoMotion, 0, uint.MaxValue, LightProbeUsage.Off);
            for (ushort sub = 0; sub < Mathf.Min(part.Mesh.subMeshCount, materials.Length); sub++)
            {
                var entity = manager.CreateEntity(typeof(LocalToWorld), typeof(EnemyPartOwner), typeof(EnemyFrameProperty));
                RenderMeshUtility.AddComponents(entity, manager, description, array,
                    MaterialMeshInfo.FromRenderMeshArrayIndices(sub, 0, sub));
                var bounds = manager.GetComponentData<RenderBounds>(entity);
                bounds.Value = EnemyRenderJob.StableRenderBounds(bounds.Value);
                manager.SetComponentData(entity, bounds);
                manager.AddComponent<Prefab>(entity);
                template.Entities.Add(entity);
            }
            return template;
        }
        private static void EnsureStar()
        {
            if (_starTemplate != Entity.Null || _catalog.StunEffectPrefab == null) return;
            var filter = _catalog.StunEffectPrefab.GetComponentInChildren<MeshFilter>(true);
            var renderer = filter != null ? filter.GetComponent<MeshRenderer>() : null;
            if (renderer == null || filter.sharedMesh == null) return;
            var manager = _world.EntityManager;
            _starTemplate = manager.CreateEntity(typeof(LocalToWorld), typeof(EnemyPartOwner), typeof(EnemyFrameProperty));
            var array = new RenderMeshArray(renderer.sharedMaterials, new[] { filter.sharedMesh });
            RenderMeshUtility.AddComponents(_starTemplate, manager,
                new RenderMeshDescription(ShadowCastingMode.Off, false, MotionVectorGenerationMode.ForceNoMotion,
                    0, uint.MaxValue, LightProbeUsage.Off), array, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            manager.AddComponent<Prefab>(_starTemplate);
        }
        private static void PlaySmoke(GameObject prefab, Vector3 position)
        {
            if (prefab == null || _smokesThisFrame >= 8) return;
            var camera = Camera.main;
            if (camera != null && (camera.transform.position - position).sqrMagnitude > 80 * 80) return;
            _smokesThisFrame++;
            Smoke smoke = null;
            foreach (var entry in Smokes)
                if (entry.Prefab == prefab && entry.Object != null && !entry.Object.activeSelf) { smoke = entry; break; }
            if (smoke == null)
            {
                if (Smokes.Count >= 48) return;
                var obj = Object.Instantiate(prefab);
                Object.DontDestroyOnLoad(obj);
                smoke = new Smoke { Object = obj, Prefab = prefab, Particles = obj.GetComponentsInChildren<ParticleSystem>(true) };
                Smokes.Add(smoke);
            }
            smoke.Object.transform.SetPositionAndRotation(position + Vector3.up * 0.8f, Quaternion.identity);
            smoke.Object.SetActive(true);
            var duration = 1f;
            foreach (var particle in smoke.Particles)
            {
                particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = particle.main;
                duration = Mathf.Max(duration, main.duration + main.startLifetime.constantMax);
                particle.Play(true);
                // Existing death dust uses a script-emitted burst. Keep its prefab shape,
                // material and lifetime while pooling its transient instances.
                if (!particle.emission.enabled) particle.Emit(42);
            }
            smoke.Until = Time.unscaledTime + duration;
        }
        private static void DestroyParts(View view)
        {
            ReleaseStars(view);
            if (_world != null && _world.IsCreated)
            {
                foreach (var entity in view.Parts) if (_world.EntityManager.Exists(entity)) _world.EntityManager.DestroyEntity(entity);
                if (_world.EntityManager.Exists(view.Root)) _world.EntityManager.DestroyEntity(view.Root);
            }
            view.Parts.Clear();
        }
        internal static void Clear()
        {
            foreach (var view in Views.Values) DestroyParts(view);
            Views.Clear();
            if (_world != null && _world.IsCreated)
                foreach (var entity in StarPool)
                    if (_world.EntityManager.Exists(entity)) _world.EntityManager.DestroyEntity(entity);
            StarPool.Clear();
            SurfaceFrames.Clear();
            foreach (var smoke in Smokes) if (smoke.Object != null) smoke.Object.SetActive(false);
        }
        internal static void Dispose()
        {
            Clear();
            if (VisualUpdates.IsCreated) VisualUpdates.Dispose();
            if (_world != null && _world.IsCreated)
            {
                foreach (var template in Templates.Values)
                    foreach (var entity in template.Entities)
                        if (_world.EntityManager.Exists(entity)) _world.EntityManager.DestroyEntity(entity);
                foreach (var template in CombinedTemplates.Values)
                    foreach (var entity in template.Entities)
                        if (_world.EntityManager.Exists(entity)) _world.EntityManager.DestroyEntity(entity);
                if (_world.EntityManager.Exists(_starTemplate)) _world.EntityManager.DestroyEntity(_starTemplate);
            }
            foreach (var material in Materials) if (material != null) Object.Destroy(material);
            foreach (var smoke in Smokes) if (smoke.Object != null) Object.Destroy(smoke.Object);
            Smokes.Clear(); Materials.Clear(); Templates.Clear(); CombinedTemplates.Clear();
            if (_source != null && _source.IsCreated) _query.Dispose();
            _starTemplate = Entity.Null; _world = _source = null; _catalog = null;
        }
    }
}
