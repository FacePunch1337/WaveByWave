using UnityEngine;

namespace WaveByWave.Items
{
    public enum ItemGripHand : byte
    {
        Right,
        Left
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("Wave by Wave/Items/Item Hand Grip Point")]
    public sealed class ItemHandGripPoint : MonoBehaviour
    {
        [SerializeField, Tooltip("Рука, ладонь которой должна точно совпасть с этой точкой.")]
        private ItemGripHand hand;

        public ItemGripHand Hand => hand;

        private void OnDrawGizmos()
        {
            const float size = 0.055f;
            Gizmos.color = hand == ItemGripHand.Right
                ? new Color(0.2f, 0.65f, 1f, 0.95f)
                : new Color(1f, 0.45f, 0.2f, 0.95f);
            Gizmos.DrawWireSphere(transform.position, size);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * (size * 2.5f));
            Gizmos.DrawLine(transform.position, transform.position + transform.up * (size * 1.75f));
        }
    }
}
