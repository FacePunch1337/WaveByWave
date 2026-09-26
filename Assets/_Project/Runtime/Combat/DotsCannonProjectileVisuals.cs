using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using WaveByWave.Ships;

namespace WaveByWave.Combat
{
    // One client-side renderer for every visible cannon ball. Authoritative motion
    // and impact remain in the DOTS server world; no projectile prefab is instantiated.
    [DefaultExecutionOrder(11000)]
    public sealed class DotsCannonProjectileVisuals : MonoBehaviour
    {
        private readonly struct ShotKey : IEquatable<ShotKey>
        {
            public readonly ulong Owner;
            public readonly uint Revision;
            public readonly bool Enemy;
            public ShotKey(ulong owner, uint revision, bool enemy)
            { Owner = owner; Revision = revision; Enemy = enemy; }
            public bool Equals(ShotKey other) => Owner == other.Owner && Revision == other.Revision && Enemy == other.Enemy;
            public override bool Equals(object obj) => obj is ShotKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Owner, Revision, Enemy);
        }

        private sealed class Shot
        {
            public ShotKey Key;
            public Vector3 Origin, Velocity, Gravity, HitPoint, HitNormal;
            public double Started;
            public float Age, Lifetime, ImpactAge;
            public float WaterEntryAge;
            public bool Impact, Water, Show, MuzzleShown, WaterEntry, WaterEntryShown;
            public Vector3 WaterEntryPoint;
            public GameObject Muzzle, WaterEffect, GroundEffect, WaterEntryEffect;
            public Mesh Mesh;
            public Material Material;
            public Vector3 Scale;
        }

        private static DotsCannonProjectileVisuals _instance;
        private readonly Dictionary<ShotKey, Shot> _shots = new();
        private readonly Dictionary<Material, Material> _materials = new();
        private readonly List<ShotKey> _expired = new();
        private readonly Dictionary<(Mesh, Material), List<Matrix4x4>> _batches = new();
        private readonly Matrix4x4[] _drawMatrices = new Matrix4x4[1023];

        private static DotsCannonProjectileVisuals Instance
        {
            get
            {
                if (_instance != null) return _instance;
                var go = new GameObject("DOTS Cannon Projectile Visuals");
                _instance = go.AddComponent<DotsCannonProjectileVisuals>();
                return _instance;
            }
        }

        public static void Add(ulong owner, uint revision, bool enemy, GameObject prefab,
            Vector3 origin, Vector3 velocity, Vector3 gravity, double started, float lifetime,
            GameObject muzzle, double presentationTime = -1d)
        {
            if (prefab == null) return;
            var meshFilter = prefab.GetComponentInChildren<MeshFilter>(true);
            var meshRenderer = meshFilter != null ? meshFilter.GetComponent<MeshRenderer>() : null;
            if (meshFilter == null || meshFilter.sharedMesh == null || meshRenderer == null ||
                meshRenderer.sharedMaterial == null) return;
            var ownerInstance = Instance;
            var key = new ShotKey(owner, revision, enemy);
            if (ownerInstance._shots.ContainsKey(key)) return;
            var manager = NetworkManager.Singleton;
            var now = presentationTime >= 0d ? presentationTime :
                manager != null && manager.IsListening ? manager.ServerTime.Time : Time.timeAsDouble;
            ownerInstance._shots.Add(key, new Shot
            {
                Key = key, Origin = origin, Velocity = velocity, Gravity = gravity,
                Started = started, Age = Mathf.Max(0f, (float)(now - started)), Lifetime = lifetime,
                Mesh = meshFilter.sharedMesh,
                Material = ownerInstance.RuntimeMaterial(meshRenderer.sharedMaterial),
                Scale = prefab.transform.localScale,
                Muzzle = muzzle
            });
        }

        public static void Impact(ulong owner, uint revision, bool enemy, Vector3 point,
            Vector3 normal, bool water, bool show, double at,
            GameObject waterEffect, GameObject groundEffect)
        {
            if (_instance == null || !_instance._shots.TryGetValue(
                    new ShotKey(owner, revision, enemy), out var shot)) return;
            shot.Impact = true;
            shot.ImpactAge = Mathf.Max(0f, (float)(at - shot.Started));
            shot.HitPoint = point;
            shot.HitNormal = normal;
            shot.Water = water;
            shot.Show = show;
            shot.WaterEffect = waterEffect;
            shot.GroundEffect = groundEffect;
        }

