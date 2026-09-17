/** \file
    \brief Various utility to manipulate UCollidersLeaf in the Unity Editor
*/
using System.Text;
using UnityEngine;
using UnityEditor;

namespace UColliders.EditorScripts {

    /// <summary>
    /// The <c>UCollidersLeafEditor</c> class handles the display
    /// of the <c>UCollidersLeaf</c> objects in the editor window.
    /// </summary>
    [CustomEditor(typeof(UCollidersLeaf))]
    [CanEditMultipleObjects]
    public class UCollidersLeafEditor : UCollidersNodeEditor
    {
        SerializedProperty preserveUCollider;

        private UCollidersLeaf uCollider;

        public override string GetStatistics()
        {
            StringBuilder statistics = new StringBuilder();
            statistics.Append("Statistics for this leaf node:");
            if (uCollider.mesh == null)
                return statistics.ToString();
            AddStatisticsRow(statistics, uCollider.mesh.vertices.Length, "vertex", "vertices");
            AddStatisticsRow(statistics, uCollider.mesh.triangles.Length, "triangle");
            return statistics.ToString();
        }

        void OnEnable() {
            preserveUCollider = serializedObject.FindProperty("preserveUCollider");
            shape = serializedObject.FindProperty("_shape");
            boundicity = serializedObject.FindProperty("_boundicity");
            uCollider = (UCollidersLeaf) target;
        }

        /// <summary>
        /// Display the UCollidersLeaf component on the Editor
        /// and wait for interactions.
        /// </summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(preserveUCollider,
                new GUIContent("Preserve on Regenerate",
                    "Keep this leaf collider when the parent regenerates.\n" +
                    "Useful for manually tweaked colliders you don't want overwritten."));

            EditorGUILayout.PropertyField(shape,
                new GUIContent("Collider Shape",
                    "Override the primitive shape for this collider piece.\n" +
                    "Box is the most accurate. Sphere and Capsule are faster at runtime."));
            if (shape.enumNames[shape.enumValueIndex] == "Sphere"
                || shape.enumNames[shape.enumValueIndex] == "Capsule") {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(boundicity, new GUIContent("Enclosure",
                    "How tightly the collider wraps around the bounding box.\n" +
                    "0 = inscribed (fits inside the box, tighter).\n" +
                    "1 = circumscribed (wraps around the box, looser)."));
                EditorGUILayout.HelpBox(
                    "The preview shows boxes, but the actual colliders use the selected shape. " +
                    "Unity does not support deformed sphere or capsule gizmos.",
                    MessageType.Warning);
                EditorGUI.indentLevel--;
            }

            // Node ID (read-only)
            if (uCollider.nodeId != null) {
                GUI.enabled = false;
                EditorGUILayout.TextField("Node ID", uCollider.nodeId);
                GUI.enabled = true;
            }

            // Statistics
            if (uCollider != null && uCollider.mesh != null) {
                EditorGUILayout.HelpBox(GetStatistics(), MessageType.Info);
            } else {
                EditorGUILayout.HelpBox("No mesh data available.", MessageType.Info);
            }

            // Subdivide action
            if (GUILayout.Button("Subdivide")) {
                foreach (UnityEngine.Object obj in targets) {
                    if (obj is UCollidersLeaf) {
                        (obj as UCollidersLeaf).Subdivide();
                    }
                }
            }
            serializedObject.ApplyModifiedProperties();
        }
    }
}
