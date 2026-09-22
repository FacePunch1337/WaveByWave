using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WaveByWave.Collision;
using WaveByWave.Enemies;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    public static class ShipFleetCollisionChecks
    {
        [MenuItem("Tools/Wave by Wave/Physics/Check fleet contacts and targeting")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Run fleet checks outside Play Mode.");
            CheckTargeting();
            CheckContacts();
            Debug.Log("[Fleet checks] PASS: registered distant targets and DOTS box contacts.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void CheckTargeting()
        {
            using var world = new World("Fleet targeting check");
            var definition = ScriptableObject.CreateInstance<EnemyShipDefinition>();
            try
            {
                var system = world.GetOrCreateSystemManaged<EnemyShipServerSystem>();
                var entity = world.EntityManager.CreateEntity(typeof(DotsEnemyShipState), typeof(DotsEnemyShipBrain));
                world.EntityManager.SetComponentData(entity, new DotsEnemyShipState
                    { Id = 1, Health = 100, Rotation = quaternion.identity });
                using var targets = new NativeArray<EnemyShipTarget>(new[]
                    { new EnemyShipTarget { Index = 0, Position = new float3(10000, 0, 0) } }, Allocator.TempJob);
                system.Steer(targets, definition);
                var brain = world.EntityManager.GetComponentData<DotsEnemyShipBrain>(entity);
                Require(brain.Target == 0 && math.abs(brain.TargetDistance - 10000) < 0.01f,
                    "An enemy lost the registered player outside the old search radius.");
                using var empty = new NativeArray<EnemyShipTarget>(0, Allocator.TempJob);
                system.Steer(empty, definition);
                Require(world.EntityManager.GetComponentData<DotsEnemyShipBrain>(entity).Target == -1,
                    "An enemy retained a despawned target.");
            }
            finally { Object.DestroyImmediate(definition); }
        }

        private static void CheckContacts()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject player = null;
            GameObject enemy = null;
            try
            {
                var origin = new Vector3(10000, 10000, 10000);
                player = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(player, scene);
                player.transform.position = origin;
                player.transform.localScale = new Vector3(2, 2, 12);
                var solver = player.AddComponent<KinematicShipCollision>();
                enemy = new GameObject("Authored test hull");
                SceneManager.MoveGameObjectToScene(enemy, scene);
                enemy.AddComponent<Rigidbody>().isKinematic = true;
                enemy.AddComponent<BoxCollider>().size = new Vector3(2, 2, 12);
                Require(solver.Initialize(~0, 0.04f), "Player query hull failed to initialize.");
                var poses = new Dictionary<int, RigidTransform>
                {
                    [1] = new RigidTransform(quaternion.identity, origin + Vector3.right * 7)
                };
                var boundsCenter = Vector3.zero;
                var boundsHalfExtents = new Vector3(1, 1, 6);
                Require(solver.SetFleetObstacles(boundsCenter, boundsHalfExtents, poses, 1),
                    "Fleet geometry failed to initialize.");
                var clear = solver.ResolveMotion(origin, Quaternion.identity, Vector3.right * 2, Vector3.zero, 1);
                Require(Mathf.Abs(clear.Position.x - origin.x - 2) < 0.02f,
                    "A five-metre visible gap blocked motion before collider contact.");
                var contact = solver.ResolveMotion(origin, Quaternion.identity, Vector3.right * 10, Vector3.zero, 1);
                Require(contact.Blocked && contact.Position.x - origin.x > 4.8f && contact.Position.x - origin.x <= 5.02f,
                    "Broadside contact did not match the authored collider width.");
                poses[1] = new RigidTransform(quaternion.RotateY(math.PI / 2), origin + Vector3.right * 10);
                Require(solver.SetFleetObstacles(boundsCenter, boundsHalfExtents, poses, 2),
                    "Rotated pose update failed.");
                contact = solver.ResolveMotion(origin, Quaternion.identity, Vector3.right * 10, Vector3.zero, 1);
                Require(contact.Blocked && contact.Position.x - origin.x > 2.8f && contact.Position.x - origin.x <= 3.02f,
                    "Contact ignored the enemy collider rotation.");
                poses[1] = new RigidTransform(quaternion.identity, origin + new Vector3(7, 20, 0));
                Require(solver.SetFleetObstacles(boundsCenter, boundsHalfExtents, poses, 3),
                    "Height update failed.");
                clear = solver.ResolveMotion(origin, Quaternion.identity, Vector3.right * 10, Vector3.zero, 1);
                Require(Mathf.Abs(clear.Position.x - origin.x - 10) < 0.02f, "Vertically separated hulls collided.");
                poses.Clear();
                Require(solver.SetFleetObstacles(boundsCenter, boundsHalfExtents, poses, 4),
                    "Fleet removal failed.");
                clear = solver.ResolveMotion(origin, Quaternion.identity, Vector3.right * 10, Vector3.zero, 1);
                Require(Mathf.Abs(clear.Position.x - origin.x - 10) < 0.02f, "Removed ship left an invisible collider.");
            }
            finally
            {
                if (player != null) Object.DestroyImmediate(player);
                if (enemy != null) Object.DestroyImmediate(enemy);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
