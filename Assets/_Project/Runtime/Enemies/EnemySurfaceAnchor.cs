using UnityEngine;

namespace WaveByWave.Enemies
{
    // Optional for scene-authored moving platforms without an NGO NetworkObject.
    // Both peers must have the same scene object/key and synchronize its motion.
    [DisallowMultipleComponent]
    public sealed class EnemySurfaceAnchor : MonoBehaviour
    {
        [Tooltip("Unique, identical key on every peer. Empty uses the scene hierarchy path.")]
        public string Key;
    }
}
