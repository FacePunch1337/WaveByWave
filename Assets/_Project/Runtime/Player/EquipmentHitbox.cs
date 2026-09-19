using UnityEngine;

namespace WaveByWave.Player
{
    // A server-only trigger for combat. Remote KCC capsules are intentionally disabled;
    // this collider is never used to move, carry or block a character.
    [DisallowMultipleComponent]
    public sealed class EquipmentHitbox : MonoBehaviour { }
}
