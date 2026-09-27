using System.Collections.Generic;
using UnityEngine;
using WaveByWave.Items;

namespace WaveByWave.Generation
{
    [CreateAssetMenu(menuName = "Wave by Wave/World/Floating Item Pool", fileName = "FloatingItemPool")]
    public sealed class FloatingItemPool : ScriptableObject
    {
        [SerializeField, Tooltip("All floating items and their relative selection weights. Day rarity rules in Ocean Generation Settings filter this pool at runtime.")]
        private List<WeightedItemEntry> items = new();

        public List<WeightedItemEntry> Items => items;
    }
}
