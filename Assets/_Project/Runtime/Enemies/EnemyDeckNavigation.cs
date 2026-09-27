using UnityEngine;

namespace WaveByWave.Enemies
{
    [DisallowMultipleComponent]
    public sealed class EnemyDeckNavigation : MonoBehaviour
    {
        public EnemyDeckNavigationData Data;
        [Tooltip("Optional maps for larger enemies. The smallest map that fits the enemy is selected; all instances of this ship share its routing data.")]
        public EnemyDeckNavigationData[] AdditionalAgentMaps = System.Array.Empty<EnemyDeckNavigationData>();
        [Tooltip("Empty uses enabled solid MeshColliders and BoxColliders fixed to this ship. Triggers and independently moving bodies are excluded.")]
        public Collider[] Sources = System.Array.Empty<Collider>();
        [Range(0.1f, 1f)] public float CellSize = 0.2f;
        [Min(0.01f)] public float AgentRadius = 0.1f;
        [Min(0.2f)] public float AgentHeight = 1.7f;
        [Range(0, 70)] public float MaximumSlope = 48f;
        [Range(0.05f, 1f)] public float StepHeight = 0.45f;
        [Range(0.05f, 10f)] public float MaximumDrop = 2f;
        [Range(0f, 20f), Tooltip("Largest gap to precompute between disconnected deck cells. Runtime also respects Maximum Surface Gap in the enemy movement settings.")]
        public float MaximumGap = 5f;
        [Range(0f, 20f)] public float TransferHeight = 10f;
        public bool ShowNavigation = true;

        public EnemyDeckNavigationData MapFor(float radius, float height, float maximumSlope)
        {
            EnemyDeckNavigationData best = null;
            void Consider(EnemyDeckNavigationData map)
            {
                if (map == null || !map.IsBaked || map.AgentRadius + 0.001f < radius ||
                    map.AgentHeight + 0.001f < height || map.MaximumSlope > maximumSlope + 0.001f) return;
                if (best == null || map.AgentRadius < best.AgentRadius ||
                    Mathf.Approximately(map.AgentRadius, best.AgentRadius) && map.AgentHeight < best.AgentHeight) best = map;
            }
            Consider(Data);
            if (AdditionalAgentMaps != null) foreach (var map in AdditionalAgentMaps) Consider(map);
            return best;
        }

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
