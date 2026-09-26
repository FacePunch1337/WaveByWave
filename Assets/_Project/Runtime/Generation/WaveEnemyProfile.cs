using UnityEngine;
using WaveByWave.Enemies;

namespace WaveByWave.Generation
{
    public enum WaveEnemySpawnKind : byte
    {
        Character,
        Ship
    }

    [CreateAssetMenu(menuName = "Wave By Wave/Voyage/Wave enemy profile", fileName = "WaveEnemy_")]
    public sealed class WaveEnemyProfile : ScriptableObject
    {
        public string DisplayName = "Enemy";
        [Tooltip("Characters use the DOTS enemy runtime. Ships use the shared enemy ship definition and spawn their configured crew.")]
        public WaveEnemySpawnKind SpawnKind;
        [InspectorName("Enemy Species"), Tooltip("Character species. Ignored by ship profiles.")]
        public EnemyKind Species = EnemyKind.Skeleton;
        [Tooltip("Stored in this profile so wave fragments only select an Enemy Type.")]
        public EnemyCombatType CombatType = EnemyCombatType.Melee;
        [HideInInspector] public int InspectorOrder;

        public bool IsShip => SpawnKind == WaveEnemySpawnKind.Ship;

        public int CountedBotsPerSpawn(EnemyShipDefinition shipDefinition)
            => IsShip ? Mathf.Max(0, shipDefinition != null ? shipDefinition.CrewCount : 0) : 1;
    }
}
