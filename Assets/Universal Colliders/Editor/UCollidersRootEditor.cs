/** \file
    \brief Various utility to manipulate UCollidersRoot in the Unity Editor
*/
using System;
using System.Text;
using UnityEngine;
using UnityEditor;
namespace UColliders.EditorScripts {

    /// <summary>
    /// Modifies the way UCollidersRoot display in the editor.
    /// It does not only provides basic serialized fields, but also
    /// interaction button and verious informations.
    /// </summary>
    [CustomEditor(typeof(UCollidersRoot))]
    [CanEditMultipleObjects]
    public class UCollidersRootEditor : UCollidersNodeEditor
    {
        /// <summary>
        /// mesh property of the UColliderRoot
        /// </summary>
        SerializedProperty mesh;

        SerializedProperty recursionLevel,
        previewColor, previewSolidColor,
        includeChildrenMeshes,
        optimizeMesh,
        encapsulateTriangles, encapsulationPath;

        /// <summary>
        /// UColliderRoot component of the selected GameObject.
        /// </summary>
        private UCollidersRoot rootNode;

        /// <summary>
        /// Control the opening/closing of the statistics foldout.
        /// </summary>
        protected static bool showStatistics;

        /// <summary>
        /// Whether OBB-specific settings changed this frame.
        /// </summary>
        private bool _obbSettingsChanged;
        private bool _settingsNeedRegeneration;

        /// <summary>
        /// Compute and return various useful data.
        /// </summary>
        /// <returns>Bullet points of data.</returns>
        public override string GetStatistics() {
            StringBuilder statistics = new StringBuilder();
            statistics.Append("Statistics for those settings:");
            if (rootNode.node != null) {
                int leafCount = rootNode.CountLeaves();
                int maxLeaves = rootNode.recursionLevel <= 30
                    ? (int)Mathf.Pow(2, rootNode.recursionLevel)
                    : int.MaxValue;
                AddStatisticsRow(statistics, leafCount, "collider");
                AddStatisticsRow(statistics, maxLeaves - leafCount, "collider");
                statistics.Append(" can't be computed");
            }
            if (rootNode.mesh != null && rootNode.mesh.vertices != null)
                AddStatisticsRow(statistics, rootNode.mesh.vertices.Length, "vertex", "vertices");
            if (rootNode.node != null && rootNode.node.obb.triangles != null)
                AddStatisticsRow(statistics, rootNode.node.obb.triangles.Length, "triangle");
            return statistics.ToString();
        }

        void OnEnable()
        {
            recursionLevel = serializedObject.FindProperty("_recursionLevel");
            mesh = serializedObject.FindProperty("mesh");
            previewColor = serializedObject.FindProperty("previewColor");
            previewSolidColor = serializedObject.FindProperty("previewSolidColor");
            includeChildrenMeshes = serializedObject.FindProperty("includeChildrenMeshes");
            shape = serializedObject.FindProperty("_shape");
            boundicity = serializedObject.FindProperty("_boundicity");
            optimizeMesh = serializedObject.FindProperty("optimizeMesh");
            encapsulateTriangles = serializedObject.FindProperty("encapsulateTriangles");
            encapsulationPath = serializedObject.FindProperty("encapsulationPath");
            rootNode = (UCollidersRoot) target;
        }

        /// <summary>
        /// Draw a section header with bold label and a separator line.
        /// </summary>
        private static void DrawSectionHeader(string title)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        /// <summary>
        /// Draw OBB subdivision settings with user-friendly labels.
        /// </summary>
        private void DrawOBBSettings()
        {
            DrawSectionHeader("Subdivision");

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.IntSlider(recursionLevel, -1, UCollidersRoot.MaxRecursionDepth,
                new GUIContent("Detail Level",
                    "Maximum number of split levels. Board-like sections stop automatically as soon as one box fits them.\n" +
                    "-1 = disabled (no colliders).\n" +
                    "Higher values allow complex bends and cavities to be refined further."));

            EditorGUILayout.PropertyField(shape,
                new GUIContent("Collider Shape",
                    "The primitive shape used for each collider piece.\n" +
                    "Box is the most accurate. Sphere and Capsule are faster at runtime."));

            if (shape.enumNames[shape.enumValueIndex] == "Sphere"
                || shape.enumNames[shape.enumValueIndex] == "Capsule")
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(boundicity, new GUIContent("Enclosure",
                    "How tightly the collider wraps around the bounding box.\n" +
                    "0 = inscribed (fits inside the box, tighter).\n" +
                    "1 = circumscribed (wraps around the box, looser)."));
                EditorGUILayout.HelpBox(
                    "The actual colliders use the selected shape. " +
                    "Unity does not support deformed sphere or capsule gizmos.",
                    MessageType.Warning);
                EditorGUI.indentLevel--;
            }

            DrawSectionHeader("Mesh Processing");

