using UnityEngine;

namespace WaveByWave.Items
{
    // Authoring data only. This prefab is never instantiated for world loot.
    public sealed class LootRarityGlow : MonoBehaviour
    {
        [SerializeField, Tooltip("DOTS-материал эффекта. Высота/ширина луча, ореол, размер круглых искр и яркость настраиваются в материале. Изменения и замена материала применяются к существующему луту во время игры. RGB задаётся редкостью, альфа берётся из материала.")]
        private Material dotsMaterial;
        public Material DotsMaterial => dotsMaterial;
    }
}
