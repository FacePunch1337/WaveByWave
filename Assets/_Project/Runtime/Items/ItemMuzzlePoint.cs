using UnityEngine;

namespace WaveByWave.Items
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Wave by Wave/Items/Item Muzzle Point")]
    public sealed class ItemMuzzlePoint : MonoBehaviour
    {
        private void OnDrawGizmos()
        {
            const float size = 0.045f;
            Gizmos.color = new Color(1f, 0.72f, 0.08f, 0.95f);
            Gizmos.DrawWireSphere(transform.position, size);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * (size * 4f));
            Gizmos.DrawLine(transform.position + transform.forward * (size * 4f),
                transform.position + transform.forward * (size * 2.7f) + transform.up * size);
            Gizmos.DrawLine(transform.position + transform.forward * (size * 4f),
                transform.position + transform.forward * (size * 2.7f) - transform.up * size);
        }
    }
}
