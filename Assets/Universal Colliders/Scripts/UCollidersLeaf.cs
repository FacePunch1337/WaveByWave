/** \file
    \brief Dislay and manipulation of UCollidersLeaf in the Unity Editor
*/
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using UColliders.OBBTree;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UColliders {

    /// <summary>
    /// A <c>UCollidersLeaf</c> makes the link between an actual GameObject and an <c>OBBTreeNode</c>.
    /// </summary>
    /// <see cref="OBBTreeNode"/>
    [ExecuteAlways]
    public class UCollidersLeaf : UCollidersNode
    {

        /// <summary>
        /// Prevent gameObject from automatic deletion.
        /// </summary>
        [Tooltip("Prevent this UCollider to be automatically deleted.")]
        public bool preserveUCollider;

        /// <summary>
        /// Triangles linking the enclosed vertices, 
        /// a subset of of the PARENT mesh vertices.
        /// </summary>
        /// <see cref="SetTriangles"/>
        [Tooltip("Triangles in the submesh encapsulated by this OBB.")]
        private int[] _triangles;

        /// <summary>
        /// ID used by this UColliderLeaf in his unique position in the OBBTree.
        ///
        /// Usually equal to <c>node.id</c>, is uses less memory than saving 
        /// <c>_triangles</c>, but forces to recompute all colliders.
        /// </summary>
        /// <seealso cref="UCollidersRoot.encapsulationPath">
        /// Global path for each level
        /// </seealso>
        [ReadOnly]
        [Tooltip("The unique ID of this node in the OBBTree.")]
        public string nodeId;

        /// <summary>
        /// Check data integrity. If necessary, regenerate <c>mesh</c> and <c>_vertices</c>.
        /// </summary>
        public void CheckAndRebuild() {
            if (nodeId == null)
                return;
            if (node == null) {
                if (rootNode == null)
                    return;
                node = rootNode.GetOBBAtPath(nodeId);
            }
            if (node == null)
                return;
            _triangles = node.obb.triangles;
            if (mesh != null) {
                if (_vertices == null)
                    RegenerateVertices();
            } else {
                if (nodeId == null) {
                    return;
                }
                if (rootNode != null) {
                    RegenerateVertices();
                    // Local data
                    mesh = ComputeMesh(_triangles);
                }
            }
        }

        /// <summary>
        /// Initializes data.
        /// </summary>
        void Awake() {
            rootNode = GetComponentInParent<UCollidersRoot>();
            // This happens when UCollidersLeaf are instanciated, but not in hierarchy
            if (rootNode == null)
                return;
            // Avoid inversion in execution order
            if (rootNode.node == null)
                rootNode.InitOBB();
            CheckAndRebuild();
            if (mesh != null && node == null && nodeId != null) {
                node = rootNode.GetOBBAtPath(nodeId);
            }
        }

        /// <summary>
        /// Update the transform to match the current OBB position, orientation, and size.
        /// Lightweight alternative to <see cref="RegenerateCollider"/> for per-frame skinned mesh updates.
        /// </summary>
        public void UpdateTransform() {
            if (node == null)
                return;
            transform.localPosition = node.obb.orientation * node.obb.bounds.center;
            transform.localRotation = node.obb.orientation;
            transform.localScale = node.obb.bounds.size;
        }

        /// <summary>
        /// Destroy previously generated collider and add a new one based on <c>shape</c>.
        /// </summary>
        public void RegenerateCollider() {
            if (node == null) {
                Debug.LogWarning("Cannot regenerate collider: node is null.", this);
                return;
            }
            transform.localPosition = node.obb.orientation * node.obb.bounds.center;
            transform.localRotation = node.obb.orientation;
            transform.localScale = node.obb.bounds.size;
            Collider existing = GetComponent<Collider>();
            if (existing != null) {
#if UNITY_EDITOR
                DestroyImmediate(existing);
#else
                Destroy(existing);
#endif
            }
            switch (shape) {
                case Shape.Box:
                    BoxCollider box = gameObject.AddComponent<BoxCollider>();
                    box.size = Vector3.one;
                    break;
                case Shape.Sphere:
                    SphereCollider sphere = gameObject.AddComponent<SphereCollider>();
                    sphere.radius = Mathf.Lerp(1, Mathf.Sqrt(3), boundicity) / 2;
                    break;
                case Shape.Capsule:
                    CapsuleCollider capsule = gameObject.AddComponent<CapsuleCollider>();
                    capsule.direction = node.obb.axesOrder[0];
                    capsule.height = 1;
                    capsule.radius = Mathf.Lerp(1, Mathf.Sqrt(3), boundicity) / 2;
                    break;
                case Shape.Mesh:
                    MeshCollider meshCollider = gameObject.AddComponent<MeshCollider>();
                    meshCollider.name = name;
                    // Due to the transform, we have to recompute the mesh
                    mesh = ComputeMesh(_triangles);
                    meshCollider.sharedMesh = mesh;
                    meshCollider.convex = true;
                    break;
            }
        }

        void Update() {
#if UNITY_EDITOR
            if (Selection.Contains(gameObject) && transform.hasChanged) {
                Undo.RecordObject(this, "Set preserveUCollider True");
                preserveUCollider = true;
                transform.hasChanged = false;
            }
#endif
            switch (shape) {
                case Shape.Box:
                    if (TryGetComponent<BoxCollider>(out BoxCollider _))
                        return;
                    break;
                case Shape.Sphere:
                    if (TryGetComponent<SphereCollider>(out SphereCollider _))
                        return;
                    break;
                case Shape.Capsule:
                    if (TryGetComponent<CapsuleCollider>(out CapsuleCollider _))
                        return;
                    break;
                case Shape.Mesh:
                    if (TryGetComponent<MeshCollider>(out MeshCollider _2))
                        return;
                    break;
            }
            if (node == null || _triangles == null)
                return;
            RegenerateCollider();
        }

        /// <summary>
        /// Regenerate vertices based on parent's <c>mesh</c> and <c>triangles</c>.
        /// </summary>
        public void RegenerateVertices() {
            if (rootNode == null || rootNode.mesh == null) {
                Debug.LogWarning("Cannot regenerate vertices: rootNode or mesh is null.", this);
                return;
            }
            if (_triangles == null || _triangles.Length == 0)
                return;
            List<Vector3> nVertices = new List<Vector3>();
            Vector3[] allVertices = rootNode.vertices ?? rootNode.mesh.vertices;
            foreach (int triangle in _triangles) {
                nVertices.Add(allVertices[triangle]);
            }
            _vertices = nVertices.ToArray();
        }

        /// <summary>
        /// Sets <c>triangles</c>.
        /// </summary>
        /// <param name="triangles"></param>
        public void SetTriangles(int[] triangles) {
            this._triangles = triangles;
        }

        /// <summary>
        /// Regenerate OBB if necessary
        /// </summary>
        void CheckOBB() {
            if (nodeId == null) {
                if (mesh == null) {
                    Debug.LogError("Cannot compute OBB: mesh is null.", this);
                    node = null;
                    return;
                }
                node = new OBBTreeNode("");
                node.obb = node.obb.BuildOBB(mesh.vertices, mesh.triangles);
            } else {
                if (rootNode == null) {
                    Debug.LogError("Cannot compute OBB: rootNode is null.", this);
                    node = null;
                    return;
                }
                node = rootNode.GetOBBAtPath(nodeId);
            }
        }

        /// <summary>
        /// This methods subdivides the current <c>gameObject</c> in two children GameObject.
        /// Please make sure that the subdivision is possible before proceeding.
        /// </summary>
        /// <see cref="Subdivide"/>
        public void SubdivideGameObject() {
            GameObject[] newGameObjects = new GameObject[2];
            for (int i = 0; i < 2; i++) {
                newGameObjects[i] = new GameObject(
                    "UCollider", typeof(UCollidersLeaf)
                );
#if UNITY_EDITOR
                Undo.RegisterCreatedObjectUndo(newGameObjects[i], "Created UCollidersLeaf Object");
#endif
                newGameObjects[i].transform.SetParent(transform.parent);
                UCollidersLeaf obbComponent = newGameObjects[i].GetComponent<UCollidersLeaf>();
                obbComponent.rootNode = rootNode;
                obbComponent.SetTriangles(node.children[i].obb.triangles);
                obbComponent.node = node.children[i];
                obbComponent.nodeId = node.children[i].id;
                obbComponent.shape = shape;
                obbComponent.boundicity = boundicity;
                obbComponent.CheckAndRebuild();
                obbComponent.RegenerateCollider();
            }
#if UNITY_EDITOR
            // Extend selection to newly created OBBs
            Object[] newSelection = new Object[Selection.objects.Length + newGameObjects.Length - 1];
            // Do not select the current GameObject that will get deleted
            int shifter = 0;
            for (int i = 0; i < Selection.objects.Length; i++) {
                if ((Selection.objects[i] as GameObject) == gameObject) {
                    shifter = 1;
                    continue;
                }
                newSelection[i - shifter] = Selection.objects[i];
            }
            newGameObjects.CopyTo(newSelection, newSelection.Length - 2);
            Selection.objects = newSelection;
            // Delay destruction to avoid "Serialized target has been destroyed" errors
            EditorApplication.delayCall += () => {
                if (gameObject != null)
                    Undo.DestroyObjectImmediate(gameObject);
            };
#else
            Destroy(gameObject);
#endif
        }

        /// <summary>
        /// The <c>Subdivide</c> method allows to subdivide an OBB in two children if possible. It does security checks before proceeding.
        /// </summary>
        /// <see cref="SubdivideGameObject"/>
        public void Subdivide() {
            CheckOBB();
            if (node == null) {
                Debug.LogError("Cannot subdivide: OBB node could not be computed.");
                return;
            }
            if (node.children.Length == 0) {
                Debug.LogError("This UColliderNode cannot be subdivided.");
                return;
            }
            if (node.children[0] == null)
                node.children = node.obb.ComputeChildren(
                    ref _vertices, node.id
                );
            if (node.children.Length == 0)
            {
                Debug.LogError("This UColliderNode cannot be subdivided.");
                return;
            }
            SubdivideGameObject();
        }

        /// <summary>
        /// Convert this UCollider in a standard collider.
        /// Formaly: delete the UCollidersLeaf component.
        /// </summary>
        public void Unpack() {
#if UNITY_EDITOR
            Undo.DestroyObjectImmediate(this);
#else
            Destroy(this);
#endif
        }
    }
}
