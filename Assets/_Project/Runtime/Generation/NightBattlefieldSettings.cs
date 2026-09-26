using UnityEngine;

namespace WaveByWave.Generation
{
    [CreateAssetMenu(menuName = "Wave By Wave/Voyage/Night battlefield", fileName = "NightBattlefieldSettings")]
    public sealed class NightBattlefieldSettings : ScriptableObject
    {
        [Header("Leaving the battlefield")]
        [Min(0f)] public float BoundaryGraceSeconds = 8f;
        [Tooltip("Seconds between new hull breaches while the ship remains outside the battle area.")]
        [Min(0.5f)] public float BoundaryBreachInterval = 10f;
        [Tooltip("Leak rate of each boundary breach relative to an ordinary hull breach.")]
        [Min(0.1f)] public float BoundaryBreachLeakMultiplier = 1f;
        [Range(0.1f, 1f)] public float BoundaryCheckInterval = 0.25f;

        [Header("Fog transition")]
        [Min(0f), Tooltip("Время плавного появления тумана в начале волны.")]
        public float FogFadeInSeconds = 4f;
        [Min(0f), Tooltip("Время плавного исчезновения тумана после окончания волны. Урон за границей прекращается сразу.")]
        public float FogFadeOutSeconds = 4f;
        [Min(0.1f), Tooltip("За сколько метров при входе в туман исчезает видимая круговая граница.")]
        public float FogImmersionDistance = 3f;

        [Header("Infinite night fog")]
        public Color FogNearColor = new(0.16f, 0.53f, 0.57f, 1f);
        public Color FogFarColor = new(0.025f, 0.12f, 0.18f, 1f);
        [Range(0.005f, 0.3f)] public float FogDensity = 0.065f;
        [Tooltip("Soft transition beyond the battlefield edge; no fog is placed inside the radius.")]
        [Min(2f)] public float FogEdgeWidth = 24f;
        [Min(5f)] public float FogHeight = 70f;
        [Range(0f, 1f)] public float FogNoiseStrength = 0.65f;
        [Range(0.001f, 0.03f)] public float FogNoiseScale = 0.004f;
        [Range(0f, 4f)] public float FogWindSpeed = 0.65f;
        [Range(4, 12)] public int FogSampleCount = 8;
        [Tooltip("Fog depth measured outward from the fixed battlefield boundary, independent of the camera position.")]
        [Min(30f)] public float FogViewDistance = 480f;

#if UNITY_EDITOR
        [Header("Scene View preview (editor only)")]
        [Tooltip("Draw the battle fog in Scene View without entering Play Mode.")]
        public bool PreviewFogInSceneView;
        [Min(0)] public int PreviewWaveIndex;
        public Vector3 PreviewCenter;
#endif
    }
}
