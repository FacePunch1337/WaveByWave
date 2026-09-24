using UnityEngine;

namespace WaveByWave.Ships
{
    [CreateAssetMenu(menuName = "Wave By Wave/Ships/Interior water profile")]
    public sealed class ShipFloodWaterProfile : ScriptableObject
    {
        public Color DeepColor = new(0.015f, 0.19f, 0.23f, 0.88f);
        public Color CrestColor = new(0.22f, 0.65f, 0.68f, 0.8f);
        [Range(0f, 0.1f)] public float RippleHeight = 0.025f;
        [Min(0.1f)] public float RippleFrequency = 3f;
        [Min(0f)] public float RippleSpeed = 1.4f;
        [Range(0f, 1f)] public float Smoothness = 0.85f;
    }
}
