/** \file
    \brief Editor window for batch collider generation on multiple GameObjects.
*/
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UColliders.CoACD;

namespace UColliders.EditorScripts {

    /// <summary>
    /// Editor window that generates Universal Colliders on multiple selected GameObjects at once.
    /// Accessible via GameObject > Universal Colliders menu.
    /// </summary>
    public class BatchGeneratorWindow : EditorWindow
    {
        /// <summary>
        /// Which collider generation approach to use.
        /// </summary>
        enum ColliderType { OBBTree, ConvexDecomposition }

        int recursionLevel = 5;
        Shape shape = Shape.Box;
        ColliderType colliderType = ColliderType.OBBTree;
        bool includeChildren = true;
        bool skipExisting = true;

        /// <summary>
        /// Opens the Batch Generator window.
        /// </summary>
        [MenuItem("GameObject/Universal Colliders/Generate for Selection", false, 30)]
        static void ShowWindow()
        {
            var window = GetWindow<BatchGeneratorWindow>("Batch Colliders");
            window.minSize = new Vector2(320, 260);
            window.Show();
        }

        /// <summary>
        /// Removes all UCollidersRoot and ConvexDecomposer components and their generated colliders from the selection.
        /// </summary>
        [MenuItem("GameObject/Universal Colliders/Remove from Selection", false, 31)]
        static void RemoveFromSelection()
        {
            GameObject[] selected = Selection.gameObjects;
            if (selected.Length == 0) {
                Debug.LogWarning("Universal Colliders: No GameObjects selected.");
                return;
            }

            Undo.SetCurrentGroupName("Remove Universal Colliders");
            int removed = 0;

            List<UCollidersRoot> roots = CollectRoots(selected, includeChildren: true, skipWithout: false);
            foreach (UCollidersRoot root in roots) {
                root.DestroyColliders(true);
                Undo.DestroyObjectImmediate(root);
                removed++;
            }

            List<ConvexDecomposer> decomposers = CollectDecomposers(selected, includeChildren: true);
            foreach (ConvexDecomposer cd in decomposers) {
                cd.DestroyColliders();
                Undo.DestroyObjectImmediate(cd);
                removed++;
            }

            Undo.IncrementCurrentGroup();
            if (removed == 0) {
                Debug.LogWarning("Universal Colliders: No collider components found in selection.");
                return;
            }
            Debug.Log($"Universal Colliders: Removed {removed} component{(removed != 1 ? "s" : "")}.");
        }

        [MenuItem("GameObject/Universal Colliders/Generate for Selection", true)]
        [MenuItem("GameObject/Universal Colliders/Remove from Selection", true)]
        static bool ValidateSelection()
        {
            return Selection.gameObjects.Length > 0;
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Batch Collider Generation", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            colliderType = (ColliderType)EditorGUILayout.EnumPopup("Method", colliderType);

            if (colliderType == ColliderType.OBBTree) {
                recursionLevel = EditorGUILayout.IntSlider("Recursion Level", recursionLevel, 0, UCollidersRoot.MaxRecursionDepth);
                shape = (Shape)EditorGUILayout.EnumPopup("Shape", shape);
            }

            EditorGUILayout.Space(4);
            includeChildren = EditorGUILayout.Toggle(
                new GUIContent("Process Children", "Find meshes in children of selected GameObjects."),
                includeChildren
            );
            skipExisting = EditorGUILayout.Toggle(
                new GUIContent("Skip Existing", "Skip GameObjects that already have a collider generator component."),
                skipExisting
            );

            EditorGUILayout.Space(8);

            // Preview what will be processed
            GameObject[] selected = Selection.gameObjects;
            List<GameObject> targets = CollectMeshObjects(selected, includeChildren, skipExisting);
            EditorGUILayout.HelpBox(
                $"{selected.Length} selected, {targets.Count} mesh object{(targets.Count != 1 ? "s" : "")} to process.",
                MessageType.Info
            );

            EditorGUILayout.Space(4);

            GUI.enabled = targets.Count > 0;
            if (GUILayout.Button("Generate Colliders", GUILayout.Height(30))) {
                GenerateForObjects(targets);
            }
            GUI.enabled = true;
        }

