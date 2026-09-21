using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace WaveByWave.Enemies
{
    // Persistent broad phase shared by spawning and swept fleet contacts. Hull-enclosing
    // circles are conservative even while a ship turns, pitches or rolls between updates.
    internal sealed class EnemyShipSpatialIndex
    {
        private readonly Dictionary<int2, List<int>> _cells = new();
        private readonly Stack<List<int>> _pool = new();
        private readonly Dictionary<int, float2> _positions = new();
        private float _diameter;

        public EnemyShipSpatialIndex(float radius = 1f) => _diameter = math.max(1f, radius * 2f);

        public void Rebuild(NativeArray<DotsEnemyShipState> states, float radius)
        {
            Clear();
            _diameter = math.max(1f, radius * 2f);
            foreach (var state in states)
                if (state.Health > 0) Set(state.Id, state.Position.xz);
        }

        public void Set(int id, float2 position)
        {
            var cell = (int2)math.floor(position / _diameter);
            if (_positions.TryGetValue(id, out var old))
            {
                var oldCell = (int2)math.floor(old / _diameter);
                if (math.all(cell == oldCell)) { _positions[id] = position; return; }
                _cells[oldCell].Remove(id);
            }
            _positions[id] = position;
            if (!_cells.TryGetValue(cell, out var list))
            {
                list = _pool.Count > 0 ? _pool.Pop() : new List<int>(8);
                _cells.Add(cell, list);
            }
            list.Add(id);
        }

        public bool IsClear(float2 position)
        {
            var cell = (int2)math.floor(position / _diameter);
            for (var z = -1; z <= 1; z++)
            for (var x = -1; x <= 1; x++)
                if (_cells.TryGetValue(cell + new int2(x, z), out var list))
                    foreach (var id in list)
                        if (math.distancesq(position, _positions[id]) < _diameter * _diameter) return false;
            return true;
        }

        public Vector3 LimitMotion(int self, float3 position, Vector3 displacement, float selfRadius = -1f)
        {
            var move = new float2(displacement.x, displacement.z);
            var length = math.length(move);
            if (length < 0.00001f) return displacement;
            var direction = move / length;
            var from = position.xz;
            var clearance = selfRadius < 0 ? _diameter : _diameter * 0.5f + selfRadius;
            var min = (int2)math.floor((math.min(from, from + move) - clearance) / _diameter);
            var max = (int2)math.floor((math.max(from, from + move) + clearance) / _diameter);
            var accepted = length;
            for (var z = min.y; z <= max.y; z++)
            for (var x = min.x; x <= max.x; x++)
            {
                if (!_cells.TryGetValue(new int2(x, z), out var list)) continue;
                foreach (var id in list)
                {
                    if (id == self) continue;
                    var to = _positions[id] - from;
                    var along = math.dot(to, direction);
                    if (along <= 0) continue; // Allow already touching hulls to separate.
                    var sideSq = math.lengthsq(to) - along * along;
                    var radiusSq = clearance * clearance;
                    if (sideSq >= radiusSq) continue;
                    var contact = along - math.sqrt(math.max(0, radiusSq - sideSq));
                    accepted = math.min(accepted, math.max(0, contact - 0.01f));
                }
            }
            return displacement * (accepted / length);
        }

        public void Clear()
        {
            foreach (var list in _cells.Values) { list.Clear(); _pool.Push(list); }
            _cells.Clear();
            _positions.Clear();
        }
    }
}
