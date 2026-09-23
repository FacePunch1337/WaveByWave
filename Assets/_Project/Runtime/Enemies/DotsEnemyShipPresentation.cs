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
            public float3 FromPosition, TargetPosition;
            public quaternion FromRotation, TargetRotation;
            public float LastSampleAt, BlendStarted, BlendDuration;
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
            var maximumObjects = Mathf.Max(0, _definition.MaximumPhysicsViews);
            var objectCount = 0;
            foreach (var cached in Views.Values)
                if (cached.Object != null) objectCount++;
            // Render time must advance between server simulation ticks on a host.
            var now = Time.unscaledTime;
            using var states = _query.ToComponentDataArray<DotsEnemyShipState>(Allocator.Temp);
            foreach (var state in states)
            {
                if (state.Id == 0 || state.Scene != scene) continue;
                var displayPosition = state.Position;
                var displayRotation = state.Rotation;
                if (state.Health <= 0f && state.DeathAt > 0f)
                {
                    var duration = Mathf.Max(0.1f, _definition.SinkDuration);
                    var t = Mathf.Clamp01((runtime.Now - state.DeathAt) / duration);
                    var eased = t * t * (3f - 2f * t);
                    displayPosition = state.DeathPosition + new float3(0f,
                        -_definition.SinkSpeed * duration * eased, 0f);
                    displayRotation = math.mul(state.DeathRotation,
                        quaternion.RotateZ(math.radians(4f * duration * eased)));
                }
                if (!Views.TryGetValue(state.Id, out var view))
                {
                    view = new View { Position = displayPosition, Rotation = displayRotation,
                        FromPosition = displayPosition, TargetPosition = displayPosition,
                        FromRotation = displayRotation, TargetRotation = displayRotation,
                        LastSampleAt = now, BlendStarted = now,
                        Hit = state.HitRevision, Shot = state.ShotRevision, Impact = state.ImpactRevision };
                    Views.Add(state.Id, view);
                }
                var distanceSq = runtime.CanSimulate ? runtime.DistanceToObserversSquared(state.Position)
                    : _camera != null ? ((Vector3)state.Position - _camera.transform.position).sqrMagnitude : 0;
                var viewDistance = Mathf.Max(_definition.PhysicsViewDistance, _definition.FireRange + 25f);
                // Hysteresis avoids repeatedly creating/destroying a hull at the distance boundary.
                if (view.Object != null) viewDistance += 30f;
                var insideViewDistance = distanceSq <= viewDistance * viewDistance;
                var overCapacity = instanceDistant && view.Object != null && objectCount > maximumObjects;
                var needsObject = !instanceDistant || insideViewDistance &&
                    (view.Object != null || objectCount < maximumObjects);
                if (needsObject && view.Object == null && createBudget > 0)
                {
                    createBudget--;
                    var instance = Object.Instantiate(_definition.ViewPrefab, state.Position, state.Rotation);
                    var component = instance.GetComponent<EnemyShipView>();
                    if (component == null) { Object.Destroy(instance); continue; }
                    component.Initialize(state.Id, _definition);
                    view.Object = component;
                    objectCount++;
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
                else if ((!needsObject || overCapacity) && view.Object != null)
                {
                    Object.Destroy(view.Object.gameObject);
                    view.Object = null;
                    objectCount--;
                }
                view.Seen = _generation;
                if (state.Health <= 0f || math.distancesq(view.Position, displayPosition) > 400f)
                {
                    view.Position = displayPosition;
                    view.Rotation = displayRotation;
                    view.FromPosition = view.TargetPosition = displayPosition;
                    view.FromRotation = view.TargetRotation = displayRotation;
                    view.LastSampleAt = now;
                }
                else if (runtime.CanSimulate)
                {
                    // The host reads an unsmoothed server world. Interpolate each new
                    // simulation sample over its cadence instead of chasing stepwise poses.
                    if (math.distancesq(view.TargetPosition, displayPosition) > 0.00000001f ||
                        math.abs(math.dot(view.TargetRotation.value, displayRotation.value)) < 0.999999f)
                    {
                        view.FromPosition = view.Position;
                        view.FromRotation = view.Rotation;
                        view.TargetPosition = displayPosition;
                        view.TargetRotation = displayRotation;
                        view.BlendDuration = Mathf.Clamp(now - view.LastSampleAt,
                            1f / Mathf.Max(1, _definition.SimulationRate),
                            1f / Mathf.Max(1, _definition.DistantSimulationRate));
                        view.BlendStarted = now;
                        view.LastSampleAt = now;
                    }
                    var progress = Mathf.Clamp01((now - view.BlendStarted) /
                        Mathf.Max(0.001f, view.BlendDuration));
                    view.Position = math.lerp(view.FromPosition, view.TargetPosition, progress);
                    view.Rotation = math.slerp(view.FromRotation, view.TargetRotation, progress);
                }
                else
                {
                    // NetCode already interpolates these ghost fields on clients.
                    view.Position = displayPosition;
                    view.Rotation = displayRotation;
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
