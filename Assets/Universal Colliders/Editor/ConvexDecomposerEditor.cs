/** \file
    \brief Custom inspector for ConvexDecomposer.
*/
using System;
using System.Text;
using UnityEngine;
using UnityEditor;
using UColliders.CoACD;

namespace UColliders.EditorScripts {

    /// <summary>
    /// Custom editor for the <see cref="ConvexDecomposer"/> component.
    /// </summary>
    [CustomEditor(typeof(ConvexDecomposer))]
    [CanEditMultipleObjects]
    public class ConvexDecomposerEditor : Editor
    {
        SerializedProperty coacdParameters;
        SerializedProperty previewColor, previewSolidColor;
        SerializedProperty includeChildrenMeshes;

        private ConvexDecomposer decomposer;

        static bool showStatistics;

        void OnEnable()
        {
            coacdParameters = serializedObject.FindProperty("_coacdParameters");
            previewColor = serializedObject.FindProperty("previewColor");
            previewSolidColor = serializedObject.FindProperty("previewSolidColor");
            includeChildrenMeshes = serializedObject.FindProperty("includeChildrenMeshes");
            decomposer = (ConvexDecomposer) target;
        }

        /// <summary>
        /// Draw CoACD parameters with user-friendly labels grouped by purpose.
        /// </summary>
        void DrawCoACDParameters()
        {
            coacdParameters.isExpanded = EditorGUILayout.Foldout(coacdParameters.isExpanded,
                "Convex Decomposition Settings");
            if (!coacdParameters.isExpanded) return;

            EditorGUI.indentLevel++;

            // --- Quality ---
            EditorGUILayout.LabelField("Quality", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(
                coacdParameters.FindPropertyRelative("threshold"),
                new GUIContent("Precision Threshold",
                    "How closely colliders follow the mesh. Lower = tighter fit but more pieces and slower. " +
                    "Recommended: 0.05 (precise) to 0.2 (fast)."));
            EditorGUILayout.PropertyField(
                coacdParameters.FindPropertyRelative("maxConvexHull"),
                new GUIContent("Max Collider Count",
                    "Maximum number of collider pieces. -1 = no limit."));
            EditorGUILayout.PropertyField(
                coacdParameters.FindPropertyRelative("merge"),
                new GUIContent("Merge Similar Pieces",
                    "Merge similar adjacent pieces after decomposition to reduce collider count."));

            // --- Performance ---
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Performance", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(
                coacdParameters.FindPropertyRelative("sampleResolution"),
                new GUIContent("Quality Samples",
                    "Number of surface sample points for evaluating fit quality. " +
                    "Higher = more accurate but slower. Recommended: 500-2000."));
            EditorGUILayout.PropertyField(
                coacdParameters.FindPropertyRelative("mctsNodes"),
                new GUIContent("Cut Candidates",
                    "Number of candidate cutting positions tested per split. " +
                    "Higher = better cuts but slower. Recommended: 10-20."));
            EditorGUILayout.PropertyField(
                coacdParameters.FindPropertyRelative("mctsIteration"),
                new GUIContent("Search Iterations",
                    "How many times the algorithm explores different cuts per split. " +
                    "Higher = better results but slower. Recommended: 50 (fast) to 200 (high quality)."));
            EditorGUILayout.PropertyField(
                coacdParameters.FindPropertyRelative("mctsMaxDepth"),
                new GUIContent("Search Depth",
                    "How many cuts ahead the algorithm plans. " +
                    "Higher = smarter cuts but exponentially slower. Recommended: 2-3."));

            // --- Advanced ---
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Advanced", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(
                coacdParameters.FindPropertyRelative("pca"),
                new GUIContent("Align to Principal Axes",
                    "Rotate the mesh to align with its longest dimensions before splitting. " +
                    "Can improve results for elongated or rotated meshes."));
            EditorGUILayout.PropertyField(
                coacdParameters.FindPropertyRelative("seed"),
                new GUIContent("Random Seed",
                    "Fixed seed for reproducible results. 0 = default."));

