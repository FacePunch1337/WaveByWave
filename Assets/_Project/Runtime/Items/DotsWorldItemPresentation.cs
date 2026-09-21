using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace WaveByWave.Items
{
    [MaterialProperty("_EffectSeed")]
    internal struct LootRaritySeed : IComponentData { public float Value; }

    // One render entity per item. The shared mesh contains a beam, halo, ground ring
    // and six spark quads. The vertex shader animates/billboards them on the GPU.
    internal sealed class DotsLootRarityEffect : IDisposable
    {
        private sealed class Template
        {
            public RenderMeshArray RenderArray;
            public Material Material;
            public Color RarityColor;
        }

        // Cache by authoring component, not the material reference: replacing the
        // material in the prefab must also update already spawned loot.
        private sealed class MaterialGroup
        {
            public readonly Dictionary<ItemRarity, Template> Variants = new();
            private readonly LootRarityGlow _settings;
            private Material _source;
            private Shader _shader;
            private int _crc;

            public MaterialGroup(LootRarityGlow settings)
            {
                _settings = settings;
                _source = settings.DotsMaterial;
                _shader = _source.shader;
                _crc = _source.ComputeCRC();
            }

            public Template GetTemplate(ItemRarity rarity)
            {
                if (Variants.TryGetValue(rarity, out var existing)) return existing;
                var source = _settings.DotsMaterial;
                var material = new Material(source)
                {
                    name = $"DOTS loot rarity {rarity}", enableInstancing = true,
                    hideFlags = HideFlags.DontSave
                };
                var template = new Template
                {
                    Material = material,
                    RarityColor = GetRarityColor(rarity),
                    RenderArray = new RenderMeshArray(new[] { material }, new[] { GetMesh() })
                };
                ApplyRarityColor(template, source);
                Variants.Add(rarity, template);
                return template;
            }

            public int Refresh()
            {
                var source = _settings != null ? _settings.DotsMaterial : null;
                // Keep the last valid material during a temporary empty Inspector field.
                if (source == null) return 0;
                var shader = source.shader;
                var crc = source.ComputeCRC();
                if (source == _source && shader == _shader && crc == _crc) return 0;
                _source = source;
                _shader = shader;
                _crc = crc;
                foreach (var template in Variants.Values)
                {
                    var material = template.Material;
                    if (material.shader != shader) material.shader = shader;
                    material.CopyPropertiesFromMaterial(source);
                    material.enableInstancing = true;
                    ApplyRarityColor(template, source);
                }
                // Existing render entities keep the exact same material/RenderMeshArray.
                return Variants.Count;
            }
        }

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly Dictionary<LootRarityGlow, MaterialGroup> MaterialGroups = new();
        private static bool _refreshSubscribed;
        private static double _nextMaterialCheck;
        private const double MaterialCheckInterval = 0.05;
        private readonly World _world;
        private Entity _entity;

        private DotsLootRarityEffect(World world, Template template, int stableSeed)
        {
            _world = world;
            var manager = world.EntityManager;
            _entity = manager.CreateEntity(typeof(LocalToWorld), typeof(LootRaritySeed));
            RenderMeshUtility.AddComponents(_entity, manager,
                new RenderMeshDescription(ShadowCastingMode.Off, false,
                    MotionVectorGenerationMode.ForceNoMotion, 0, uint.MaxValue, LightProbeUsage.Off),
                template.RenderArray, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            manager.SetComponentData(_entity, new LootRaritySeed
            {
                Value = (math.hash(new uint2(unchecked((uint)stableSeed), 0x9E3779B9u)) & 0xFFFFu) / 65536f
            });
        }

        public static DotsLootRarityEffect Create(World world, ItemRarity rarity, GameObject effectPrefab,
            int stableSeed)
        {
            if (world == null || !world.IsCreated || effectPrefab == null) return null;
            var settings = effectPrefab.GetComponent<LootRarityGlow>();
            var source = settings != null ? settings.DotsMaterial : null;
            if (source == null) return null;
            if (!MaterialGroups.TryGetValue(settings, out var group))
            {
                group = new MaterialGroup(settings);
                MaterialGroups.Add(settings, group);
            }
            if (!_refreshSubscribed)
            {
                RenderPipelineManager.beginContextRendering += RefreshBeforeRendering;
                _refreshSubscribed = true;
            }
            return new DotsLootRarityEffect(world, group.GetTemplate(rarity), stableSeed);
        }

        private static Color GetRarityColor(ItemRarity rarity) => rarity switch
        {
            ItemRarity.Uncommon => new Color(0.3f, 1f, 0.4f),
            ItemRarity.Rare => new Color(0.15f, 0.55f, 1f),
            ItemRarity.Epic => new Color(0.8f, 0.25f, 1f),
            ItemRarity.Legendary => new Color(1f, 0.65f, 0.12f),
            _ => new Color(0.8f, 0.9f, 1f)
        };

        private static void ApplyRarityColor(Template template, Material source)
        {
            if (!source.HasProperty(BaseColorId)) return;
            var color = template.RarityColor;
            color.a = source.GetColor(BaseColorId).a;
            template.Material.SetColor(BaseColorId, color);
        }

        private static void RefreshBeforeRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            // One bounded check per effect prefab, not per item or camera. Realtime
            // also allows tuning while Play Mode is paused. No material copies on
            // unchanged frames, no entity iteration or structural changes.
            if (!Application.isPlaying) return;
            var now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextMaterialCheck) return;
            _nextMaterialCheck = now + MaterialCheckInterval;
            foreach (var group in MaterialGroups.Values) group.Refresh();
        }

        private static Mesh _mesh;
        private static Mesh GetMesh()
        {
            if (_mesh != null) return _mesh;
            const int partCount = 9;
            var vertices = new Vector3[partCount * 4];
            var uv = new Vector2[vertices.Length];
            var roles = new Vector2[vertices.Length];
            var indices = new int[partCount * 6];
            for (var part = 0; part < partCount; part++)
            {
                var first = part * 4;
                for (var corner = 0; corner < 4; corner++)
                {
                    var x = corner == 1 || corner == 2 ? 1f : 0f;
                    var y = corner >= 2 ? 1f : 0f;
                    uv[first + corner] = new Vector2(x, y);
                    vertices[first + corner] = new Vector3(x - 0.5f, y - 0.5f, 0f);
                    roles[first + corner] = new Vector2(part, 0f);
                }
                var index = part * 6;
                indices[index] = first; indices[index + 1] = first + 1; indices[index + 2] = first + 2;
                indices[index + 3] = first; indices[index + 4] = first + 2; indices[index + 5] = first + 3;
            }
            _mesh = new Mesh { name = "DOTS Loot Beacon Cards", hideFlags = HideFlags.DontSave };
            _mesh.vertices = vertices; _mesh.uv = uv; _mesh.uv2 = roles; _mesh.triangles = indices;
            // Shader property limits keep GPU displacement inside the culling bounds.
            _mesh.bounds = new Bounds(new Vector3(0f, 2.5f, 0f), new Vector3(6f, 9f, 6f));
            _mesh.UploadMeshData(true);
            return _mesh;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetResources()
        {
            RenderPipelineManager.beginContextRendering -= RefreshBeforeRendering;
            _refreshSubscribed = false;
            _nextMaterialCheck = 0;
            foreach (var group in MaterialGroups.Values)
                foreach (var template in group.Variants.Values)
                    if (template.Material != null) UnityEngine.Object.Destroy(template.Material);
            MaterialGroups.Clear();
            if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
            _mesh = null;
        }

        public void SetPose(Vector3 position, float scale = 1f)
        {
            if (_world == null || !_world.IsCreated || _entity == Entity.Null) return;
            _world.EntityManager.SetComponentData(_entity, new LocalToWorld
            {
                Value = float4x4.TRS(position, quaternion.identity, new float3(Mathf.Max(0.01f, scale)))
            });
        }

        public void Dispose()
        {
            if (_world != null && _world.IsCreated && _entity != Entity.Null &&
                _world.EntityManager.Exists(_entity)) _world.EntityManager.DestroyEntity(_entity);
            _entity = Entity.Null;
        }
    }

    // Presentation-only entities are driven after ship interpolation. They deliberately
    // have no LocalTransform: LocalToWorldSystem must not compute the same pose again.
    internal sealed class DotsWorldItemPresentation : IDisposable
    {
        private sealed class Template
        {
            public Mesh Mesh;
            public Material[] Materials;
            public RenderMeshArray RenderArray;
            public Vector3 Offset;
            public Quaternion Rotation;
            public Vector3 Scale;
        }

        private static readonly Dictionary<ItemDefinition, Template> Templates = new();
        private static int _nextEffectSeed;
        private readonly List<Entity> _entities = new();
        private readonly World _world;
        private readonly Template _template;
        private readonly DotsLootRarityEffect _rarityEffect;

        private DotsWorldItemPresentation(World world, Template template, ItemRarity rarity,
            GameObject rarityEffectPrefab, int effectSeed)
        {
            _world = world;
            _template = template;
            var description = new RenderMeshDescription(ShadowCastingMode.On, true,
                MotionVectorGenerationMode.Camera, LootSilhouetteRenderFeature.ItemLayer,
                uint.MaxValue, LightProbeUsage.BlendProbes);
            var subMeshes = Mathf.Min(template.Mesh.subMeshCount, template.Materials.Length);
            for (ushort sub = 0; sub < subMeshes; sub++)
            {
                var entity = world.EntityManager.CreateEntity(typeof(LocalToWorld));
                RenderMeshUtility.AddComponents(entity, world.EntityManager, description, template.RenderArray,
                    MaterialMeshInfo.FromRenderMeshArrayIndices(sub, 0, sub));
                _entities.Add(entity);
            }
            _rarityEffect = DotsLootRarityEffect.Create(world, rarity, rarityEffectPrefab, effectSeed);
        }

        public static DotsWorldItemPresentation Create(ItemDefinition definition, GameObject rarityEffectPrefab,
            int effectSeed = 0)
        {
            var world = Unity.NetCode.ClientServerBootstrap.ClientWorld;
            if (definition == null || world == null || !world.IsCreated) return null;
            if (!Templates.TryGetValue(definition, out var template))
            {
                if (!LootStressTest.TryGetVisual(definition, out var filter, out var renderer)) return null;
                var matrix = filter.transform.localToWorldMatrix;
                template = new Template
                {
                    Mesh = filter.sharedMesh, Materials = renderer.sharedMaterials,
                    Offset = matrix.GetColumn(3), Rotation = matrix.rotation, Scale = matrix.lossyScale
                };
                template.RenderArray = new RenderMeshArray(template.Materials, new[] { template.Mesh });
                Templates.Add(definition, template);
            }
            var seed = effectSeed != 0 ? effectSeed : ++_nextEffectSeed;
            return new DotsWorldItemPresentation(world, template, definition.Rarity, rarityEffectPrefab, seed);
        }

        public void SetPose(Vector3 rootPosition, Quaternion rootRotation, float size = 1f)
        {
            if (_world == null || !_world.IsCreated) return;
            size = Mathf.Max(0.01f, size);
            var position = rootPosition + rootRotation * (_template.Offset * size);
            var rotation = rootRotation * _template.Rotation;
            var localToWorld = new LocalToWorld
            {
                Value = float4x4.TRS(position, rotation, _template.Scale * size)
            };
            foreach (var entity in _entities) _world.EntityManager.SetComponentData(entity, localToWorld);
            _rarityEffect?.SetPose(rootPosition, size);
        }

        public void Dispose()
        {
            if (_world != null && _world.IsCreated)
                foreach (var entity in _entities)
                    if (_world.EntityManager.Exists(entity)) _world.EntityManager.DestroyEntity(entity);
            _rarityEffect?.Dispose();
            _entities.Clear();
        }
    }
}
