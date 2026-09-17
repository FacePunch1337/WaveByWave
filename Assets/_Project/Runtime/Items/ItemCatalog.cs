using System;
using System.Collections.Generic;
using UnityEngine;

namespace WaveByWave.Items
{
    [CreateAssetMenu(menuName = "Wave by Wave/Items/Item Catalog", fileName = "ItemCatalog")]
    public sealed class ItemCatalog : ScriptableObject
    {
        [SerializeField] private List<ItemDefinition> items = new();
        private Dictionary<string, ItemDefinition> _lookup;

        public IReadOnlyList<ItemDefinition> Items => items;

        public bool TryGet(string id, out ItemDefinition definition)
        {
            _lookup ??= BuildLookup();
            return _lookup.TryGetValue(id, out definition);
        }

        private Dictionary<string, ItemDefinition> BuildLookup()
        {
            var result = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.Id))
                    result[item.Id] = item;
            }

            return result;
        }

        private void OnValidate() => _lookup = null;
    }
}
