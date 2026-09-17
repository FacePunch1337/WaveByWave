using System.Collections.Generic;
using UnityEngine;

namespace WaveByWave.Collision
{
    /// <summary>
    /// Identifies collision geometry created by the editor collision generator and
    /// remembers which source colliders were replaced. The component is deliberately
    /// lightweight so generated collision remains valid in player builds.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class GeneratedCollisionSet : MonoBehaviour
    {
        [SerializeField, HideInInspector] private GameObject sourceRoot;
        [SerializeField, HideInInspector] private bool usesConvexColliders;
        [SerializeField, HideInInspector] private List<MeshCollider> sourceColliders = new();
        [SerializeField, HideInInspector] private List<bool> previousEnabledStates = new();

        public GameObject SourceRoot => sourceRoot;
        public bool UsesConvexColliders => usesConvexColliders;
        public IReadOnlyList<MeshCollider> SourceColliders => sourceColliders;
        public IReadOnlyList<bool> PreviousEnabledStates => previousEnabledStates;

        public void Configure(
            GameObject source,
            bool generatedWithConvexColliders,
            IReadOnlyList<MeshCollider> colliders,
            IReadOnlyList<bool> enabledStates)
        {
            sourceRoot = source;
            usesConvexColliders = generatedWithConvexColliders;
            sourceColliders.Clear();
            previousEnabledStates.Clear();

            if (colliders == null || enabledStates == null)
                return;

            var count = Mathf.Min(colliders.Count, enabledStates.Count);
            for (var i = 0; i < count; i++)
            {
                sourceColliders.Add(colliders[i]);
                previousEnabledStates.Add(enabledStates[i]);
            }
        }
    }
}