            // Mesh Repair settings
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Mesh Repair", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(
                coacdParameters.FindPropertyRelative("preprocessMode"),
                new GUIContent("Repair Mode",
                    "Auto: check if mesh is manifold, repair if needed.\n" +
                    "On: always repair (use if you see broken colliders).\n" +
                    "Off: skip repair (faster, but may fail on non-manifold meshes)."));
            EditorGUILayout.PropertyField(
                coacdParameters.FindPropertyRelative("preprocessResolution"),
                new GUIContent("Repair Quality",
                    "Voxel grid resolution for mesh repair (20-100).\n" +
                    "Higher = better detail preservation but slower.\n" +
                    "Lower = faster but may lose thin features."));

            EditorGUI.indentLevel--;
        }

        static void DrawSectionHeader(string title)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        void DrawMeshSource()
        {
            DrawSectionHeader("Mesh Source");
            EditorGUILayout.PropertyField(includeChildrenMeshes,
                new GUIContent("Include Child Meshes",
                    "Combine meshes from all child GameObjects into a single decomposition."));
        }

        void DrawPreviewSettings()
        {
            DrawSectionHeader("Preview");
            EditorGUILayout.PropertyField(previewColor,
                new GUIContent("Gizmo Display",
                    "How collider hulls are shown in the Scene view."));
            if (previewColor.enumNames[previewColor.enumValueIndex] == "Solid"
                || previewColor.enumNames[previewColor.enumValueIndex] == "Fill")
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(previewSolidColor,
                    new GUIContent("Gizmo Color", "The color used for the collider preview gizmos."));
                EditorGUI.indentLevel--;
            }
        }

        void DrawStatistics()
        {
            showStatistics = EditorGUILayout.Foldout(showStatistics, "Statistics");
            if (!showStatistics) return;

            EditorGUI.indentLevel++;
            int hullCount = decomposer.CountHulls();
            if (hullCount > 0) {
                StringBuilder sb = new StringBuilder();
                sb.Append("Statistics:");
                sb.AppendLine();
                sb.Append($"  - {hullCount} convex hull{(hullCount != 1 ? "s" : "")}");
                EditorGUILayout.HelpBox(sb.ToString(), MessageType.Info);
            } else {
                EditorGUILayout.HelpBox("No collider data generated yet.", MessageType.Info);
            }
            EditorGUI.indentLevel--;
        }

        void DrawActions()
        {
            DrawSectionHeader("Actions");

            if (GUILayout.Button("Regenerate Colliders")) {
                foreach (UnityEngine.Object obj in targets) {
                    if (obj is ConvexDecomposer cd) {
                        CoACDWrapper.Cancelled = false;
                        CoACDWrapper.OnProgress = (msg, p) => {
                            if (EditorUtility.DisplayCancelableProgressBar(
                                "CoACD Decomposition", msg, p))
                                CoACDWrapper.Cancelled = true;
                        };
                        try {
                            cd.RegenerateColliders();
                        } catch (OperationCanceledException) {
                            Debug.Log("CoACD decomposition cancelled.");
                        } finally {
                            CoACDWrapper.OnProgress = null;
                            CoACDWrapper.Cancelled = false;
                            EditorUtility.ClearProgressBar();
                        }
                    }
                }
            }
            if (GUILayout.Button("Unpack Colliders")) {
                int count = 0;
                foreach (UnityEngine.Object obj in targets) {
                    if (obj is ConvexDecomposer cd)
                        count += cd.UnpackColliders();
                }
                Debug.Log("Unpacked " + count + " convex hull colliders!");
            }
            if (GUILayout.Button("Delete Colliders and Data")) {
                foreach (UnityEngine.Object obj in targets) {
                    if (obj is ConvexDecomposer cd)
                        cd.DeleteCollidersAndData();
                }
            }
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawCoACDParameters();
            DrawMeshSource();
            DrawPreviewSettings();
            DrawStatistics();

            serializedObject.ApplyModifiedProperties();

            DrawActions();
        }
    }
}
