using UnityEngine;

namespace WaveByWave.Enemies
{
    public enum EnemyShipSide : sbyte { Port = -1, Starboard = 1 }

    [DisallowMultipleComponent]
    public sealed class EnemyShipHardpoint : MonoBehaviour
    {
        public EnemyShipSide Side;
    }
}
