using UnityEngine;

namespace WaveByWave.Enemies
{
    [DisallowMultipleComponent]
    public sealed class EnemyDeckNavigation : MonoBehaviour
    {
        public EnemyDeckNavigationData Data;
        [Tooltip("Empty uses enabled solid MeshColliders and BoxColliders fixed to this ship. Triggers and independently moving bodies are excluded.")]
        public Collider[] Sources = System.Array.Empty<Collider>();
        [Range(0.1f, 1f)] public float CellSize = 0.2f;
        [Min(0.01f)] public float AgentRadius = 0.1f;
        [Min(0.2f)] public float AgentHeight = 1.7f;
        [Range(0, 70)] public float MaximumSlope = 48f;
        [Range(0.05f, 1f)] public float StepHeight = 0.45f;
        [Range(0.05f, 10f)] public float MaximumDrop = 2f;
        public bool ShowNavigation = true;

        private void OnDrawGizmosSelected()
        {
            if (!ShowNavigation || Data == null || !Data.IsBaked) return;
            var previous = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            var stride = Mathf.Max(1, Data.Nodes.Length / 12000);
            for (var i = 0; i < Data.Nodes.Length; i += stride)
            {
                var node = Data.Nodes[i];
                Gizmos.color = node.Boundary ? new Color(1, 0.65f, 0.1f, 0.8f) : new Color(0.1f, 1, 0.5f, 0.6f);
                Gizmos.DrawWireCube(node.Position, new Vector3(Data.CellSize * 0.85f, 0.015f, Data.CellSize * 0.85f));
            }
            Gizmos.matrix = previous;
        }
    }
}
