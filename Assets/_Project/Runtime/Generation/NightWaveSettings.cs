using System;
using UnityEngine;
using WaveByWave.Enemies;

namespace WaveByWave.Generation
{
    [Serializable]
    public sealed class NightWaveBot
    {
        [Tooltip("Optional NetworkObject + NetworkHealth prefab. Leave empty for a DOTS skeleton of the selected type.")]
        public GameObject Prefab;
        public EnemyCombatType SkeletonType = EnemyCombatType.Melee;
        [Min(0)] public int Count = 10;
    }

    [Serializable]
    public sealed class NightWaveDefinition
    {
        public string Name = "Night wave";
        public NightWaveBot[] Bots = Array.Empty<NightWaveBot>();
        [Min(0)] public int EnemyShips;
        [Min(2f)] public float SpawnRadius = 12f;
        [Tooltip("Radius of the safe battle area around the ship's position when this wave starts.")]
        [Min(20f)] public float BattlefieldRadius = 150f;
    }

    [CreateAssetMenu(menuName = "Wave By Wave/Voyage/Night waves", fileName = "NightWaveSettings")]
    public sealed class NightWaveSettings : ScriptableObject
    {
        [Min(0f)] public float VictoryDisplayDuration = 5f;
        [Tooltip("Crew spawned on enemy ships is not counted in these entries.")]
        public NightWaveDefinition[] Waves = Array.Empty<NightWaveDefinition>();

        [Header("Leaving the battlefield")]
        [Min(0f)] public float BoundaryGraceSeconds = 8f;
        [Tooltip("Seconds between new hull breaches while the ship remains outside the battle area.")]
        [Min(0.5f)] public float BoundaryBreachInterval = 10f;
        [Tooltip("Leak rate of each boundary breach relative to an ordinary hull breach.")]
        [Min(0.1f)] public float BoundaryBreachLeakMultiplier = 1f;
        [Range(0.1f, 1f)] public float BoundaryCheckInterval = 0.25f;

        [Header("Infinite night fog")]
        public Color FogNearColor = new(0.16f, 0.53f, 0.57f, 1f);
        public Color FogFarColor = new(0.025f, 0.12f, 0.18f, 1f);
        [Range(0.005f, 0.3f)] public float FogDensity = 0.065f;
        [Min(2f)] public float FogEdgeWidth = 24f;
        [Min(5f)] public float FogHeight = 70f;
        [Range(0f, 1f)] public float FogNoiseStrength = 0.65f;
        [Range(0.001f, 0.03f)] public float FogNoiseScale = 0.004f;
        [Range(0f, 4f)] public float FogWindSpeed = 0.65f;
        [Range(4, 12)] public int FogSampleCount = 8;
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
