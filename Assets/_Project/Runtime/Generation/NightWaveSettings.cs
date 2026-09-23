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
    }

    [CreateAssetMenu(menuName = "Wave By Wave/Voyage/Night waves", fileName = "NightWaveSettings")]
    public sealed class NightWaveSettings : ScriptableObject
    {
        [Min(0f)] public float VictoryDisplayDuration = 5f;
        [Tooltip("Crew spawned on enemy ships is not counted in these entries.")]
        public NightWaveDefinition[] Waves = Array.Empty<NightWaveDefinition>();
    }
}
