/** \file
    \brief Standalone convex decomposition component using CoACD.
*/
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UColliders.CoACD;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UColliders {

    /// <summary>
    /// Generates convex hull colliders for a mesh using the CoACD algorithm.
    ///
    /// Add this component to a GameObject with a MeshFilter or SkinnedMeshRenderer.
    /// Click "Regenerate Colliders" in the inspector to generate convex MeshColliders.
    /// Colliders can also be generated at runtime via <see cref="RegenerateColliders"/>.
    /// </summary>
    /// <remarks>
    /// <b>Performance warning:</b> CoACD decomposition is computationally expensive and
    /// typically takes one to ten minutes depending on mesh complexity and parameters.
    /// For best results, generate colliders in the editor and save them into your
    /// scene or prefab. Runtime generation is available but will block the main thread
    /// (or a coroutine frame) for the entire duration.
    /// </remarks>
    [ExecuteAlways]
    [AddComponentMenu("Physics/Convex Decomposer")]
    public class ConvexDecomposer : MonoBehaviour
    {
        /// <summary>
        /// Parameters for the CoACD algorithm.
        /// </summary>
        [SerializeField]
        [Tooltip("Parameters for the CoACD convex decomposition algorithm.")]
        private CoACDParameters _coacdParameters = new CoACDParameters();

        /// <summary>
        /// Parameters for the CoACD algorithm.
        /// </summary>
        public CoACDParameters coacdParameters {
            get => _coacdParameters;
            set => _coacdParameters = value;
        }

        /// <summary>
        /// The way to display preview colliders.
        /// </summary>
        public enum PreviewMode {
            /// <summary>Do not preview colliders.</summary>
            None,
            /// <summary>Preview collider wireframes with a fixed color.</summary>
            Solid,
            /// <summary>Preview collider wireframes with random colors.</summary>
            Random,
            /// <summary>Preview colliders with a filled volume of fixed color.</summary>
            Fill,
            /// <summary>Preview colliders with a filled volume of random colors.</summary>
            RandomFill
        }

        /// <summary>
        /// How collider hulls are previewed in the Scene view.
        /// </summary>
        [Tooltip("How collider hulls are shown in the Scene view.")]
        public PreviewMode previewColor = PreviewMode.Solid;

        /// <summary>
        /// Fixed color used when <see cref="previewColor"/> is Solid or Fill.
        /// </summary>
        [Tooltip("Fixed color for collider preview gizmos.")]
        public Color previewSolidColor = Color.green;

        /// <summary>
        /// Combine meshes from all child GameObjects into a single decomposition.
        /// </summary>
        [Tooltip("Combine meshes from all child GameObjects into a single decomposition.")]
        public bool includeChildrenMeshes;

        /// <summary>
        /// Cached convex hull meshes from the last decomposition.
        /// </summary>
        [NonSerialized]
        private List<Mesh> _convexHulls;

        /// <summary>
        /// Invoked after collider generation completes (both sync and async paths).
        /// </summary>
        public event Action<ConvexDecomposer> OnCollidersGenerated;

        /// <summary>
        /// Returns the number of convex hulls generated.
        /// </summary>
        public int CountHulls() {
            return _convexHulls != null ? _convexHulls.Count : 0;
        }

        /// <summary>
        /// Load mesh data from MeshFilter or SkinnedMeshRenderer.
        /// </summary>
        /// <returns>The mesh to decompose.</returns>
        /// <exception cref="NoMeshFilterException">If no mesh source is found.</exception>
        /// <exception cref="ReadMeshDisabledException">If the mesh is not readable.</exception>
        Mesh LoadMesh() {
            if (includeChildrenMeshes) {
                return LoadCombinedMesh();
            }
            if (TryGetComponent<SkinnedMeshRenderer>(out var skinnedRenderer)) {
                if (skinnedRenderer.sharedMesh == null)
                    throw new NoMeshFilterException();
                if (!skinnedRenderer.sharedMesh.isReadable)
                    throw new ReadMeshDisabledException();
                Mesh baked = new Mesh();
                skinnedRenderer.BakeMesh(baked, useScale: false);
                return baked;
            }
            if (TryGetComponent<MeshFilter>(out MeshFilter meshFilter)) {
                Mesh mesh = meshFilter.sharedMesh;
                if (mesh == null)
                    throw new NoMeshFilterException();
                if (!mesh.isReadable)
                    throw new ReadMeshDisabledException();
                return mesh;
            }
            throw new NoMeshFilterException();
        }

        /// <summary>
        /// Combine meshes from this object and all children into a single mesh.
        /// </summary>
        Mesh LoadCombinedMesh() {
            List<Vector3> allVertices = new List<Vector3>();
            List<int> allTriangles = new List<int>();
            foreach (MeshFilter meshF in GetComponentsInChildren<MeshFilter>()) {
                Mesh m = meshF.sharedMesh;
                if (m == null || !m.isReadable)
                    continue;
                int baseVertex = allVertices.Count;
                Matrix4x4 matrix = transform.worldToLocalMatrix
                    * meshF.gameObject.transform.localToWorldMatrix;
                foreach (Vector3 vertex in m.vertices)
                    allVertices.Add(matrix.MultiplyPoint(vertex));
                foreach (int triangle in m.triangles)
                    allTriangles.Add(triangle + baseVertex);
            }
            if (allVertices.Count == 0)
                throw new NoMeshFilterException();
            Mesh combined = new Mesh();
            combined.SetVertices(allVertices);
            combined.SetTriangles(allTriangles, 0);
            combined.RecalculateBounds();
            combined.RecalculateNormals();
            return combined;
        }

        /// <summary>
        /// Destroy all generated collider children.
        /// </summary>
        public void DestroyColliders() {
            // Iterate in reverse to avoid index shifting
            for (int i = transform.childCount - 1; i >= 0; i--) {
                GameObject child = transform.GetChild(i).gameObject;
                if (child.name.StartsWith("ConvexHull_")) {
#if UNITY_EDITOR
                    Undo.DestroyObjectImmediate(child);
#else
                    Destroy(child);
#endif
                }
            }
        }

        /// <summary>
        /// Delete all generated colliders and reset cached data.
        /// </summary>
        public void DeleteCollidersAndData() {
            DestroyColliders();
            _convexHulls = null;
        }

        /// <summary>
        /// Regenerate all convex hull colliders.
        /// </summary>
        /// <remarks>
        /// <b>Warning:</b> This call blocks the main thread for one to ten minutes
        /// depending on mesh complexity. Prefer generating colliders in the editor
        /// and saving them into your scene or prefab.
        /// </remarks>
        public void RegenerateColliders() {
            DestroyColliders();
            Mesh sourceMesh = LoadMesh();
            _convexHulls = CoACDWrapper.RunACD(sourceMesh, _coacdParameters);
            InstantiateHulls();
            OnCollidersGenerated?.Invoke(this);
        }

        /// <summary>
        /// Coroutine that regenerates colliders, yielding between steps
        /// to spread work across frames.
        /// </summary>
        /// <remarks>
        /// <b>Warning:</b> The CoACD computation itself is not incremental — the
        /// decomposition step will still block a single frame for one to ten minutes.
        /// Only mesh loading and hull instantiation are spread across frames. Prefer
        /// generating colliders in the editor and saving them into your scene or prefab.
        /// </remarks>
        public IEnumerator GenerateCollidersAsync() {
            DestroyColliders();
            yield return null;
            Mesh sourceMesh = LoadMesh();
            _convexHulls = CoACDWrapper.RunACD(sourceMesh, _coacdParameters);
            yield return null;
            InstantiateHulls();
            OnCollidersGenerated?.Invoke(this);
        }

        /// <summary>
        /// Create child GameObjects with convex MeshColliders from cached hulls.
        /// </summary>
        void InstantiateHulls() {
            if (_convexHulls == null)
                return;
            for (int i = 0; i < _convexHulls.Count; i++) {
                GameObject hullObject = new GameObject("ConvexHull_" + i);
#if UNITY_EDITOR
                Undo.RegisterCreatedObjectUndo(hullObject, "Created ConvexHull");
#endif
                hullObject.transform.SetParent(transform);
                hullObject.transform.localPosition = Vector3.zero;
                hullObject.transform.localRotation = Quaternion.identity;
                hullObject.transform.localScale = Vector3.one;
                MeshCollider mc = hullObject.AddComponent<MeshCollider>();
                mc.sharedMesh = _convexHulls[i];
                mc.cookingOptions = MeshColliderCookingOptions.CookForFasterSimulation
                    | MeshColliderCookingOptions.WeldColocatedVertices;
                mc.convex = true;
            }
        }

        /// <summary>
        /// Remove the ConvexDecomposer component but keep generated collider children.
        /// </summary>
        /// <returns>The number of hull children that were kept.</returns>
        public int UnpackColliders() {
            int count = 0;
            for (int i = 0; i < transform.childCount; i++) {
                if (transform.GetChild(i).gameObject.name.StartsWith("ConvexHull_"))
                    count++;
            }
#if UNITY_EDITOR
            Undo.DestroyObjectImmediate(this);
#else
            Destroy(this);
#endif
            return count;
        }

        void Start() {
#if UNITY_EDITOR
            // In the editor (edit mode), CoACD is expensive — require explicit trigger.
            if (!Application.isPlaying)
                return;
#endif
            // In play mode, regenerate if no hull children exist
            bool hasHulls = false;
            for (int i = 0; i < transform.childCount; i++) {
                if (transform.GetChild(i).gameObject.name.StartsWith("ConvexHull_")) {
                    hasHulls = true;
                    break;
                }
            }
            if (!hasHulls) {
                Debug.LogWarning(
                    $"[ConvexDecomposer] Generating colliders at runtime on \"{gameObject.name}\". " +
                    "This will block the main thread for one to ten minutes. " +
                    "For best performance, generate colliders in the editor and save them into your scene or prefab.",
                    this);
                RegenerateColliders();
            }
        }

        void OnDrawGizmosSelected() {
            if (!isActiveAndEnabled)
                return;
            if (_convexHulls == null || _convexHulls.Count == 0 || previewColor == PreviewMode.None)
                return;
            Gizmos.matrix = transform.localToWorldMatrix;
            for (int i = 0; i < _convexHulls.Count; i++) {
                Color drawColor = previewSolidColor;
                if (previewColor == PreviewMode.Random || previewColor == PreviewMode.RandomFill) {
                    float hue = (i * 0.618034f) % 1f;
                    drawColor = Color.HSVToRGB(hue, 0.75f, 0.9f);
                }
                Gizmos.color = drawColor;
                if (previewColor == PreviewMode.Solid || previewColor == PreviewMode.Random)
                    Gizmos.DrawWireMesh(_convexHulls[i]);
                else
                    Gizmos.DrawMesh(_convexHulls[i]);
            }
        }

        void OnDestroy() {
            _convexHulls = null;
        }
    }
}
