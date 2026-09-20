using UnityEngine;

namespace WaveByWave.Generation
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public sealed class BakedIslandChunk : MonoBehaviour
    {
        [SerializeField, HideInInspector] private Vector3Int minimum;
        [SerializeField, HideInInspector] private Vector3Int maximum;
        public Vector3Int Minimum => minimum;
        public Vector3Int Maximum => maximum;

        public void Configure(Vector3Int min, Vector3Int max)
        { minimum = min; maximum = max; }

        public bool TryGetComponents(out MeshFilter filter, out MeshRenderer renderer, out MeshCollider collider)
        {
            filter = GetComponent<MeshFilter>(); renderer = GetComponent<MeshRenderer>();
            collider = GetComponent<MeshCollider>();
            return filter != null && renderer != null && collider != null;
        }
    }
}
