using UnityEngine;
using UnityEngine.UI;
using UColliders;

namespace UColliders.Demos
{
    /// <summary>
    /// Side-by-side comparison of OBB vs CoACD decomposition on the same mesh.
    /// Attach to a parent GameObject. Requires two children: one with OBB, one with CoACD.
    /// The OBB side cycles through recursion levels automatically.
    /// </summary>
    public class DecompositionShowcase : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The UCollidersRoot using OBB decomposition.")]
        public UCollidersRoot obbRoot;

        [Tooltip("The ConvexDecomposer using CoACD decomposition.")]
        public ConvexDecomposer coacdDecomposer;

        [Tooltip("UI text displaying current OBB recursion level.")]
        public Text obbLabel;

        [Tooltip("UI text displaying CoACD hull count.")]
        public Text coacdLabel;

        [Header("Settings")]
        [Tooltip("Rotation speed in degrees per second.")]
        public float rotationSpeed = 30f;

        [Tooltip("Seconds between OBB recursion level changes.")]
        public float levelInterval = 2f;

        [Tooltip("Maximum recursion level before looping.")]
        public int maxLevel = 8;

        float timer;

        void Start()
        {
            if (obbRoot != null) {
                obbRoot.recursionLevel = 0;
                obbRoot.previewColor = UCollidersRoot.CollidersPreviewColor.Random;
                obbRoot.RegenerateColliders();
                UpdateOBBLabel();
            }

            if (coacdDecomposer != null) {
                coacdDecomposer.previewColor = ConvexDecomposer.PreviewMode.Random;
                coacdDecomposer.RegenerateColliders();
                UpdateCoACDLabel();
            }
        }

        void Update()
        {
            // Rotate both models together
            transform.Rotate(Vector3.up * rotationSpeed * Time.deltaTime, Space.World);

            // Cycle OBB recursion levels
            if (obbRoot == null) return;
            timer += Time.deltaTime;
            if (timer >= levelInterval) {
                timer = 0f;
                int next = obbRoot.recursionLevel + 1;
                if (next > maxLevel) next = 0;
                obbRoot.recursionLevel = next;
                obbRoot.RegenerateColliders();
                UpdateOBBLabel();
            }
        }

        void UpdateOBBLabel()
        {
            if (obbLabel != null)
                obbLabel.text = $"OBB Level {obbRoot.recursionLevel} ({obbRoot.CountLeaves()} colliders)";
        }

        void UpdateCoACDLabel()
        {
            if (coacdLabel != null && coacdDecomposer != null)
                coacdLabel.text = $"CoACD ({coacdDecomposer.CountHulls()} hulls)";
        }
    }
}
