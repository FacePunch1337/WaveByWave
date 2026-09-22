using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;
using Collider = Unity.Physics.Collider;

namespace WaveByWave.Collision
{
    // An immutable query copy of the authored colliders, shared by every DOTS vessel.
    // Child blobs are copied into the compound and released immediately.
    internal static class AuthoredShipHull
    {
        public static BlobAssetReference<Collider> Create(GameObject prefab, int layers)
        {
            var children = new List<CompoundCollider.ColliderBlobInstance>();
            var root = prefab.transform;
            var inverse = Quaternion.Inverse(root.rotation);
            var library = Resources.Load<ShipCollisionMeshLibrary>(ShipCollisionMeshLibrary.ResourceName);
            try
            {
                foreach (var source in prefab.GetComponentsInChildren<UnityEngine.Collider>())
                {
                    if (!source.enabled || source.isTrigger || (layers & (1 << source.gameObject.layer)) == 0) continue;
                    var shape = CreateShape(source, library);
                    if (!shape.IsCreated) continue;
                    children.Add(new CompoundCollider.ColliderBlobInstance
                    {
                        Collider = shape,
                        CompoundFromChild = new RigidTransform(inverse * source.transform.rotation,
                            inverse * (source.transform.position - root.position))
                    });
                }
                if (children.Count == 0) throw new InvalidOperationException($"No solid ship colliders on {prefab.name}.");
                using var array = new NativeArray<CompoundCollider.ColliderBlobInstance>(children.ToArray(), Allocator.Temp);
                return CompoundCollider.Create(array);
            }
            finally { foreach (var child in children) if (child.Collider.IsCreated) child.Collider.Dispose(); }
        }

        private static BlobAssetReference<Collider> CreateShape(UnityEngine.Collider source, ShipCollisionMeshLibrary library)
        {
            var signed = source.transform.lossyScale;
            var scale = math.abs((float3)signed);
            switch (source)
            {
                case UnityEngine.BoxCollider box:
                    return Unity.Physics.BoxCollider.Create(new BoxGeometry { Center = (float3)box.center * (float3)signed,
                        Size = (float3)box.size * scale, Orientation = quaternion.identity, BevelRadius = 0 });
                case UnityEngine.SphereCollider sphere:
                    return Unity.Physics.SphereCollider.Create(new SphereGeometry { Center = (float3)sphere.center * (float3)signed,
                        Radius = sphere.radius * math.cmax(scale) });
                case UnityEngine.CapsuleCollider capsule:
                    var axis = capsule.direction == 0 ? math.right() : capsule.direction == 1 ? math.up() : math.forward();
                    var radius = capsule.radius * (capsule.direction == 0 ? math.max(scale.y, scale.z)
                        : capsule.direction == 1 ? math.max(scale.x, scale.z) : math.max(scale.x, scale.y));
                    var offset = axis * math.max(0, capsule.height * scale[capsule.direction] * 0.5f - radius);
                    var center = (float3)capsule.center * (float3)signed;
                    return Unity.Physics.CapsuleCollider.Create(new CapsuleGeometry { Vertex0 = center - offset,
                        Vertex1 = center + offset, Radius = radius });
                case UnityEngine.MeshCollider mesh when mesh.sharedMesh != null:
                    ShipCollisionMeshLibrary.Entry data = null;
                    if (library == null || !library.TryGet(mesh.sharedMesh, out data))
                    {
                        if (!Application.isEditor && !mesh.sharedMesh.isReadable)
                            throw new InvalidOperationException($"Bake collision mesh {mesh.sharedMesh.name} before building.");
                        data = new ShipCollisionMeshLibrary.Entry { vertices = mesh.sharedMesh.vertices, triangles = mesh.sharedMesh.triangles };
                    }
                    var vertices = new NativeArray<float3>(data.vertices.Length, Allocator.Temp);
                    try
                    {
                        for (var i = 0; i < vertices.Length; i++) vertices[i] = (float3)data.vertices[i] * (float3)signed;
                        if (mesh.convex)
                        {
                            var parameters = ConvexHullGenerationParameters.Default;
                            parameters.BevelRadius = 0;
                            parameters.SimplificationTolerance = 0;
                            return ConvexCollider.Create(vertices, parameters);
                        }
                        var indices = new NativeArray<int3>(data.triangles.Length / 3, Allocator.Temp);
                        try
                        {
                            for (var i = 0; i < indices.Length; i++) indices[i] = new int3(data.triangles[3*i], data.triangles[3*i+1], data.triangles[3*i+2]);
                            return Unity.Physics.MeshCollider.Create(vertices, indices);
                        }
                        finally { indices.Dispose(); }
                    }
                    finally { vertices.Dispose(); }
                default: throw new NotSupportedException($"Unsupported ship collider: {source.GetType().Name}");
            }
        }
    }
}