        public static void EnterWater(ulong owner, uint revision, bool enemy, Vector3 point,
            double at, GameObject waterEffect)
        {
            if (_instance == null || !_instance._shots.TryGetValue(
                    new ShotKey(owner, revision, enemy), out var shot)) return;
            shot.WaterEntry = true;
            shot.WaterEntryAge = Mathf.Max(0f, (float)(at - shot.Started));
            shot.WaterEntryPoint = point;
            shot.WaterEntryEffect = waterEffect;
        }

        public static void ClearOwner(ulong owner, bool enemy)
        {
            if (_instance == null) return;
            _instance._expired.Clear();
            foreach (var pair in _instance._shots)
                if (pair.Key.Owner == owner && pair.Key.Enemy == enemy)
                    _instance._expired.Add(pair.Key);
            foreach (var key in _instance._expired) _instance._shots.Remove(key);
            _instance._expired.Clear();
        }

        private Material RuntimeMaterial(Material source)
        {
            if (_materials.TryGetValue(source, out var material)) return material;
            material = new Material(source) { enableInstancing = true,
                name = source.name + " (cannon instances)" };
            _materials.Add(source, material);
            return material;
        }

        private void LateUpdate()
        {
            _expired.Clear();
            foreach (var batch in _batches.Values) batch.Clear();
            foreach (var pair in _shots)
            {
                var shot = pair.Value;
                shot.Age += Time.deltaTime;
                if (!shot.MuzzleShown)
                {
                    shot.MuzzleShown = true;
                    CannonEffects.Muzzle(shot.Origin, shot.Velocity.normalized, shot.Muzzle);
                }
                if (shot.WaterEntry && !shot.WaterEntryShown && shot.Age >= shot.WaterEntryAge)
                {
                    shot.WaterEntryShown = true;
                    CannonEffects.Hit(shot.WaterEntryPoint, Vector3.up, true,
                        shot.WaterEntryEffect, null);
                }
                if (shot.Impact && shot.Age >= shot.ImpactAge)
                {
                    if (shot.Show) CannonEffects.Hit(shot.HitPoint, shot.HitNormal, shot.Water,
                        shot.WaterEffect, shot.GroundEffect);
                    _expired.Add(pair.Key);
                    continue;
                }
                if (shot.Age >= shot.Lifetime + 2f) { _expired.Add(pair.Key); continue; }
                var position = shot.Origin + shot.Velocity * shot.Age +
                    shot.Gravity * (0.5f * shot.Age * shot.Age);
                var velocity = shot.Velocity + shot.Gravity * shot.Age;
                var rotation = velocity.sqrMagnitude > 0.0001f
                    ? Quaternion.LookRotation(velocity.normalized) : Quaternion.identity;
                var key = (shot.Mesh, shot.Material);
                if (!_batches.TryGetValue(key, out var matrices))
                {
                    matrices = new List<Matrix4x4>(128);
                    _batches.Add(key, matrices);
                }
                matrices.Add(Matrix4x4.TRS(position, rotation, shot.Scale));
            }
            foreach (var key in _expired) _shots.Remove(key);
            foreach (var pair in _batches)
            {
                var matrices = pair.Value;
                for (var start = 0; start < matrices.Count; start += _drawMatrices.Length)
                {
                    var count = Mathf.Min(_drawMatrices.Length, matrices.Count - start);
                    matrices.CopyTo(start, _drawMatrices, 0, count);
                    Graphics.DrawMeshInstanced(pair.Key.Item1, 0, pair.Key.Item2, _drawMatrices,
                        count, null, ShadowCastingMode.Off, false);
                }
            }
        }

        private void OnDestroy()
        {
            foreach (var material in _materials.Values) Destroy(material);
            _materials.Clear();
            _shots.Clear();
            if (_instance == this) _instance = null;
        }
    }
}
