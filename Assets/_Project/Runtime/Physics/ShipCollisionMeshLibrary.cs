using System;
using System.Collections.Generic;
using UnityEngine;

namespace WaveByWave.Collision
{
    // Imported models need not have Read/Write enabled in player builds. The editor
    // baker copies their collision data here, without changing the model importers.
    [PreferBinarySerialization]
    public sealed class ShipCollisionMeshLibrary : ScriptableObject
    {
        public const string ResourceName = "ShipCollisionMeshLibrary";

        [Serializable]
        public sealed class Entry
        {
            public Mesh source;
            public Vector3[] vertices;
            public int[] triangles;
        }

        [SerializeField] private Entry[] entries = Array.Empty<Entry>();
        private Dictionary<Mesh, Entry> _lookup;

        public void SetEntries(Entry[] data)
        {
            entries = data;
            _lookup = null;
        }

        public bool TryGet(Mesh source, out Entry entry)
        {
            if (_lookup == null)
            {
                _lookup = new Dictionary<Mesh, Entry>(entries.Length);
                foreach (var item in entries)
                    if (item.source != null)
                        _lookup[item.source] = item;
            }

            return _lookup.TryGetValue(source, out entry);
        }
    }
}
