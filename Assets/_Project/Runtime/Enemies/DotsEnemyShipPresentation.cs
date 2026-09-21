using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Profiling;
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
            public Matrix4x4 Frame;
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
        private static EnemyShipFleetRenderer _fleet;
        private static Camera _camera;
        private static readonly ProfilerMarker PresentationMarker = new("WaveByWave.Fleet.Presentation");

        internal static void Update(DotsEnemyShipRuntime runtime)
        {
            using var scope = PresentationMarker.Auto();
            if (runtime.Definition == null || runtime.Definition.ViewPrefab == null) return;
            var source = runtime.CanSimulate && runtime.ServerWorld != null && runtime.ServerWorld.IsCreated
                ? runtime.ServerWorld : ClientServerBootstrap.ClientWorld;
            if (source == null || !source.IsCreated) return;
            if (_source != source || _definition != runtime.Definition)
            {
                Dispose();
                _source = source;
                _definition = runtime.Definition;
                _fleet = new EnemyShipFleetRenderer(_definition.ViewPrefab);
                _query = source.EntityManager.CreateEntityQuery(typeof(DotsEnemyShipState));
                _scene = DotsEnemyRuntime.SceneKey(SceneManager.GetActiveScene().name);
            }
            var scene = DotsEnemyRuntime.SceneKey(SceneManager.GetActiveScene().name);
            if (_scene != scene) { Clear(); _scene = scene; }
            _generation++;
            if (_camera == null || !_camera.isActiveAndEnabled) _camera = Camera.main;
            var instanceDistant = _definition.InstanceDistantShips && _fleet.Available &&
                (_camera != null || runtime.CanSimulate);
            _fleet.Begin(_camera);
            var createBudget = Mathf.Max(1, _definition.ViewCreationsPerFrame);
            var blend = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            using var states = _query.ToComponentDataArray<DotsEnemyShipState>(Allocator.Temp);
            foreach (var state in states)
            {
                if (state.Id == 0 || state.Scene != scene) continue;
                if (!Views.TryGetValue(state.Id, out var view))
                {
                    view = new View { Position = state.Position, Rotation = state.Rotation,
                        Hit = state.HitRevision, Shot = state.ShotRevision, Impact = state.ImpactRevision };
                    Views.Add(state.Id, view);
                }
                var distanceSq = runtime.CanSimulate ? runtime.DistanceToObserversSquared(state.Position)
                    : _camera != null ? ((Vector3)state.Position - _camera.transform.position).sqrMagnitude : 0;
                var viewDistance = Mathf.Max(_definition.PhysicsViewDistance, _definition.FireRange + 25f);
                // Hysteresis avoids repeatedly creating/destroying a hull at the distance boundary.
                if (view.Object != null) viewDistance += 30f;
                var needsObject = !instanceDistant || distanceSq <= viewDistance * viewDistance;
                if (needsObject && view.Object == null && createBudget > 0)
                {
                    createBudget--;
                    var instance = Object.Instantiate(_definition.ViewPrefab, state.Position, state.Rotation);
                    var component = instance.GetComponent<EnemyShipView>();
                    if (component == null) { Object.Destroy(instance); continue; }
                    component.Initialize(state.Id, _definition);
                    view.Object = component;
                    runtime.RegisterView(state.Id, component);
                    var age = runtime.Now - state.ShotStarted;
                    if (state.ShotRevision != 0 && age >= 0 && age <= _definition.ProjectileLifetime)
                    {
                        component.PlayShot(state.ShotRevision, state.ShotOrigin, state.ShotVelocity, state.ShotStarted);
                        if (state.ImpactRevision != 0 && state.ImpactShotRevision == state.ShotRevision)
                            component.PlayImpact(state.ImpactShotRevision, state.ImpactPoint, state.ImpactNormal,
                                (state.ImpactFlags & 1) != 0, (state.ImpactFlags & 2) != 0, state.ImpactAt);
                    }
                }
                else if (!needsObject && view.Object != null)
                {
                    Object.Destroy(view.Object.gameObject);
                    view.Object = null;
                }
                view.Seen = _generation;
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
                view.Frame = Matrix4x4.TRS(view.Position, view.Rotation, _definition.ViewPrefab.transform.localScale);
                if (view.Object == null)
                {
                    if (_fleet.Available) _fleet.Add(view.Frame);
                    continue;
                }
                view.Object.SetSimulationPose(state.Position, state.Rotation);
                view.Object.SetPose(view.Position, view.Rotation);
                if (view.Hit != state.HitRevision)
                {
                    view.Hit = state.HitRevision;
                    view.Object.SetHealth(state.Health, state.HitRevision);
                }
                else view.Object.SetHealth(state.Health, 0);
                view.Object.UpdateDamageFlash();
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
                if (pair.Value.Seen != _generation)
                {
                    if (pair.Value.Object != null) Object.Destroy(pair.Value.Object.gameObject);
                    Removed.Add(pair.Key);
                }
            foreach (var id in Removed) Views.Remove(id);
            _fleet.Draw();
        }

        public static EnemyShipView GetView(int id) => Views.TryGetValue(id, out var view) ? view.Object : null;

        public static bool TryGetFrame(int id, out Matrix4x4 frame)
        {
            frame = Matrix4x4.identity;
            if (!Views.TryGetValue(id, out var view) || _definition == null) return false;
            frame = view.Frame;
            return true;
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
            _fleet?.Dispose();
            _fleet = null;
            _camera = null;
        }
    }
}
