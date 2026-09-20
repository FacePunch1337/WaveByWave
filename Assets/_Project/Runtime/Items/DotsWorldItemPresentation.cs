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
    // Single loose-item presentation for the same Entities Graphics path used by mass loot.
    // Gameplay authority and replication live in LootStressTest; this class owns only render entities.
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
        private readonly List<Entity> _entities = new();
        private readonly World _world;
        private readonly Template _template;

        private DotsWorldItemPresentation(World world, Template template)
        {
            _world = world;
            _template = template;
            var description = new RenderMeshDescription(ShadowCastingMode.On, true,
                MotionVectorGenerationMode.Camera, 0, uint.MaxValue, LightProbeUsage.BlendProbes);
            var subMeshes = Mathf.Min(template.Mesh.subMeshCount, template.Materials.Length);
            for (ushort sub = 0; sub < subMeshes; sub++)
            {
                var entity = world.EntityManager.CreateEntity(typeof(LocalTransform));
                RenderMeshUtility.AddComponents(entity, world.EntityManager, description, template.RenderArray,
                    MaterialMeshInfo.FromRenderMeshArrayIndices(sub, 0, sub));
                if (!world.EntityManager.HasComponent<PostTransformMatrix>(entity))
                    world.EntityManager.AddComponentData(entity, new PostTransformMatrix());
                _entities.Add(entity);
            }
        }

        public static DotsWorldItemPresentation Create(ItemDefinition definition)
        {
            var world = Unity.NetCode.ClientServerBootstrap.ClientWorld;
            if (definition == null || world == null || !world.IsCreated) return null;
            if (!Templates.TryGetValue(definition, out var template))
            {
                if (!LootStressTest.TryGetVisual(definition, out var filter, out var renderer)) return null;
                var matrix = filter.transform.localToWorldMatrix;
                template = new Template
                {
                    Mesh = filter.sharedMesh,
                    Materials = renderer.sharedMaterials,
                    Offset = matrix.GetColumn(3),
                    Rotation = matrix.rotation,
                    Scale = matrix.lossyScale
                };
                template.RenderArray = new RenderMeshArray(template.Materials, new[] { template.Mesh });
                Templates.Add(definition, template);
            }
            return new DotsWorldItemPresentation(world, template);
        }

        public void SetPose(Vector3 rootPosition, Quaternion rootRotation)
        {
            if (_world == null || !_world.IsCreated) return;
            var position = rootPosition + rootRotation * _template.Offset;
            var rotation = rootRotation * _template.Rotation;
            var local = LocalTransform.FromPositionRotationScale(position, rotation, 1f);
            var localToWorld = new LocalToWorld
            {
                Value = float4x4.TRS(position, rotation, _template.Scale)
            };
            var post = new PostTransformMatrix { Value = float4x4.Scale(_template.Scale) };
            foreach (var entity in _entities)
            {
                if (!_world.EntityManager.Exists(entity)) continue;
                _world.EntityManager.SetComponentData(entity, local);
                if (_world.EntityManager.HasComponent<LocalToWorld>(entity))
                    _world.EntityManager.SetComponentData(entity, localToWorld);
                _world.EntityManager.SetComponentData(entity, post);
            }
        }

        public void Dispose()
        {
            if (_world != null && _world.IsCreated)
                foreach (var entity in _entities)
                    if (_world.EntityManager.Exists(entity)) _world.EntityManager.DestroyEntity(entity);
            _entities.Clear();
        }
    }
}
