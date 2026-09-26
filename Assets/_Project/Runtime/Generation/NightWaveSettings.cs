using System;
using UnityEngine;

namespace WaveByWave.Generation
{
    [Serializable]
    public sealed class NightWaveEnemy
    {
        [Tooltip("Profile that defines the enemy species and combat type.")]
        public WaveEnemyProfile EnemyType;
        [Min(0)] public int Count = 1;
        [Tooltip("Exclusion radius. Enemies never spawn closer to the spawn center than this distance.")]
        [Min(1f)] public float SpawnRadius = 20f;
        [Tooltip("Width of the spawn ring outside the exclusion radius.")]
        [Min(1f)] public float SpawnBandWidth = 20f;
    }

    [Serializable]
    public sealed class NightWaveFragment
    {
        public string Name = "Fragment";
        [Tooltip("The next fragment starts only after every enemy and ship crew member in this list is defeated.")]
        public NightWaveEnemy[] Enemies = Array.Empty<NightWaveEnemy>();
    }

    [Serializable]
    public sealed class NightWaveDefinition
    {
        public string Name = "Night wave";
        [Tooltip("Radius of the safe battle area around the ship's position when this wave starts.")]
        [Min(20f)] public float BattlefieldRadius = 150f;
        public NightWaveFragment[] Fragments = Array.Empty<NightWaveFragment>();
    }

    [CreateAssetMenu(menuName = "Wave By Wave/Voyage/Night waves", fileName = "NightWaveSettings")]
    public sealed class NightWaveSettings : ScriptableObject
    {
        [Min(0f)] public float VictoryDisplayDuration = 5f;
        public NightWaveDefinition[] Waves = Array.Empty<NightWaveDefinition>();
    }
}
