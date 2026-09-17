/** \file 
    \brief The main file to generate UCollidersRoot Components
*/
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Serialization;
using UColliders.OBBTree;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UColliders {

    /// <summary>
    /// The main component created of this project.
    ///
    /// The end-user will interact with this component as the base
    /// way to create UCollidersRoot node.
    /// Simply drag this component in the Editor and launch the scene.
    /// You can also generate UCollidersLeaf at Runtime with the 
    /// UCollidersRoot::RegenerateColliders method. 
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Physics/Universal Collider")]
    public class UCollidersRoot : UCollidersNode
    {
        /// <summary>
        /// Recursion level for the UCollidersLeaf generation.
        /// </summary>
        /// <remarks>Maximum number of OBBs = 2<sup>recursionLevel</sup></remarks>
        [SerializeField]
        [FormerlySerializedAs("recursionLevel")]
        [Min(-1)]
        [Tooltip("Recursion level for OBBs")]
        private int _recursionLevel = -1;

        /// <summary>
        /// Recursion level for OBB subdivision. Clamped to [-1, MaxRecursionDepth].
        /// -1 means no colliders are generated.
        /// </summary>
        public int recursionLevel {
            get => _recursionLevel;
            set => _recursionLevel = Mathf.Clamp(value, -1, MaxRecursionDepth);
        }

        /// <summary>
        /// The differents ways the preview of UCollidersLeaf nodes can be done.
        /// </summary>
        public enum CollidersPreviewColor {
            /// <summary>
            /// Do not preview colliders.
            /// </summary>
            None,  
            /// <summary>
            /// Preview colliders's  wireframe
            /// </summary>
            Solid, 
            /// <summary>
            /// Preview colliders's wireframe with random color.
            /// </summary>
            Random, 
            /// <summary>
            /// Preview colliders with a filled volume of color.
            /// </summary>
            Fill, 
            /// <summary>
            /// Preview colliders with a filled volume of random color.
            /// </summary>
            RandomFill 
        };

        /// <summary>
        /// The actual way the preview of the UCollidersLeaf is done.
        /// </summary>
        [Tooltip("The way to display preview colliders.")]
        public CollidersPreviewColor previewColor = CollidersPreviewColor.Solid;

        /// <summary>
        /// When previewColor == CollidersPreviewColor.Solid, use this color.
        /// </summary>
        /// <see cref="CollidersPreviewColor.Solid" />
        [Tooltip("Fixed color for colliders preview.")]
        public Color previewSolidColor = Color.green;

        /// <summary>
        /// Allows the use of hybrid mesh made from the concatenation 
        /// of the differents children mesh.
        /// </summary>
        [Tooltip("Add vertices from children recursively")]
        public bool includeChildrenMeshes;

        /// <summary>
        /// Balance the mesh vertices distribution.
        /// </summary>
        [Tooltip("Balance the mesh vertices distribution")]
        public bool optimizeMesh;

        /// <summary>
        /// Maximum allowed recursion depth to prevent stack overflow on pathological meshes.
        /// </summary>
        public const int MaxRecursionDepth = 20;

        /// <summary>
        /// Balancing only supplies extra samples for choosing OBB split planes. More than
        /// a few samples per source triangle does not improve the final box tree, but used
        /// to multiply a level-8 mesh into hundreds of thousands of temporary triangles.
        /// </summary>
        internal const int MaxBalancedCutsPerTriangle = 4;

        /// <summary>
        /// Minimum projected surface coverage required to accept one OBB as a fitted
        /// board-like collider. Detail Level remains only a maximum recursion depth.
        /// </summary>
        internal const float AdaptiveSurfaceCoverageThreshold = 0.72f;

        private int _optimizationLevel;

        /// <summary>
        /// Clip vertex definitions created by SplittingSubdivide during OBB tree construction.
        /// Used to reconstruct clip vertices when the source mesh deforms (skinned meshes).
        /// </summary>
        [System.NonSerialized]
        private List<ClipVertexDef> _clipVertexDefs;



        /// <summary>
        /// By default, we encapsulate vertices only, 
        /// which leaves gaps between the colliders. 
        /// Check this option to enclose triangles as well, 
        /// which prevents holes formation but has a slower 
        /// convergence speed.
        /// </summary>
        /// <remarks>
        /// In the case of disjoint triangles, holes will still form.
        /// </remarks>
        /// <see cref="encapsulationPath">Complete encapsulation path;</see>
        [Tooltip(@"By default, we encapsulate vertices only, 
which leaves gaps between the colliders. 
Check this option to enclose triangles as well, 
which prevents holes but has a slower convergence speed.")]
        public bool encapsulateTriangles;

        /// <summary>
        /// The path of the successive OBB.
        /// It determines for each recursion level if we should encapsulate
        /// triangles or only vertices.
        /// <list type="bullet">
        /// <item>
        /// <description>0: encapsulate vertices only.</description>
        /// </item>
        /// <item>
        /// <description>1: encapsulate triangles.</description>
        /// </item>
        /// </list>
        /// </summary>
        /// <seealso cref="encapsulateTriangles">User Choice for last Level</seealso>
        /// <seealso cref="UCollidersLeaf.nodeId">
        /// Nodes ID are coherent with encapsulation path by default.
        /// </seealso>
        [Tooltip("Encapsulate or not triangles for each recursion level.")]
        public string encapsulationPath;

        // --------------------------------------------------------------------------------

        /// <summary>
        /// All vertices used by the OBB tree, including clip vertices appended by SplittingSubdivide.
        /// For non-skinned meshes this matches <c>mesh.vertices</c> plus clip vertices.
        /// For skinned meshes the baked vertices are followed by reconstructed clip vertices.
        /// </summary>
        public Vector3[] vertices => _vertices;

        /// <summary>
        /// Returns the shared mesh from a <c>MeshFilter</c>.
        /// Always uses <c>sharedMesh</c> to avoid creating a per-instance copy that leaks memory.
        /// </summary>
        /// <param name="meshFilter">The <c>MeshFilter</c> component.</param>
        /// <returns>The shared mesh.</returns>
        /// <exception cref="ReadMeshDisabledException">If the mesh is not readable.</exception>
        Mesh GetMesh(MeshFilter meshFilter) {
            Mesh mesh = meshFilter.sharedMesh;
            if (!mesh.isReadable)
                throw new ReadMeshDisabledException();
            return mesh;
        }

        /// <summary>
        /// Add vertices to <c>_vertices</c> using the children GameObjects.
        /// </summary>
        /// <param name="gObject">The <c>GameObject</c> from which to add the vertices.</param>
        /// <returns>The new array of triangles to use.</returns>
        void AddVerticesRecursively(out Vector3[] vertices, out int[] triangles) {
            List<Vector3> allVertices = new List<Vector3>();
            List<int> allTriangles = new List<int>();
            Mesh newMesh;
            int baseVertex;
            Matrix4x4 transformMatrix;
            foreach (MeshFilter meshF in GetComponentsInChildren<MeshFilter>()) {
                newMesh = GetMesh(meshF);
                baseVertex = allVertices.Count;
                transformMatrix = transform.worldToLocalMatrix 
                * meshF.gameObject.transform.localToWorldMatrix;
                // Add vertices in the good space
                foreach (Vector3 vertex in newMesh.vertices) {
                    allVertices.Add(
                        transformMatrix.MultiplyPoint(vertex)
                    );
                }
                foreach (int triangle in newMesh.triangles)
                    allTriangles.Add(triangle + baseVertex);
            }
            vertices = allVertices.ToArray();
            triangles = allTriangles.ToArray();
        }

        /// <summary>Reusable list for BakeMesh vertex extraction (avoids per-frame allocation).</summary>
        private List<Vector3> _bakedVertexBuffer;

        void RecomputeSkinnedMesh(SkinnedMeshRenderer skinnedRenderer) {
            if (skinnedRenderer == null || skinnedRenderer.sharedMesh == null)
                return;
            if (!skinnedRenderer.sharedMesh.isReadable)
                throw new ReadMeshDisabledException();
            // BakeMesh(useScale:false) returns vertices in bind-pose space without any
            // transform scale. But the leaf children inherit the full ancestor transform
            // (lossyScale), while the SkinnedMeshRenderer visual is positioned by bones
            // independently. We compensate by dividing vertices by lossyScale so that
            // leaf.worldScale = lossyScale * (OBB / lossyScale) = OBB = visual size.
            if (mesh == null)
                mesh = new Mesh();
            skinnedRenderer.BakeMesh(mesh, useScale: false);
            // Reuse buffer to avoid per-frame allocation from mesh.vertices
            if (_bakedVertexBuffer == null)
                _bakedVertexBuffer = new List<Vector3>(mesh.vertexCount);
            mesh.GetVertices(_bakedVertexBuffer);
            int bakedCount = _bakedVertexBuffer.Count;
            int clipCount = _clipVertexDefs?.Count ?? 0;
            int totalCount = bakedCount + clipCount;
            if (_vertices == null || _vertices.Length != totalCount)
                _vertices = new Vector3[totalCount];
            _bakedVertexBuffer.CopyTo(_vertices);
            // Compensate for ancestor scale so colliders match the visual mesh
            Vector3 lossy = transform.lossyScale;
            if (lossy.x != 1f || lossy.y != 1f || lossy.z != 1f) {
                Vector3 invScale = new Vector3(1f / lossy.x, 1f / lossy.y, 1f / lossy.z);
                for (int i = 0; i < bakedCount; i++)
                    _vertices[i] = Vector3.Scale(_vertices[i], invScale);
            }
            // Reconstruct clip vertices from their source vertex definitions.
            // Must be done in order since later clip vertices may reference earlier ones.
            if (_clipVertexDefs != null) {
                for (int i = 0; i < clipCount; i++) {
                    var def = _clipVertexDefs[i];
                    _vertices[bakedCount + i] = Vector3.Lerp(
                        _vertices[def.indexA], _vertices[def.indexB], def.t);
                }
            }
            // Refit existing tree if available, otherwise do a full build
            if (node != null && node.obb.triangles != null) {
                RefitTree(node);
                // Update leaf collider transforms to match refitted OBBs
                foreach (UCollidersLeaf leaf in GetComponentsInChildren<UCollidersLeaf>())
                    leaf.UpdateTransform();
            } else {
                node.obb = node.obb.BuildOBB(_vertices, mesh.triangles);
                node.children = new OBBTreeNode[] { null, null };
            }
        }

        /// <summary>
        /// Recursively refit all OBBs in the tree to updated vertices.
        /// Uses bottom-up approach: refit leaf nodes from vertices first, then merge
        /// parent bounds from children bounds. Each triangle is only processed once
        /// (at the leaf level) instead of once per tree depth level.
        /// </summary>
        /// <param name="treeNode">The node to refit.</param>
        void RefitTree(OBBTreeNode treeNode) {
            if (treeNode == null || treeNode.obb.triangles == null)
                return;
            bool hasChildren = treeNode.children != null && treeNode.children.Length == 2
                && treeNode.children[0] != null;
            if (hasChildren) {
                // Bottom-up: refit children first
                RefitTree(treeNode.children[0]);
                RefitTree(treeNode.children[1]);
                // Merge parent bounds from children instead of re-iterating all triangles
                treeNode.obb = treeNode.obb.RefitFromChildren(
                    treeNode.children[0].obb, treeNode.children[1].obb);
            } else {
                // Leaf node: refit from actual vertices
                treeNode.obb = treeNode.obb.RefitOBB(_vertices);
            }
        }

        /// <summary>
        /// Balance the mesh vertices distribution by subdividing long edges.
        /// Adds intermediate vertices along edges that are large relative to the recursion level.
        /// </summary>
        /// <param name="mesh">The mesh to balance.</param>
        /// <returns>A new mesh with more evenly distributed vertices.</returns>
        Mesh BalanceMesh(Mesh mesh)
        {
            Vector3 extents = mesh.bounds.extents;
            float maxExtent = Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
            float threshold = maxExtent / Mathf.Pow(2, recursionLevel);
            float thresholdSq = threshold * threshold;

            List<Vector3> newVertices = new List<Vector3>();
            mesh.GetVertices(newVertices);
            List<int> newTriangles = new List<int>();
            int[] meshTriangles = mesh.triangles;
            Vector3[] meshVertices = mesh.vertices;
            int trianglesTotal = meshTriangles.Length;

            for (int t = 0; t < trianglesTotal; t += 3) {
                int idx0 = meshTriangles[t];
                int idx1 = meshTriangles[t + 1];
                int idx2 = meshTriangles[t + 2];

                float sqLen01 = (meshVertices[idx1] - meshVertices[idx0]).sqrMagnitude;
                float sqLen12 = (meshVertices[idx2] - meshVertices[idx1]).sqrMagnitude;
                float sqLen20 = (meshVertices[idx0] - meshVertices[idx2]).sqrMagnitude;

                // If all edges are short enough, keep the original triangle
                if (sqLen01 <= thresholdSq && sqLen12 <= thresholdSq && sqLen20 <= thresholdSq) {
                    newTriangles.Add(idx0);
                    newTriangles.Add(idx1);
                    newTriangles.Add(idx2);
                    continue;
                }

                // Find the longest edge: splitA -> splitB, with opposite vertex
                int splitA, splitB, opposite;
                float longestSqLen;
                if (sqLen01 >= sqLen12 && sqLen01 >= sqLen20) {
                    splitA = idx0; splitB = idx1; opposite = idx2;
                    longestSqLen = sqLen01;
                } else if (sqLen12 >= sqLen20) {
                    splitA = idx1; splitB = idx2; opposite = idx0;
                    longestSqLen = sqLen12;
                } else {
                    splitA = idx2; splitB = idx0; opposite = idx1;
                    longestSqLen = sqLen20;
                }

                float edgeLength = Mathf.Sqrt(longestSqLen);
                int cuts = Mathf.Clamp(
                    Mathf.CeilToInt(edgeLength / threshold) - 1,
                    1,
                    MaxBalancedCutsPerTriangle);

                // Insert evenly-spaced midpoints along splitA -> splitB
                int firstMidIdx = newVertices.Count;
                Vector3 vA = meshVertices[splitA];
                Vector3 vB = meshVertices[splitB];
                for (int j = 1; j <= cuts; j++) {
                    float frac = (float)j / (cuts + 1);
                    newVertices.Add(Vector3.Lerp(vA, vB, frac));
                }

                // Fan triangulation from opposite vertex
                // First fan triangle: (splitA, mid[0], opposite)
                newTriangles.Add(splitA);
                newTriangles.Add(firstMidIdx);
                newTriangles.Add(opposite);
                // Middle fan triangles: (mid[i], mid[i+1], opposite)
                for (int j = 0; j < cuts - 1; j++) {
                    newTriangles.Add(firstMidIdx + j);
                    newTriangles.Add(firstMidIdx + j + 1);
                    newTriangles.Add(opposite);
                }
                // Last fan triangle: (mid[last], splitB, opposite)
                newTriangles.Add(firstMidIdx + cuts - 1);
                newTriangles.Add(splitB);
                newTriangles.Add(opposite);
            }
            Mesh newMesh = new Mesh();
            newMesh.SetVertices(newVertices);
            newMesh.SetTriangles(newTriangles, 0);
            newMesh.RecalculateBounds();
            return newMesh;
        }

        /// <summary>
        /// Loads data and builds OBBs
        /// </summary>
        public void InitOBB()
        {
            node = new OBBTreeNode("");
            _clipVertexDefs = new List<ClipVertexDef>();
            if (TryGetComponent<SkinnedMeshRenderer>(out var skinnedMeshRenderer)) {
                if (skinnedMeshRenderer.sharedMesh == null)
                    throw new NoMeshFilterException();
                if (!skinnedMeshRenderer.sharedMesh.isReadable)
                    throw new ReadMeshDisabledException();
                RecomputeSkinnedMesh(skinnedMeshRenderer);
                return;
            }
            if (includeChildrenMeshes)
            {
                AddVerticesRecursively(out _vertices, out int[] triangles);
                mesh = ComputeMesh(triangles, _vertices);
                if (optimizeMesh) {
                    mesh = BalanceMesh(mesh);
                    _optimizationLevel = recursionLevel;
                }
                _vertices = mesh.vertices;
                // Just a user-friendly name
                StringBuilder nameBuilder = new StringBuilder();
                foreach (MeshFilter meshF in GetComponentsInChildren<MeshFilter>()) {
                    if (nameBuilder.Length > 0)
                        nameBuilder.Append("+");
                    nameBuilder.Append(meshF.name);
                }
                mesh.name = nameBuilder.ToString();
                // We add vertices from children, in this gameObject's space
                node.obb = node.obb.BuildOBB(mesh.vertices, mesh.triangles);
            }
            else if (TryGetComponent<MeshFilter>(out MeshFilter meshF))
            {
                mesh = GetMesh(meshF);
                if (mesh.vertexCount == 0 || mesh.triangles.Length == 0)
                    throw new TooFewVerticesException(mesh.vertexCount);
                if (optimizeMesh) {
                    mesh = BalanceMesh(mesh);
                    _optimizationLevel = recursionLevel;
                }
                _vertices = mesh.vertices;
                node.obb = node.obb.BuildOBB(_vertices, mesh.triangles);
            }
            else
            {
                throw new NoMeshFilterException();
            }
        }

        void Awake() {
            rootNode = this;
            if (!(
                TryGetComponent<MeshFilter>(out MeshFilter _) ||
                TryGetComponent<SkinnedMeshRenderer>(out SkinnedMeshRenderer _))
                ) {
                includeChildrenMeshes = true;
            }
            InitOBB();
        }

        /// <summary>
        /// Recomputes skinned mesh vertices and refits OBB colliders each frame.
        /// Only active when a <see cref="SkinnedMeshRenderer"/> is present.
        /// </summary>
        void LateUpdate() {
            if (TryGetComponent<SkinnedMeshRenderer>(out var skinnedRenderer))
                RecomputeSkinnedMesh(skinnedRenderer);
        }

        /// <summary>
        /// Invoked after collider generation completes (both sync and async paths).
        /// </summary>
        public event Action<UCollidersRoot> OnCollidersGenerated;

        /// <summary>
        /// Regenerates all colliders using the current decomposition method.
        /// </summary>
        public void RegenerateColliders() {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            DestroyColliders();
            if (mesh == null && node?.obb.triangles == null) {
                Debug.LogError("Cannot regenerate colliders: mesh data is not available. "
                    + "Check that Read/Write is enabled in the mesh import settings.", this);
                return;
            }
            ForceOBBRecursionLevel(recursionLevel);
            InstantiateColliders();
            stopwatch.Stop();
            int generatedLeaves = CountLeaves();
            int maximumLeaves = recursionLevel < 0
                ? 0
                : 1 << Mathf.Min(recursionLevel, 30);
            Debug.Log(
                $"[Universal Colliders] Generated {generatedLeaves} adaptive OBB colliders " +
                $"(detail limit {maximumLeaves}) for '{name}' in {stopwatch.Elapsed.TotalSeconds:0.###} s.",
                this);
            OnCollidersGenerated?.Invoke(this);
        }

        /// <summary>
        /// Coroutine that regenerates colliders, yielding between OBB tree building
        /// and collider instantiation to spread work across frames.
        /// Use <c>StartCoroutine(GenerateCollidersAsync())</c> at runtime
        /// to avoid frame spikes on large meshes.
        /// </summary>
        /// <returns>An <c>IEnumerator</c> for use with <c>StartCoroutine</c>.</returns>
        public IEnumerator GenerateCollidersAsync() {
            DestroyColliders();
            yield return null;
            InitOBB();
            yield return null;
            ForceOBBRecursionLevel(recursionLevel);
            yield return null;
            InstantiateColliders();
            OnCollidersGenerated?.Invoke(this);
        }

        /// <summary>
        /// Convert UCollidersLeaf objects to standard colliders.
        /// Delete unnecessary data.
        /// </summary>
        public int UnpackColliders() {
            UCollidersLeaf[] leaves = GetComponentsInChildren<UCollidersLeaf>();
            int count = leaves.Length;
            if (count == 0) {
                Debug.LogWarning(
                    "No child has a UColliderLeaf component to unpack!\n"
                    + "You may click on \"Regenerate Colliders\" in a first time."
                );
            }
#if UNITY_EDITOR
            Undo.SetCurrentGroupName("Unpack " + count + " UColliders");
#endif
            foreach (UCollidersLeaf child in leaves) {
                child.Unpack();
            }
#if UNITY_EDITOR
            Undo.IncrementCurrentGroup();
#endif
            DeleteCollidersAndData();
            return count;
        }

        /// <summary>
        /// Reset the data to default values
        /// </summary>
        public void DeleteCollidersAndData() {
            DestroyColliders(true);
            recursionLevel = -1;
            encapsulationPath = "";
            _optimizationLevel = -1;
            InitOBB();
        }

        // 
        /// <summary>
        /// Destroy the generated collider GameObjects.
        /// </summary>
        /// <param name="force">Set to <c>true</c> to delete even the OBB with the "preserveOBB" value.</param>
        public void DestroyColliders(bool force = false) {
            GameObject obbGameObject;
            foreach (UCollidersLeaf obbCode in transform.GetComponentsInChildren<UCollidersLeaf>()) {
                obbGameObject = obbCode.gameObject;
                if (force || !obbCode.preserveUCollider) {
#if UNITY_EDITOR
                    Undo.DestroyObjectImmediate(obbGameObject);
#else
                    Destroy(obbGameObject);
#endif
                }
            }
        }

        /// <summary>
        /// Instantiate the colliders associated with this node and his children.
        /// Set <c>saveColliders</c> to <c>true</c>.
        /// </summary>
        /// <param name="obbNode">
        /// The node to instantiate from if <c>level == obbLevel</c> is <c>true</c> 
        /// or if they are no children.
        /// </param>
        public void InstantiateColliders(OBBTreeNode obbNode) {
            UCollidersLeaf[] existingLeaves = GetComponentsInChildren<UCollidersLeaf>();
            var existingNodeIds = new HashSet<string>();
            foreach (UCollidersLeaf leaf in existingLeaves) {
                string existingId = leaf.node?.id ?? leaf.nodeId;
                if (existingId != null)
                    existingNodeIds.Add(existingId);
            }
            InstantiateColliders(obbNode, existingNodeIds);
        }

        void InstantiateColliders(OBBTreeNode obbNode, HashSet<string> existingNodeIds) {
            if (obbNode == null || obbNode.id == null)
                return;
            if (obbNode.id.Length > recursionLevel)
                return;
            if (
                obbNode.children == null
                || obbNode.children.Length == 0
                || obbNode.children[0] == null
                || obbNode.id.Length == recursionLevel
            ) {
                // Hash lookup keeps collider creation linear. The old nested scan made
                // 1024 leaves perform roughly one million component comparisons.
                if (existingNodeIds.Contains(obbNode.id))
                    return;
                GameObject obbGameObject = new GameObject(
                    "UCollider", typeof(UCollidersLeaf)
                );
#if UNITY_EDITOR
                Undo.RegisterCreatedObjectUndo(obbGameObject, "Created UColliderLeaf");
#endif
                obbGameObject.transform.SetParent(transform);
                UCollidersLeaf obbComponent = obbGameObject.GetComponent<UCollidersLeaf>();
                obbComponent.rootNode = this;
                obbComponent.SetTriangles(obbNode.obb.triangles);
                obbComponent.node = obbNode;
                obbComponent.shape = shape;
                obbComponent.boundicity = boundicity;
                obbComponent.nodeId = obbNode.id;
                obbComponent.CheckAndRebuild();
                obbComponent.RegenerateCollider();
                existingNodeIds.Add(obbNode.id);
            } else {
                foreach (OBBTreeNode obbChild in obbNode.children)
                    InstantiateColliders(obbChild, existingNodeIds);
            }
        }

        /// <summary>
        /// Instantiates colliders starting from <c>_node</c> and level 0.
        /// </summary>
        public void InstantiateColliders() {
            InstantiateColliders(node);
        }

        /// <summary>
        /// Generate colliders associated with the current decomposition method.
        /// </summary>
        public void Start() {
            // Skip regeneration if colliders already exist (e.g. loaded from scene).
            if (GetComponentInChildren<UCollidersLeaf>() != null)
                return;
            ForceOBBRecursionLevel(recursionLevel);
            RegenerateColliders();
        }


        /// <summary>
        /// Update <c>encapsulationPath</c> to match the current settings.
        /// Uses <c>recursionLevel</c> as default maximum level.
        /// </summary>
        public void SetEncapsulationPath() {
            SetEncapsulationPath(recursionLevel);
        }

        /// <summary>
        /// Update <c>encapsulationPath</c> to be as long as <c>level</c>.
        /// The path determines per-level whether to encapsulate triangles or only vertices.
        /// Read <c>encapsulationPath</c> after calling to inspect the result.
        /// </summary>
        /// <param name="level">The level of the path to reach.</param>
        public void SetEncapsulationPath(int level) {
            if (encapsulationPath == null)
                encapsulationPath = "";
            string lastMilestone = encapsulateTriangles ? "1" : "0";
            // End-if structure to avoid trimming multiple operations at once
            if (level < recursionLevel) {
                // Do not edit anything if we are not on the leaf node
            }
            else if (level < 0) {
                encapsulationPath = "";
            }
            else if (encapsulationPath.Length <= level) {
                int steps = level - encapsulationPath.Length + 1;
                for (int i = 0; i < steps; i++)
                    encapsulationPath += lastMilestone;
            }
            else if (encapsulationPath[level].ToString() != lastMilestone)
            {
                encapsulationPath = encapsulationPath.Substring(0, level)
                + lastMilestone;
            }
        }

        /// <summary>
        /// Build the OBB until the desired recursion level is met, starting from the provided node.
        /// </summary>
        /// <param name="node">Current <c>OBBTreeNode</c> node.</param>
        /// <param name="level">Maximum recursion depth.</param>
        void ForceOBBRecursionLevel(OBBTreeNode node, int level) {
            SetEncapsulationPath(level);
            // Abort if node is invalid (can happen after Unity serialization roundtrip)
            if (node == null || node.id == null || node.children == null)
                return;
            // Abort if there are no computable children or depth limit exceeded
            if (node.id.Length >= level || node.children.Length == 0 || node.id.Length >= MaxRecursionDepth)
                return;

            // Stop as soon as this part already behaves like one fitted board. This turns
            // Detail Level into a safety ceiling instead of forcing every branch to produce
            // 2^level equally tiny boxes. Low-coverage rings, bends, forks and disconnected
            // geometry continue subdividing until each leaf tightly matches its own section.
            if (node.obb.EstimateSurfaceCoverage(_vertices) >= AdaptiveSurfaceCoverageThreshold) {
                node.children = new OBBTreeNode[] { null, null };
                return;
            }
            if (node.children[0] == null || node.children[0].id == null) {
                bool encapsulateVerticesOnly;
                if (encapsulationPath.Length <= level) {
                    encapsulateVerticesOnly = !encapsulateTriangles;
                } else {
                    encapsulateVerticesOnly = encapsulationPath[level] == '0';
                }
                node.children = node.obb.ComputeChildren(
                    ref _vertices,
                    node.id,
                    new Vector3(
                        1/transform.lossyScale.x,
                        1/transform.lossyScale.y,
                        1/transform.lossyScale.z
                    ) * Physics.defaultContactOffset,
                    encapsulateVerticesOnly,
                    _clipVertexDefs
                );
            }
            if (node.children.Length > 0) {
                ForceOBBRecursionLevel(node.children[0], level);
                ForceOBBRecursionLevel(node.children[1], level);
            }
        }

        /// <summary>
        /// Build the OBB until the desired recursion level is met. 
        /// </summary>
        /// <param name="obbLevel">Maximum recursion level.</param>
        void ForceOBBRecursionLevel(int obbLevel) {
            if (obbLevel > MaxRecursionDepth) {
                Debug.LogWarning(
                    $"Recursion level {obbLevel} exceeds maximum ({MaxRecursionDepth}). Clamping.",
                    this
                );
                obbLevel = MaxRecursionDepth;
            }
            if (optimizeMesh && _optimizationLevel < recursionLevel) {
                InitOBB();
            }
            if (_clipVertexDefs == null)
                _clipVertexDefs = new List<ClipVertexDef>();
            ForceOBBRecursionLevel(node, obbLevel);
        }

        /// <summary>
        /// Return the OBBTreeNode at a specific path.
        /// This method dynamically recomputes the OBBTree.
        /// </summary>
        public OBBTreeNode GetOBBAtPath(string nodeId) {
            if (string.IsNullOrEmpty(nodeId))
                return node;
            if (node == null || _vertices == null || _vertices.Length == 0)
                throw new PathNotComputableException(nodeId);
            OBBTreeNode currentNode = node;
            for (int i = 0; i < nodeId.Length; i++) {
                if (currentNode.id == nodeId)
                    return currentNode;
                if (currentNode.children == null || currentNode.children.Length == 0)
                    throw new PathNotComputableException(nodeId);
                if (currentNode.children[0] == null) {
                    // Recompute children dynamically
                    currentNode.children = currentNode.obb.ComputeChildren(
                        ref _vertices, currentNode.id, outerSubdivide: nodeId[i] < '2'
                    );
                    if (currentNode.children.Length == 0)
                        throw new PathNotComputableException(nodeId);
                }
                // Find matching child
                OBBTreeNode found = null;
                for (int j = 0; j < currentNode.children.Length; j++) {
                    var child = currentNode.children[j];
                    if (child == null || child.id == null || child.id.Length <= i)
                        continue;
                    if (child.id[i] == nodeId[i]) {
                        found = child;
                        break;
                    }
                }
                if (found != null) {
                    currentNode = found;
                    continue;
                }
                // No child matched — try recomputing
                currentNode.children = currentNode.obb.ComputeChildren(
                    ref _vertices, currentNode.id, outerSubdivide: nodeId[i] < '2'
                );
                if (currentNode.children.Length == 0)
                    throw new PathNotComputableException(nodeId);
                for (int j = 0; j < currentNode.children.Length; j++) {
                    var child = currentNode.children[j];
                    if (child == null || child.id == null || child.id.Length <= i)
                        continue;
                    if (child.id[i] == nodeId[i]) {
                        found = child;
                        break;
                    }
                }
                if (found == null)
                    throw new PathNotComputableException(nodeId);
                currentNode = found;
            }
            return currentNode;
        }

        /// <summary>
        /// Return the number of leaf node that should be instantiated.
        /// </summary>
        /// <param name="obbNode">Current node studied.</param>
        /// <returns>The number of leaves starting from this node.</returns>
        public int CountLeaves(OBBTreeNode obbNode) {
            if (obbNode.id.Length > recursionLevel)
                return 0;
            if (
                obbNode.children.Length == 0 
                || obbNode.children[0] == null 
                || obbNode.id.Length == recursionLevel
                ) 
            {
                return 1;
            } else {
                int count = 0;
                foreach (OBBTreeNode obbChild in obbNode.children)
                    count += CountLeaves(obbChild);
                return count;
            }
        }


        /// <summary>
        /// Return the number of leaf node that should be instantiated.
        /// </summary>
        public int CountLeaves() {
            return CountLeaves(node);
        }

        void OnDrawGizmosSelected()
        {
            if (!this.isActiveAndEnabled)
                return;

            if (previewColor == CollidersPreviewColor.None || recursionLevel < 0)
                return;

            // Data are often not initialized
            if (node == null) {
                InitOBB();
            }
            if (_vertices == null || _vertices.Length < 4) {
                return;
            }
            ForceOBBRecursionLevel(recursionLevel);

            Draw(recursionLevel);
        }

        /// <summary>
        /// Draw a box. All values should be in local space.
        /// </summary>
        /// <param name="center">Center of the box.</param>
        /// <param name="size">Size on the box on each axis.</param>
        /// <param name="rotation">Rotation of the box.</param>
        /// <param name="parent">Parent <c>GameObject</c>.</param>
        /// <see cref="DrawOBB"/>
        public void DrawBox(Vector3 center, Vector3 size, Quaternion rotation, Transform parent) {
            // Known limitation: box preview may deform incorrectly with non-uniform scale
            Gizmos.matrix = parent.localToWorldMatrix * Matrix4x4.TRS(rotation * center, rotation, size);
            if (previewColor == CollidersPreviewColor.Solid || previewColor == CollidersPreviewColor.Random) {
                Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
            } else {
                Gizmos.DrawCube(Vector3.zero, Vector3.one);
            }
            
        }

        /// <summary>
        /// Draw a capsule preview as a box.
        /// Unity Gizmos has no native capsule primitive, so we draw the OBB box
        /// which closely matches the capsule's bounding volume.
        /// </summary>
        /// <param name="center">Center of the box.</param>
        /// <param name="scale">Scale on the box on each axis.</param>
        /// <param name="rotation">Rotation of the box.</param>
        /// <param name="parent">Parent <c>GameObject</c>.</param>
        /// <see cref="DrawBox"/>
        /// <see cref="DrawOBB"/>
        public void DrawCapsule(Vector3 center, Vector3 scale, Quaternion rotation, Transform parent) {
            DrawBox(center, scale, rotation, parent);
        }

        /// <summary>
        /// Draw a deformed Sphere.
        /// </summary>
        /// <param name="center">Center of the box.</param>
        /// <param name="scale">Scale on the box on each axis.</param>
        /// <param name="rotation">Rotation of the box.</param>
        /// <param name="parent">Parent <c>GameObject</c>.</param>
        /// <see cref="DrawOBB"/>
        public void DrawSphere(Vector3 center, Vector3 scale, Quaternion rotation, Transform parent) {
            // Known limitation: sphere preview may not exactly match the generated SphereCollider
            Gizmos.matrix = parent.localToWorldMatrix 
            * Matrix4x4.TRS(
                rotation * center, 
                rotation, 
                scale / 2);
            if (previewColor == CollidersPreviewColor.Solid || previewColor == CollidersPreviewColor.Random) {
                Gizmos.DrawWireSphere(Vector3.zero, Mathf.Lerp(1, Mathf.Sqrt(3), boundicity));
            } else {
                Gizmos.DrawSphere(Vector3.zero, Mathf.Lerp(1, Mathf.Sqrt(3), boundicity));
            }
        }

        /// <summary>Cached mesh used for gizmo drawing to avoid per-frame allocations.</summary>
        private Mesh _gizmoMesh;

        void OnDestroy()
        {
            if (_gizmoMesh != null)
            {
#if UNITY_EDITOR
                DestroyImmediate(_gizmoMesh);
#else
                Destroy(_gizmoMesh);
#endif
                _gizmoMesh = null;
            }
        }

        /// <summary>
        /// Draw a mesh.
        /// </summary>
        /// <param name="parent">Parent <c>GameObject</c>.</param>
        /// <param name="triangles">Triangles that define the submesh.</param>
        /// <see cref="DrawOBB"/>
        public void DrawMesh(Transform parent, int[] triangles) {
            Gizmos.matrix = parent.localToWorldMatrix;
            if (_gizmoMesh == null)
                _gizmoMesh = new Mesh();
            _gizmoMesh.Clear();
            _gizmoMesh.vertices = _vertices;
            _gizmoMesh.triangles = triangles;
            _gizmoMesh.RecalculateNormals();
            // Do not use ComputeMesh as it would be huge on memory
            if (previewColor == CollidersPreviewColor.Solid || previewColor == CollidersPreviewColor.Random) {
                Gizmos.DrawWireMesh(_gizmoMesh);
            } else {
                Gizmos.DrawMesh(_gizmoMesh);
            }
        }


        /// <summary>
        /// Draws an OBB, using <c>shape</c> to specify its shape.
        /// </summary>
        /// <param name="obb">The OBB to draw.</param>
        /// <param name="color">Color of the drawing.</param>
        protected void DrawOBB(OBB obb, Color color)
        {
            Gizmos.color = color;
            switch (shape) {
                case Shape.Box:
                    DrawBox(obb.bounds.center, obb.bounds.size, obb.orientation, transform);
                    break;
                case Shape.Capsule:
                    DrawCapsule(obb.bounds.center, obb.bounds.size, obb.orientation, transform);
                    break;
                case Shape.Sphere:
                    DrawSphere(obb.bounds.center, obb.bounds.size, obb.orientation, transform);
                    break;
                case Shape.Mesh:
                    DrawMesh(transform, obb.triangles);
                    break;
            }

        }

        /// <summary>
        /// Draw an OBB if we are at the good recursion level, starting from <c>_node</c>.
        /// </summary>
        /// <param name="draw_level">The recursion level to draw</param>.
        void Draw(int draw_level)
        {
            Draw(node, draw_level, 0);
        }

        /// <summary>
        /// Draw an OBB if we are at the good recursion level.
        /// </summary>
        /// <param name="node">Current proceeded node.</param>
        /// <param name="draw_level">Recursion level to reach for drawing.</param>
        /// <param name="current_level">Current recursion level.</param>
        void Draw(OBBTreeNode node, int draw_level, int current_level)
        {
            if (node == null || node.id == null || node.children == null)
                return;

            if (current_level == draw_level || node.children.Length == 0) {
                // Skip nodes with uninitialized OBBs (e.g. from serialization)
                if (node.obb.triangles == null)
                    return;
                Color drawColor = previewSolidColor;
                if (previewColor == CollidersPreviewColor.Random || previewColor == CollidersPreviewColor.RandomFill) {
                    // Golden ratio spacing produces maximally distinct hues
                    float hue = (node.obb.triangles.Length * 0.618034f) % 1f;
                    drawColor = Color.HSVToRGB(hue, 0.75f, 0.9f);
                }
                DrawOBB(node.obb, drawColor);
            }
            else
            {
                foreach (OBBTreeNode child in node.children)
                {
                    Draw(child, draw_level, current_level + 1);
                }
            }
        }
    }
}
