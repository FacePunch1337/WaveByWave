/** 
 * \file
 * \brief Abstract content for UColliders
 */
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using UColliders.OBBTree;

/// <summary>
/// Base namespace of the UniversalColliders project.
///
/// All the scripts related to the Universal Colliders
/// project should be in this namespace.
/// </summary>
namespace UColliders {
    
    /// <summary>
    /// The different shapes an OBB can be represented with.
    /// </summary>
    public enum Shape {
        /// <summary>
        /// Use boxes for OBBs, this is the native shape.
        /// </summary>
        Box, 
        /// <summary>
        /// Use a capsule, it is a good compromise 
        /// between collision and shape precision. 
        /// </summary>
        Capsule, 
        /// <summary>
        /// Use a sphere, it optimzes collision detection.
        /// </summary>
        Sphere, 
        /// <summary>
        /// Use a convex mesh. It is slower, and rarely recommended.
        /// </summary>
        Mesh 
    };


    /// <summary>
    /// The <c>UCollidersNode</c> provides a basic framework for 
    /// other OBB related classes.
    /// </summary>
    public abstract class UCollidersNode : MonoBehaviour
    {
        /// <summary>
        /// The parent script that contains the root node.
        /// </summary>
        public UCollidersRoot rootNode;

        /// <summary>
        /// The <c>OBBTreeNode</c> associated.
        /// Not serialized — the tree is rebuilt at runtime via <see cref="UCollidersRoot.InitOBB"/>.
        /// </summary>
        [System.NonSerialized]
        public OBBTreeNode node;
        
        /// <summary>
        /// Shape of this OBB, not always a box.
        /// </summary>
        [SerializeField]
        [FormerlySerializedAs("shape")]
        [Tooltip("Change the collider's shape.")]
        private Shape _shape;

        /// <summary>
        /// Shape of the collider for this OBB node.
        /// </summary>
        public Shape shape {
            get => _shape;
            set => _shape = value;
        }

        /// <summary>
        /// Tendency of this collider to wrap around the OBB.
        /// 0 means the collider is inside the OBB.
        /// 1 means the collider encloses the OBB entirely.
        /// Only used with sphere or capsule colliders.
        /// </summary>
        [SerializeField]
        [FormerlySerializedAs("boundicity")]
        [Range(0, 1)]
        [Tooltip(@"Tendency of this collider to wrap around the OBB.
        - 0 means the collider is inside the OBB.
        - 1 means the collider encloses the OBB entirely.")]
        private float _boundicity;

        /// <summary>
        /// Tendency of this collider to wrap around the OBB (0 = inside, 1 = encloses).
        /// Clamped to [0, 1]. Only used with sphere or capsule colliders.
        /// </summary>
        public float boundicity {
            get => _boundicity;
            set => _boundicity = Mathf.Clamp01(value);
        }


        /// <summary>
        /// Vertices enclosed by this node, root's mesh space and ordering.
        /// This is a subset of <c>rootNode.vertices</c>.
        /// </summary>
        [Tooltip("The oriented bounding box contains those vertices.")]
        protected Vector3[] _vertices;

        /// <summary>
        /// Submesh associated with this node.
        /// 
        /// It is a submesh of <c>rootNode</c>'s <c>mesh</c>,
        /// and can be <c>rootNode</c>'s <c>mesh</c> itself.        
        /// </summary>
        [Tooltip("Submesh associated with this node.")]
        public Mesh mesh;

        /// <summary>
        /// Compute a new mesh based on provided triangles.
        ///
        /// <c>rootNode.mesh.vertices</c> are used as vertices.
        /// </summary>
        /// <param name="triangles">The triangles that define the submesh.</param>
        /// <returns>The computed mesh, should be the same as <c>mesh</c>.</returns>
        /// <remarks>Code adapted from https://answers.unity.com/questions/947930/create-a-mesh-from-a-sub-mesh.html </remarks>
        public Mesh ComputeMesh(int[] triangles) {
            return ComputeMesh(triangles, rootNode.vertices ?? rootNode.mesh.vertices);
        }


        /// <summary>
        /// Compute a new mesh based on provided triangles.
        /// </summary>
        /// <param name="triangles">The triangles that define the submesh.</param>
        /// <param name="vertices">The vertices mapped by the <c>triangles</c></param>
        /// <returns>The computed mesh, should be the same as <c>mesh</c>.</returns>
        /// <remarks>Code adapted from https://answers.unity.com/questions/947930/create-a-mesh-from-a-sub-mesh.html </remarks>
        public Mesh ComputeMesh(int[] triangles, Vector3[] vertices) {
            if (triangles == null || triangles.Length == 0)
                throw new System.ArgumentException("Triangles array must not be null or empty.", nameof(triangles));
            if (vertices == null || vertices.Length == 0)
                throw new System.ArgumentException("Vertices array must not be null or empty.", nameof(vertices));
            Mesh newMesh = new Mesh();
            int[] newTriangles = new int[triangles.Length];
            int count = 0;
            int i, vertex_index;
            Dictionary<int, int> vertices_map = new Dictionary<int, int>();
            for (i = 0; i < triangles.Length; i++){
                vertex_index = triangles[i];
                if (!vertices_map.ContainsKey(vertex_index)) {
                    vertices_map.Add(vertex_index, count);
                    // Small shortcut for less computations
                    newTriangles[i] = count;
                    count++;
                } else {
                    newTriangles[i] = vertices_map[triangles[i]];
                }
            }

            Vector3[] newVerts = new Vector3[count];
            // Prepare the local transformations
            Matrix4x4 matrix = transform.worldToLocalMatrix * rootNode.transform.localToWorldMatrix;
            foreach (KeyValuePair<int, int> pair in vertices_map) {
                newVerts[pair.Value] = matrix.MultiplyPoint(vertices[pair.Key]);
            }

            newMesh.vertices = newVerts;
            newMesh.triangles = newTriangles;
            newMesh.uv = new Vector2[newVerts.Length];
            //newMesh.RecalculateNormals();
            return newMesh;
        }
    }
}
