using System;
using System.Collections.Generic;
using UnityEngine;

namespace UColliders.CoACD
{
    /// <summary>
    /// Wrapper for the CoACD (Collision-Aware Convex Decomposition) algorithm.
    /// Uses the pure C# implementation.
    /// </summary>
    public static class CoACDWrapper
    {
        /// <summary>
        /// Optional progress callback: (message, progress 0-1).
        /// Set before calling <see cref="RunACD"/> to receive progress updates.
        /// </summary>
        public static Action<string, float> OnProgress
        {
            get => CoACDEngine.OnProgress;
            set => CoACDEngine.OnProgress = value;
        }

        /// <summary>
        /// Set to <c>true</c> to cancel the current decomposition.
        /// Checked between iterations; throws <see cref="OperationCanceledException"/>.
        /// </summary>
        public static bool Cancelled
        {
            get => CoACDEngine.Cancelled;
            set => CoACDEngine.Cancelled = value;
        }

        /// <summary>
        /// Returns <c>true</c> because CoACD is always available via the pure C# implementation.
        /// </summary>
        public static bool IsAvailable() => true;

        /// <summary>
        /// Run CoACD decomposition on a Unity Mesh and return a list of convex hull meshes.
        /// </summary>
        /// <param name="mesh">The input mesh to decompose.</param>
        /// <param name="parameters">CoACD algorithm parameters.</param>
        /// <returns>A list of convex hull meshes.</returns>
        public static List<Mesh> RunACD(Mesh mesh, CoACDParameters parameters)
        {
            return CoACDEngine.Run(mesh, parameters);
        }
    }
}
