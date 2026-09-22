using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace WaveByWave.Enemies
{
    // Broad phase only. Cells/circles never stop motion; oriented DOTS boxes decide contact.
    internal sealed class EnemyShipSpatialIndex
    {
        private readonly Dictionary<int2, List<int>> _cells = new();
        private readonly Stack<List<int>> _pool = new();
        private struct Entry
        {
            public float2 Position;
            public int2 Cell;
            public int Index;
        }

        private readonly Dictionary<int, Entry> _entries = new();
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
            if (_entries.TryGetValue(id, out var old))
            {
                if (math.all(cell == old.Cell))
                {
                    old.Position = position;
                    _entries[id] = old;
                    return;
                }
                RemoveFromCell(old);
            }
            if (!_cells.TryGetValue(cell, out var list))
            {
                list = _pool.Count > 0 ? _pool.Pop() : new List<int>(8);
                _cells.Add(cell, list);
            }
            _entries[id] = new Entry { Position = position, Cell = cell, Index = list.Count };
            list.Add(id);
        }

        public void Remove(int id)
        {
            if (!_entries.TryGetValue(id, out var entry)) return;
            RemoveFromCell(entry);
            _entries.Remove(id);
        }

        private void RemoveFromCell(Entry entry)
        {
            var list = _cells[entry.Cell];
            var last = list.Count - 1;
            if (entry.Index != last)
            {
                var movedId = list[last];
                list[entry.Index] = movedId;
                var moved = _entries[movedId];
                moved.Index = entry.Index;
                _entries[movedId] = moved;
            }
            list.RemoveAt(last);
            if (list.Count != 0) return;
            _cells.Remove(entry.Cell);
            _pool.Push(list);
        }

        public bool IsClear(float2 position)
        {
            var cell = (int2)math.floor(position / _diameter);
            for (var z = -1; z <= 1; z++)
            for (var x = -1; x <= 1; x++)
                if (_cells.TryGetValue(cell + new int2(x, z), out var list))
                    foreach (var id in list)
                        if (math.distancesq(position, _entries[id].Position) < _diameter * _diameter) return false;
            return true;
        }

        public void CollectCandidates(int self, float3 position, Vector3 displacement, List<int> result)
        {
            result.Clear();
            var move = new float2(displacement.x, displacement.z);
            var from = position.xz;
            var clearance = _diameter;
            var min = (int2)math.floor((math.min(from, from + move) - clearance) / _diameter);
            var max = (int2)math.floor((math.max(from, from + move) + clearance) / _diameter);
            for (var z = min.y; z <= max.y; z++)
            for (var x = min.x; x <= max.x; x++)
            {
                if (!_cells.TryGetValue(new int2(x, z), out var list)) continue;
                foreach (var id in list)
                {
                    if (id != self) result.Add(id);
                }
            }
        }

        public void CollectWithinRadius(float2 position, float radius, List<int> result)
        {
            result.Clear();
            var radiusSq = radius * radius;
            var min = (int2)math.floor((position - radius) / _diameter);
            var max = (int2)math.floor((position + radius) / _diameter);
            for (var z = min.y; z <= max.y; z++)
            for (var x = min.x; x <= max.x; x++)
            {
                if (!_cells.TryGetValue(new int2(x, z), out var list)) continue;
                foreach (var id in list)
                    if (math.distancesq(position, _entries[id].Position) <= radiusSq)
                        result.Add(id);
            }
        }

        public void Clear()
        {
            foreach (var list in _cells.Values) { list.Clear(); _pool.Push(list); }
            _cells.Clear();
            _entries.Clear();
        }
    }
}
