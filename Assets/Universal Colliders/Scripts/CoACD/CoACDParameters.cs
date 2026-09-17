using System;
using UnityEngine;

namespace UColliders.CoACD
{
    /// <summary>
    /// Preprocess mode for CoACD manifold preprocessing (native library only).
    /// </summary>
    public enum CoACDPreprocessMode
    {
        /// <summary>Automatically check input mesh manifoldness.</summary>
        Auto = 0,
        /// <summary>Force turn on the pre-processing.</summary>
        On = 1,
        /// <summary>Force turn off the pre-processing.</summary>
        Off = 2
    }

    /// <summary>
    /// Parameters for the CoACD (Collision-Aware Convex Decomposition) algorithm.
    /// </summary>
    [Serializable]
    public class CoACDParameters
    {
        /// <summary>
        /// Concavity threshold for terminating the decomposition.
        /// Lower values produce more convex hulls with tighter fits.
        /// </summary>
        [Range(0.01f, 1f)]
        [Tooltip("How closely colliders must follow the mesh surface.\n" +
            "Lower = more accurate (more pieces, slower).\n" +
            "Higher = fewer pieces (faster, rougher fit).\n" +
            "Recommended: 0.05 (precise) to 0.2 (fast).")]
        public float threshold = 0.1f;

        /// <summary>
        /// Maximum number of convex hulls generated. -1 for no limit.
        /// </summary>
        [Tooltip("Maximum number of collider pieces to generate.\n" +
            "-1 = no limit (let the algorithm decide).\n" +
            "Use a positive value to cap the number of colliders.")]
        public int maxConvexHull = -1;

        /// <summary>
        /// Manifold preprocessing mode (native library only, ignored by C# implementation).
        /// </summary>
        [Tooltip("Mesh repair mode (native library only).\n" +
            "Auto: check if the mesh needs repair.\n" +
            "On: always repair.\n" +
            "Off: skip repair (faster, but may fail on non-manifold meshes).")]
        public CoACDPreprocessMode preprocessMode = CoACDPreprocessMode.Auto;

        /// <summary>
        /// Voxel grid resolution for manifold preprocessing.
        /// Higher values preserve more detail but are slower.
        /// </summary>
        [Range(20, 100)]
        [Tooltip("Voxel grid resolution for mesh repair (20-100).\n" +
            "Higher = better detail preservation but slower.\n" +
            "Lower = faster but may lose thin features.")]
        public int preprocessResolution = 30;

        /// <summary>
        /// Number of surface sample points for quality evaluation.
        /// </summary>
        [Range(200, 10000)]
        [Tooltip("Number of points sampled on the mesh surface to evaluate fit quality.\n" +
            "Higher = more accurate quality checks but slower.\n" +
            "Recommended: 500–2000.")]
        public int sampleResolution = 800;

        /// <summary>
        /// Number of candidate cutting planes evaluated per split.
        /// </summary>
        [Range(5, 40)]
        [Tooltip("Number of candidate cutting positions tested per split.\n" +
            "Higher = better cuts but slower.\n" +
            "Recommended: 10–20.")]
        public int mctsNodes = 12;

        /// <summary>
        /// Number of search iterations per split.
        /// </summary>
        [Range(20, 2000)]
        [Tooltip("How many times the algorithm explores different cuts per split.\n" +
            "Higher = better results but slower.\n" +
            "Recommended: 50 (fast) to 200 (high quality).")]
        public int mctsIteration = 50;

        /// <summary>
        /// How many cuts ahead the algorithm looks when choosing where to split.
        /// </summary>
        [Range(2, 7)]
        [Tooltip("How many cuts ahead the algorithm plans.\n" +
            "Higher = smarter cuts but exponentially slower.\n" +
            "Recommended: 2–3.")]
        public int mctsMaxDepth = 2;

        /// <summary>
        /// Align the mesh to its principal axes before decomposition.
        /// Can help with elongated or rotated meshes.
        /// </summary>
        [Tooltip("Align the mesh to its principal axes before splitting.\n" +
            "Can improve results for elongated or rotated meshes.")]
        public bool pca;

        /// <summary>
        /// Merge similar adjacent pieces after decomposition to reduce collider count.
        /// </summary>
        [Tooltip("Merge similar adjacent pieces after decomposition.\n" +
            "Reduces the number of colliders without losing much accuracy.")]
        public bool merge = true;

        /// <summary>
        /// Random seed for reproducibility. 0 for default.
        /// </summary>
        [Tooltip("Fixed seed for reproducible results. 0 = default seed.")]
        public uint seed;
    }
}