        /// <summary>
        /// Generates colliders on all target GameObjects with a progress bar.
        /// </summary>
        void GenerateForObjects(List<GameObject> targets)
        {
            Undo.SetCurrentGroupName("Batch Generate Universal Colliders");
            int succeeded = 0;
            int failed = 0;

            bool useConvexDecomposition = colliderType == ColliderType.ConvexDecomposition;

            for (int i = 0; i < targets.Count; i++) {
                GameObject go = targets[i];
                bool cancelled = EditorUtility.DisplayCancelableProgressBar(
                    "Generating Colliders",
                    $"({i + 1}/{targets.Count}) {go.name}",
                    (float)i / targets.Count
                );
                if (cancelled) break;

                try {
                    if (useConvexDecomposition) {
                        ConvexDecomposer cd = go.GetComponent<ConvexDecomposer>();
                        if (cd == null)
                            cd = Undo.AddComponent<ConvexDecomposer>(go);
                        else
                            Undo.RecordObject(cd, "Configure ConvexDecomposer");

                        CoACDWrapper.OnProgress = (msg, p) => {
                            float objectProgress = (float)i / targets.Count;
                            float perObject = 1f / targets.Count;
                            EditorUtility.DisplayProgressBar(
                                "Generating Colliders",
                                $"({i + 1}/{targets.Count}) {go.name}: {msg}",
                                objectProgress + p * perObject
                            );
                        };
                        cd.RegenerateColliders();
                    } else {
                        UCollidersRoot root = go.GetComponent<UCollidersRoot>();
                        if (root == null)
                            root = Undo.AddComponent<UCollidersRoot>(go);
                        else
                            Undo.RecordObject(root, "Configure UCollidersRoot");

                        root.recursionLevel = recursionLevel;
                        root.shape = shape;
                        root.RegenerateColliders();
                    }
                    succeeded++;
                }
                catch (Exception e) {
                    Debug.LogError($"Universal Colliders: Failed on '{go.name}': {e.Message}", go);
                    failed++;
                }
                finally {
                    if (useConvexDecomposition)
                        CoACDWrapper.OnProgress = null;
                }
            }

            EditorUtility.ClearProgressBar();
            Undo.IncrementCurrentGroup();

            string message = $"Universal Colliders: Generated colliders on {succeeded} object{(succeeded != 1 ? "s" : "")}";
            if (failed > 0)
                message += $", {failed} failed";
            Debug.Log(message + ".");
        }

        /// <summary>
        /// Collects GameObjects that have a mesh and should be processed.
        /// </summary>
        static List<GameObject> CollectMeshObjects(GameObject[] selected, bool includeChildren, bool skipExisting)
        {
            HashSet<GameObject> seen = new HashSet<GameObject>();
            List<GameObject> result = new List<GameObject>();

            foreach (GameObject go in selected) {
                if (includeChildren) {
                    foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>()) {
                        AddIfValid(mf.gameObject, skipExisting, seen, result);
                    }
                    foreach (SkinnedMeshRenderer smr in go.GetComponentsInChildren<SkinnedMeshRenderer>()) {
                        AddIfValid(smr.gameObject, skipExisting, seen, result);
                    }
                } else {
                    if (go.GetComponent<MeshFilter>() != null || go.GetComponent<SkinnedMeshRenderer>() != null) {
                        AddIfValid(go, skipExisting, seen, result);
                    }
                }
            }
            return result;
        }

        static void AddIfValid(GameObject go, bool skipExisting, HashSet<GameObject> seen, List<GameObject> result)
        {
            if (!seen.Add(go)) return;
            if (skipExisting && (go.GetComponent<UCollidersRoot>() != null || go.GetComponent<ConvexDecomposer>() != null)) return;
            result.Add(go);
        }

        /// <summary>
        /// Collects existing UCollidersRoot components from the selection.
        /// </summary>
        static List<UCollidersRoot> CollectRoots(GameObject[] selected, bool includeChildren, bool skipWithout)
        {
            HashSet<UCollidersRoot> seen = new HashSet<UCollidersRoot>();
            List<UCollidersRoot> result = new List<UCollidersRoot>();

            foreach (GameObject go in selected) {
                UCollidersRoot[] roots = includeChildren
                    ? go.GetComponentsInChildren<UCollidersRoot>()
                    : go.GetComponents<UCollidersRoot>();
                foreach (UCollidersRoot root in roots) {
                    if (seen.Add(root))
                        result.Add(root);
                }
            }
            return result;
        }

        /// <summary>
        /// Collects existing ConvexDecomposer components from the selection.
        /// </summary>
        static List<ConvexDecomposer> CollectDecomposers(GameObject[] selected, bool includeChildren)
        {
            HashSet<ConvexDecomposer> seen = new HashSet<ConvexDecomposer>();
            List<ConvexDecomposer> result = new List<ConvexDecomposer>();

            foreach (GameObject go in selected) {
                ConvexDecomposer[] decomposers = includeChildren
                    ? go.GetComponentsInChildren<ConvexDecomposer>()
                    : go.GetComponents<ConvexDecomposer>();
                foreach (ConvexDecomposer cd in decomposers) {
                    if (seen.Add(cd))
                        result.Add(cd);
                }
            }
            return result;
        }
    }
}