            EditorGUILayout.PropertyField(optimizeMesh,
                new GUIContent("Balance Vertices",
                    "Subdivide long edges so vertices are more evenly distributed.\n" +
                    "Improves collider placement on meshes with sparse geometry."));
            EditorGUILayout.PropertyField(encapsulateTriangles,
                new GUIContent("Fill Gaps",
                    "Include triangle surfaces (not just vertices) when computing collider bounds.\n" +
                    "Prevents holes between colliders, but uses larger colliders."));

            _obbSettingsChanged = EditorGUI.EndChangeCheck();
        }

        /// <summary>
        /// Draw mesh source fields.
        /// </summary>
        private void DrawMeshSource()
        {
            DrawSectionHeader("Mesh Source");

            EditorGUILayout.PropertyField(mesh,
                new GUIContent("Mesh",
                    "The mesh to generate colliders for. " +
                    "Auto-detected from MeshFilter or SkinnedMeshRenderer if left empty."));
            EditorGUILayout.PropertyField(includeChildrenMeshes,
                new GUIContent("Include Child Meshes",
                    "Combine meshes from all child GameObjects into a single collider hierarchy."));
        }

        /// <summary>
        /// Draw preview/display settings.
        /// </summary>
        private void DrawPreviewSettings()
        {
            DrawSectionHeader("Preview");

            EditorGUILayout.PropertyField(previewColor,
                new GUIContent("Gizmo Display",
                    "How collider bounds are shown in the Scene view.\n" +
                    "None = hidden.\n" +
                    "Solid/Fill = single color.\n" +
                    "Random/RandomFill = each collider gets a unique color."));
            if (previewColor.enumNames[previewColor.enumValueIndex] == "Solid"
                || previewColor.enumNames[previewColor.enumValueIndex] == "Fill")
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(previewSolidColor,
                    new GUIContent("Gizmo Color", "The color used for the collider preview gizmos."));
                EditorGUI.indentLevel--;
            }
        }

        /// <summary>
        /// Draw statistics foldout with debug info.
        /// </summary>
        private void DrawStatistics()
        {
            showStatistics = EditorGUILayout.Foldout(showStatistics, "Statistics");
            if (!showStatistics) return;

            EditorGUI.indentLevel++;

            GUI.enabled = false;
            EditorGUILayout.PropertyField(encapsulationPath,
                new GUIContent("Subdivision Path",
                    "Internal: the sequence of split modes used at each recursion level.\n" +
                    "0 = vertex-only split, 1 = triangle-clipping split."));
            GUI.enabled = true;
            if (rootNode != null && rootNode.node != null) {
                EditorGUILayout.HelpBox(GetStatistics(), MessageType.Info);
            } else {
                EditorGUILayout.HelpBox("No collider data generated yet.", MessageType.Info);
            }

            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// Draw action buttons.
        /// </summary>
        private void DrawActions()
        {
            DrawSectionHeader("Actions");

            if (_settingsNeedRegeneration)
                EditorGUILayout.HelpBox(
                    "Settings changed. Generation is waiting so dragging a slider cannot start several expensive rebuilds.",
                    MessageType.Info);

            if (GUILayout.Button("Generate / Regenerate Colliders", GUILayout.Height(30f))) {
                try {
                    EditorUtility.DisplayProgressBar(
                        "Universal Colliders",
                        "Building optimized OBB tree...",
                        0.35f);
                    foreach (UnityEngine.Object obj in targets) {
                        if (obj is UCollidersRoot root)
                            root.RegenerateColliders();
                    }
                    _settingsNeedRegeneration = false;
                }
                finally {
                    EditorUtility.ClearProgressBar();
                }
            }
            if (GUILayout.Button("Unpack Colliders")) {
                int count = 0;
                foreach (UnityEngine.Object obj in targets) {
                    if (obj is UCollidersRoot)
                        count += ((UCollidersRoot)obj).UnpackColliders();
                }
                Debug.Log("Unpacked " + count + " UColliderLeaf components!");
            }
            if (GUILayout.Button("Delete Colliders and Data")) {
                foreach (UnityEngine.Object obj in targets) {
                    if (obj is UCollidersRoot) {
                        ((UCollidersRoot)obj).DeleteCollidersAndData();
                    }
                }
            }
        }

        /// <summary>
        /// Display the UCollidersRoot component on the Editor
        /// and wait for interactions.
        /// </summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawOBBSettings();
            DrawMeshSource();
            DrawPreviewSettings();
            DrawStatistics();

            // Apply property changes before checking for auto-regeneration
            serializedObject.ApplyModifiedProperties();

            // Defer expensive work until the explicit button is pressed. Auto rebuilding
            // once for every intermediate value while dragging Detail Level was a major
            // source of multi-minute editor stalls.
            if (_obbSettingsChanged)
                _settingsNeedRegeneration = true;

            // --- Action buttons ---
            DrawActions();
        }
    }
}
