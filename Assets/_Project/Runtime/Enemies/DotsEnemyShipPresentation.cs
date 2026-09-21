using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace WaveByWave.Enemies
{
    public static class DotsEnemyShipPresentation
    {
        private sealed class View
        {
            public EnemyShipView Object;
            public float3 Position;
            public quaternion Rotation;
            public uint Hit;
            public uint Shot;
            public uint Impact;
            public int Seen;
        }

        private static readonly Dictionary<int, View> Views = new();
        private static readonly List<int> Removed = new();
        private static World _source;
        private static EntityQuery _query;
        private static EnemyShipDefinition _definition;
        private static int _generation;
        private static int _scene;

        internal static void Update(DotsEnemyShipRuntime runtime)
        {
            if (runtime.Definition == null || runtime.Definition.ViewPrefab == null) return;
            var source = runtime.CanSimulate && runtime.ServerWorld != null && runtime.ServerWorld.IsCreated
                ? runtime.ServerWorld : ClientServerBootstrap.ClientWorld;
            if (source == null || !source.IsCreated) return;
            if (_source != source || _definition != runtime.Definition)
            {
                Dispose();
                _source = source;
                _definition = runtime.Definition;
                _query = source.EntityManager.CreateEntityQuery(typeof(DotsEnemyShipState));
                _scene = DotsEnemyRuntime.SceneKey(SceneManager.GetActiveScene().name);
            }
            var scene = DotsEnemyRuntime.SceneKey(SceneManager.GetActiveScene().name);
            if (_scene != scene) { Clear(); _scene = scene; }
            _generation++;
            var blend = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            using var states = _query.ToComponentDataArray<DotsEnemyShipState>(Allocator.Temp);
            foreach (var state in states)
            {
                if (state.Id == 0 || state.Scene != scene) continue;
                if (!Views.TryGetValue(state.Id, out var view))
                {
                    var instance = Object.Instantiate(_definition.ViewPrefab, state.Position, state.Rotation);
                    var component = instance.GetComponent<EnemyShipView>();
                    if (component == null)
                    {
                        Debug.LogError("Enemy ship view prefab requires EnemyShipView.", instance);
                        Object.Destroy(instance);
                        continue;
                    }
                    component.Initialize(state.Id, _definition);
                    view = new View { Object = component, Position = state.Position, Rotation = state.Rotation,
                        Hit = state.HitRevision, Shot = state.ShotRevision, Impact = state.ImpactRevision };
                    Views.Add(state.Id, view);
                    runtime.RegisterView(state.Id, component);
                    if (runtime.CanSimulate) runtime.EnsureCrew(state.Id, component);
                    var age = runtime.Now - state.ShotStarted;
                    if (state.ShotRevision != 0 && age >= 0 && age <= _definition.ProjectileLifetime)
                    {
                        component.PlayShot(state.ShotRevision, state.ShotOrigin, state.ShotVelocity, state.ShotStarted);
                        if (state.ImpactRevision != 0 && state.ImpactShotRevision == state.ShotRevision)
                            component.PlayImpact(state.ImpactShotRevision, state.ImpactPoint, state.ImpactNormal,
                                (state.ImpactFlags & 1) != 0, (state.ImpactFlags & 2) != 0, state.ImpactAt);
                    }
                }
                view.Seen = _generation;
                if (view.Object == null) continue;
                if (runtime.CanSimulate && state.Health > 0) runtime.EnsureCrew(state.Id, view.Object);
                if (math.distancesq(view.Position, state.Position) > 400f)
                {
                    view.Position = state.Position;
                    view.Rotation = state.Rotation;
                }
                else
                {
                    view.Position = math.lerp(view.Position, state.Position, blend);
                    view.Rotation = math.slerp(view.Rotation, state.Rotation, blend);
                }
                view.Object.SetPose(view.Position, view.Rotation);
                if (view.Hit != state.HitRevision)
                {
                    view.Hit = state.HitRevision;
                    view.Object.SetHealth(state.Health, state.HitRevision);
                }
                else view.Object.SetHealth(state.Health, 0);
                if (view.Shot != state.ShotRevision)
                {
                    view.Shot = state.ShotRevision;
                    view.Object.PlayShot(state.ShotRevision, state.ShotOrigin, state.ShotVelocity, state.ShotStarted);
                }
                if (view.Impact != state.ImpactRevision)
                {
                    view.Impact = state.ImpactRevision;
                    view.Object.PlayImpact(state.ImpactShotRevision, state.ImpactPoint, state.ImpactNormal,
                        (state.ImpactFlags & 1) != 0, (state.ImpactFlags & 2) != 0, state.ImpactAt);
                }
            }
            Removed.Clear();
            foreach (var pair in Views)
                if (pair.Value.Seen != _generation || pair.Value.Object == null)
                {
                    if (pair.Value.Object != null) Object.Destroy(pair.Value.Object.gameObject);
                    Removed.Add(pair.Key);
                }
            foreach (var id in Removed) Views.Remove(id);
        }

        internal static void Clear()
        {
            foreach (var view in Views.Values)
                if (view.Object != null) Object.Destroy(view.Object.gameObject);
            Views.Clear();
        }

        internal static void Dispose()
        {
            Clear();
            if (_source != null && _source.IsCreated) _query.Dispose();
            _source = null;
            _definition = null;
        }
    }
}
