using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MoveUp.EditorTools
{
    /// <summary>
    /// Builds a concave compound collider out of geometry-aware primitive colliders.
    /// Whole box/capsule shapes are recognized first; remaining connected surface
    /// regions are fitted with board-like boxes and refined only where needed.
    /// </summary>
    public sealed class PrimitiveColliderGeneratorWindow : EditorWindow
    {
        private const string GeneratedPrefix = "__PrimitiveColliders_Generated";

        [SerializeField] private bool automaticGeometrySettings = true;
        [SerializeField] private int colliderBudget = 128;
        [SerializeField] private bool showAdvancedSettings;
        [SerializeField, Range(5f, 45f)] private float surfaceMergeAngle = 16f;
        [SerializeField, Range(0.45f, 0.9f)] private float targetSurfaceCoverage = 0.66f;
        [SerializeField] private float minimumBoardThickness = 0.02f;
        [SerializeField] private bool includeInactive = true;
        [SerializeField] private bool ignoreNonSolidVisuals = true;
        [SerializeField] private bool disableSourceMeshColliders = true;
        [SerializeField] private bool copySourceSettings = true;
        [SerializeField] private bool fallbackIsTrigger;
        [SerializeField] private PhysicsMaterial fallbackMaterial;
        [SerializeField, Range(0f, 0.5f)] private float overlap = 0.12f;

        private Vector2 scroll;
        private string lastResult;
        private MessageType lastResultType = MessageType.Info;

        [MenuItem("Tools/Move Up/Primitive Collider Generator")]
        private static void OpenWindow()
        {
            GetWindow<PrimitiveColliderGeneratorWindow>("Primitive Colliders");
        }

        [MenuItem("GameObject/Move Up/Generate Primitive Colliders", false, 20)]
        private static void GenerateFromMenu()
        {
            PrimitiveColliderGeneratorWindow window = GetWindow<PrimitiveColliderGeneratorWindow>("Primitive Colliders");
            window.GenerateForSelection();
        }

        [MenuItem("GameObject/Move Up/Generate Primitive Colliders", true)]
        private static bool ValidateGenerateFromMenu()
        {
            return Selection.gameObjects != null && Selection.gameObjects.Length > 0 && !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        [MenuItem("GameObject/Move Up/Remove Generated Primitive Colliders", false, 21)]
        private static void RemoveFromMenu()
        {
            int removed = RemoveGenerated(GetGenerationRoots(GetSelectionRoots()));
            Debug.Log($"Primitive Collider Generator: removed {removed} generated collider group(s).");
        }

        [MenuItem("GameObject/Move Up/Remove Generated Primitive Colliders", true)]
        private static bool ValidateRemoveFromMenu()
        {
            return Selection.gameObjects != null && Selection.gameObjects.Length > 0 && !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Compound collision from primitives", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "The tool recognizes complete box and capsule shapes, then fits remaining surfaces with oriented boards. " +
                "Flat areas become large boards; bends, holes and concave boundaries are subdivided only where needed. " +
                "All generated collision is stored in a separate hierarchy below the selected prefab root.",
                MessageType.Info);

            EditorGUILayout.Space(4f);
            DrawSelectionSummary();

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Geometry analysis", EditorStyles.boldLabel);
            automaticGeometrySettings = EditorGUILayout.Toggle(
                new GUIContent("Automatic fitting", "Automatically derives merge angle, board thickness and useful collider count from each mesh."),
                automaticGeometrySettings);
            colliderBudget = EditorGUILayout.IntSlider(
                new GUIContent("Safety limit per mesh", "Maximum refinement budget. Good flat regions still use only one or a few boards."),
                colliderBudget, 8, 512);

            showAdvancedSettings = EditorGUILayout.Foldout(showAdvancedSettings, "Advanced fitting", true);
            if (showAdvancedSettings)
            {
                using (new EditorGUI.DisabledScope(automaticGeometrySettings))
                {
                    surfaceMergeAngle = EditorGUILayout.Slider(
                        new GUIContent("Surface merge angle", "Maximum normal change inside one initial geometric region."),
                        surfaceMergeAngle, 5f, 45f);
                    targetSurfaceCoverage = EditorGUILayout.Slider(
                        new GUIContent("Required coverage", "Higher values split concave outlines and holes into more tightly fitted boards."),
                        targetSurfaceCoverage, 0.45f, 0.9f);
                    minimumBoardThickness = EditorGUILayout.FloatField(
                        new GUIContent("Minimum thickness", "Local-space board thickness. Automatic fitting derives this from mesh scale."),
                        Mathf.Max(0.0001f, minimumBoardThickness));
                }

                overlap = EditorGUILayout.Slider(
                    new GUIContent("Seam overlap", "Small extension at board edges, relative to the derived board thickness, to prevent collision gaps."),
                    overlap, 0f, 0.5f);
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Sources and physics", EditorStyles.boldLabel);
            includeInactive = EditorGUILayout.Toggle("Include inactive", includeInactive);
            ignoreNonSolidVisuals = EditorGUILayout.Toggle(
                new GUIContent("Ignore sails and flags", "Skips non-solid cloth visuals whose object or mesh name identifies a sail, flag, banner or cloth."),
                ignoreNonSolidVisuals);
            disableSourceMeshColliders = EditorGUILayout.Toggle(new GUIContent("Disable MeshColliders", "Disables only MeshColliders that were used as generation sources."), disableSourceMeshColliders);
            copySourceSettings = EditorGUILayout.Toggle(new GUIContent("Copy source settings", "Copies trigger and material from a source MeshCollider when present."), copySourceSettings);

            using (new EditorGUI.DisabledScope(copySourceSettings))
            {
                fallbackIsTrigger = EditorGUILayout.Toggle("Fallback is trigger", fallbackIsTrigger);
                fallbackMaterial = (PhysicsMaterial)EditorGUILayout.ObjectField("Fallback material", fallbackMaterial, typeof(PhysicsMaterial), false);
            }

            EditorGUILayout.Space(12f);
            using (new EditorGUI.DisabledScope(Selection.gameObjects == null || Selection.gameObjects.Length == 0 || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Generate / Regenerate", GUILayout.Height(32f)))
                    GenerateForSelection();

                if (GUILayout.Button("Remove generated colliders", GUILayout.Height(24f)))
                {
                    int removed = RemoveGenerated(GetGenerationRoots(GetSelectionRoots()));
                    lastResult = $"Removed generated groups: {removed}.";
                    lastResultType = MessageType.Info;
                }
            }

            if (!string.IsNullOrEmpty(lastResult))
            {
                EditorGUILayout.Space(8f);
                EditorGUILayout.HelpBox(lastResult, lastResultType);
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.HelpBox(
                "Automatic fitting is recommended. The safety limit is not a target: simple geometry stays simple. " +
                "Openings and concave areas remain open because only mesh surfaces are covered.",
                MessageType.None);
            EditorGUILayout.EndScrollView();
        }

        private void DrawSelectionSummary()
        {
            GameObject[] roots = GetSelectionRoots();
            if (roots.Length == 0)
            {
                EditorGUILayout.HelpBox("Select one or more scene objects.", MessageType.Warning);
                return;
            }

            int meshFilters = 0;
            int meshColliders = 0;
            foreach (GameObject root in roots)
            {
                meshFilters += root.GetComponentsInChildren<MeshFilter>(includeInactive).Length;
                meshColliders += root.GetComponentsInChildren<MeshCollider>(includeInactive).Length;
            }

            EditorGUILayout.LabelField("Selected roots", roots.Length.ToString());
            EditorGUILayout.LabelField("MeshFilter / MeshCollider sources", $"{meshFilters} / {meshColliders}");
        }

        private void GenerateForSelection()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                lastResult = "Generation is available only outside Play Mode.";
                lastResultType = MessageType.Warning;
                return;
            }

            GameObject[] roots = GetSelectionRoots();
            if (roots.Length == 0)
            {
                lastResult = "Select at least one scene object.";
                lastResultType = MessageType.Warning;
                return;
            }

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Generate Primitive Colliders");

            int generatedMeshes = 0;
            int generatedColliders = 0;
            int disabledMeshColliders = 0;
            int skippedMeshes = 0;
            var warnings = new List<string>();
            var generatedRoots = new Dictionary<int, Transform>();

            try
            {
                RemoveGenerated(GetGenerationRoots(roots));
                List<MeshSource> sources = CollectSources(roots, includeInactive, ignoreNonSolidVisuals);

                for (int i = 0; i < sources.Count; i++)
                {
                    MeshSource source = sources[i];
                    float progress = sources.Count == 0 ? 1f : (float)i / sources.Count;
                    EditorUtility.DisplayProgressBar("Primitive Collider Generator", $"Analyzing surfaces in {source.Mesh.name} ({i + 1}/{sources.Count})", progress);

                    if (!TryReadMesh(source.Mesh, out Vector3[] vertices, out int[] triangles, out string readError))
                    {
                        skippedMeshes++;
                        warnings.Add($"{source.Mesh.name}: {readError}");
                        continue;
                    }

                    if (vertices.Length == 0 || triangles.Length < 3)
                    {
                        skippedMeshes++;
                        warnings.Add($"{source.Mesh.name}: mesh has no triangle geometry.");
                        continue;
                    }

                    BoardGenerationResult result = BuildGeometryAwareBoards(
                        vertices,
                        triangles,
                        colliderBudget,
                        automaticGeometrySettings,
                        surfaceMergeAngle,
                        targetSurfaceCoverage,
                        minimumBoardThickness);
                    if (result.Boards.Count == 0)
                    {
                        skippedMeshes++;
                        warnings.Add($"{source.Mesh.name}: no valid surface regions were found.");
                        continue;
                    }

                    PhysicsMaterial material = fallbackMaterial;
                    bool isTrigger = fallbackIsTrigger;
                    if (copySourceSettings && source.SourceCollider != null)
                    {
                        material = source.SourceCollider.sharedMaterial;
                        isTrigger = source.SourceCollider.isTrigger;
                    }

                    Transform generatedRoot = GetOrCreateGeneratedRoot(source.Root, generatedRoots);
                    GameObject group = new GameObject(GetGeneratedName(source.Transform, source.Mesh));
                    Undo.RegisterCreatedObjectUndo(group, "Create Primitive Collider Group");
                    group.layer = source.Transform.gameObject.layer;
                    Transform groupTransform = group.transform;
                    groupTransform.SetParent(generatedRoot, false);
                    CopyRelativeTransform(source.Transform, generatedRoot, groupTransform);

                    for (int boxIndex = 0; boxIndex < result.Boards.Count; boxIndex++)
                    {
                        OrientedBoard board = result.Boards[boxIndex];
                        float paddingReference = board.Kind == PrimitiveKind.Capsule ? board.Radius : board.Size.y;
                        float padding = Mathf.Max(paddingReference * overlap, result.MeshScale * 0.00005f);
                        string primitiveName = board.Kind == PrimitiveKind.Capsule ? "Capsule" : "Board";
                        GameObject boardObject = new GameObject($"{primitiveName}_{boxIndex + 1:000}");
                        Undo.RegisterCreatedObjectUndo(boardObject, "Create Oriented Collider Board");
                        boardObject.layer = group.layer;

                        Transform boardTransform = boardObject.transform;
                        boardTransform.SetParent(groupTransform, false);
                        boardTransform.localPosition = board.Center;
                        boardTransform.localRotation = board.Rotation;
                        boardTransform.localScale = Vector3.one;

                        if (board.Kind == PrimitiveKind.Capsule)
                        {
                            CapsuleCollider collider = Undo.AddComponent<CapsuleCollider>(boardObject);
                            collider.center = Vector3.zero;
                            collider.direction = 1;
                            collider.radius = board.Radius + padding;
                            collider.height = Mathf.Max(board.Height + padding * 2f, collider.radius * 2f);
                            collider.isTrigger = isTrigger;
                            collider.sharedMaterial = material;
                        }
                        else
                        {
                            BoxCollider collider = Undo.AddComponent<BoxCollider>(boardObject);
                            collider.center = Vector3.zero;
                            collider.size = board.Size + new Vector3(padding * 2f, padding, padding * 2f);
                            collider.isTrigger = isTrigger;
                            collider.sharedMaterial = material;
                        }
                    }

                    generatedMeshes++;
                    generatedColliders += result.Boards.Count;

                    if (result.InitialRegionCount > colliderBudget)
                    {
                        warnings.Add(
                            $"{source.Mesh.name}: geometry proposed {result.InitialRegionCount} primitive/surface regions. " +
                            $"The result was limited to {colliderBudget}; smallest residual details were omitted instead of bridging empty space.");
                    }
                    else if (result.UnresolvedRegionCount > 0)
                    {
                        warnings.Add(
                            $"{source.Mesh.name}: refinement reached its automatic budget at {result.Boards.Count} boards; " +
                            $"{result.UnresolvedRegionCount} region(s) could still be fitted more tightly.");
                    }

                    if (disableSourceMeshColliders && source.SourceCollider != null && source.SourceCollider.enabled)
                    {
                        Undo.RecordObject(source.SourceCollider, "Disable Source MeshCollider");
                        source.SourceCollider.enabled = false;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(source.SourceCollider);
                        disabledMeshColliders++;
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                lastResult = "Generation failed. See Console for details. You can use Undo to revert partial output.";
                lastResultType = MessageType.Error;
                return;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Undo.CollapseUndoOperations(undoGroup);
            }

            lastResult = $"Generated {generatedColliders} primitive colliders for {generatedMeshes} mesh(es). " +
                         $"Disabled source MeshColliders: {disabledMeshColliders}. Skipped: {skippedMeshes}.";
            lastResultType = warnings.Count > 0 ? MessageType.Warning : MessageType.Info;

            if (warnings.Count > 0)
            {
                lastResult += "\n" + string.Join("\n", warnings);
                foreach (string warning in warnings)
                    Debug.LogWarning("Primitive Collider Generator: " + warning);
            }

            SceneView.RepaintAll();
        }

        private static BoardGenerationResult BuildGeometryAwareBoards(
            Vector3[] vertices,
            int[] indices,
            int maximumBoards,
            bool automatic,
            float requestedMergeAngle,
            float requestedCoverage,
            float requestedMinimumThickness)
        {
            Bounds meshBounds = new Bounds(vertices[0], Vector3.zero);
            for (int i = 1; i < vertices.Length; i++)
                meshBounds.Encapsulate(vertices[i]);

            float meshScale = Mathf.Max(meshBounds.size.x, Mathf.Max(meshBounds.size.y, meshBounds.size.z));
            if (meshScale <= 0.000001f)
                return BoardGenerationResult.Empty;

            List<SurfaceTriangle> surfaceTriangles = CreateSurfaceTriangles(vertices, indices);
            if (surfaceTriangles.Count == 0)
                return BoardGenerationResult.Empty;

            int hardBudget = Mathf.Clamp(maximumBoards, 8, 512);
            float mergeAngle = automatic
                ? Mathf.Clamp(10f + Mathf.Log10(Mathf.Max(1, surfaceTriangles.Count)) * 2.25f, 11f, 19f)
                : Mathf.Clamp(requestedMergeAngle, 5f, 45f);
            float targetCoverage = automatic ? 0.68f : Mathf.Clamp(requestedCoverage, 0.45f, 0.9f);
            float baseThickness = automatic
                ? DeriveAutomaticThickness(meshBounds, meshScale)
                : Mathf.Max(0.0001f, requestedMinimumThickness);
            float minimumBoardSpan = Mathf.Max(meshScale / 400f, baseThickness * 1.5f);
            float maximumCurvature = automatic ? 0.045f : 0.035f;

            int automaticBudget = automatic
                ? Mathf.Clamp(
                    Mathf.RoundToInt(10f + Mathf.Sqrt(surfaceTriangles.Count) * 0.8f),
                    Mathf.Min(16, hardBudget),
                    Mathf.Min(96, hardBudget))
                : hardBudget;

            List<int>[] adjacency = BuildSurfaceAdjacency(
                vertices,
                surfaceTriangles,
                Mathf.Max(meshScale * 0.00001f, 0.0000001f));

            List<List<int>> connectedComponents = SegmentConnectedComponents(surfaceTriangles, adjacency);
            var primitives = new List<OrientedBoard>();
            var coveredByWholePrimitive = new bool[surfaceTriangles.Count];
            var componentTrianglesByIndex = new List<List<SurfaceTriangle>>(connectedComponents.Count);
            var componentShapes = new List<ElongatedComponent>(connectedComponents.Count);
            var componentClaimed = new bool[connectedComponents.Count];

            for (int componentIndex = 0; componentIndex < connectedComponents.Count; componentIndex++)
            {
                List<int> component = connectedComponents[componentIndex];
                var componentTriangles = new List<SurfaceTriangle>(component.Count);
                for (int i = 0; i < component.Count; i++)
                    componentTriangles.Add(surfaceTriangles[component[i]]);

                componentTrianglesByIndex.Add(componentTriangles);
                componentShapes.Add(AnalyzeElongatedComponent(componentTriangles));
            }

            // Exported meshes frequently contain one disconnected strip per material or
            // smoothing sector. A mast can therefore arrive as 12-24 separate rectangles.
            // Reassemble compatible, coaxial strips before treating them as independent boards.
            RecognizeCapsuleAssemblies(
                surfaceTriangles,
                connectedComponents,
                componentTrianglesByIndex,
                componentShapes,
                componentClaimed,
                coveredByWholePrimitive,
                meshScale,
                primitives);

            // Recognize self-contained solids after the aggregate capsule pass. This order
            // lets a complete mast section join neighbouring disconnected side strips into
            // one continuous capsule instead of stopping at the export boundary.
            for (int componentIndex = 0; componentIndex < connectedComponents.Count; componentIndex++)
            {
                if (componentClaimed[componentIndex])
                    continue;

                List<SurfaceTriangle> componentTriangles = componentTrianglesByIndex[componentIndex];
                Bounds componentBounds = CalculateTriangleBounds(componentTriangles);
                float componentScale = Mathf.Max(
                    componentBounds.size.x,
                    Mathf.Max(componentBounds.size.y, componentBounds.size.z));
                float componentThickness = automatic
                    ? DeriveAutomaticThickness(componentBounds, componentScale)
                    : baseThickness;

                OrientedBoard primitive;
                bool recognized = TryFitSingleCapsule(componentTriangles, componentScale, out primitive) ||
                                  TryFitSingleBox(componentTriangles, componentScale, componentThickness, out primitive);
                if (!recognized)
                    continue;

                primitives.Add(primitive);
                ClaimComponent(componentIndex, connectedComponents, componentClaimed, coveredByWholePrimitive);
                CoverTrianglesInsidePrimitive(
                    surfaceTriangles,
                    primitive,
                    coveredByWholePrimitive,
                    meshScale * 0.0025f);
                SynchronizeCoveredComponents(
                    connectedComponents,
                    componentClaimed,
                    coveredByWholePrimitive);
            }

            // Do the same for rectangular beams whose six faces were exported as separate
            // islands. This is what lets a rotated plank or stair stringer become one OBB.
            RecognizeBoxAssemblies(
                surfaceTriangles,
                connectedComponents,
                componentTrianglesByIndex,
                componentShapes,
                componentClaimed,
                coveredByWholePrimitive,
                meshScale,
                baseThickness,
                primitives);

            // Only after aggregate primitive recognition may an isolated flat component
            // become a board. Doing this earlier would permanently turn a cylinder into
            // one thin box per side.
            for (int componentIndex = 0; componentIndex < connectedComponents.Count; componentIndex++)
            {
                if (componentClaimed[componentIndex])
                    continue;

                List<SurfaceTriangle> componentTriangles = componentTrianglesByIndex[componentIndex];
                Bounds componentBounds = CalculateTriangleBounds(componentTriangles);
                float componentScale = Mathf.Max(
                    componentBounds.size.x,
                    Mathf.Max(componentBounds.size.y, componentBounds.size.z));
                float componentThickness = automatic
                    ? DeriveAutomaticThickness(componentBounds, componentScale)
                    : baseThickness;

                if (!TryFitSinglePlanarBoard(componentTriangles, componentScale, componentThickness, out OrientedBoard board))
                    continue;

                primitives.Add(board);
                ClaimComponent(componentIndex, connectedComponents, componentClaimed, coveredByWholePrimitive);
            }

            float segmentationAngle = mergeAngle;
            List<List<int>> regions = SegmentSurfaceRegions(
                surfaceTriangles,
                adjacency,
                segmentationAngle,
                coveredByWholePrimitive);

            // A physical primitive may be welded to neighbouring geometry and therefore
            // belong to one large connected component. Search the sharp surface patches
            // themselves before coarsening them. This extracts the mast from its basket
            // and a rectangular beam from attached brackets instead of boxing every face.
            RecognizePrimitiveAssembliesFromSurfaceRegions(
                surfaceTriangles,
                regions,
                coveredByWholePrimitive,
                meshScale,
                baseThickness,
                primitives);

            // The safety limit is a real limit. Prefer complete recognized solids and omit
            // low-impact detail instead of silently emitting hundreds of face colliders.
            if (primitives.Count > hardBudget)
            {
                primitives.Sort((left, right) =>
                    PrimitiveImportance(right).CompareTo(PrimitiveImportance(left)));
                primitives.RemoveRange(hardBudget, primitives.Count - hardBudget);
            }

            while (true)
            {
                regions = SegmentSurfaceRegions(
                    surfaceTriangles,
                    adjacency,
                    segmentationAngle,
                    coveredByWholePrimitive);
                if (regions.Count + primitives.Count <= hardBudget || segmentationAngle >= 45f)
                    break;

                segmentationAngle = Mathf.Min(45f, segmentationAngle + 5f);
            }

            var settings = new BoardFittingSettings(
                meshScale,
                baseThickness,
                minimumBoardSpan,
                segmentationAngle,
                targetCoverage,
                maximumCurvature,
                automatic);

            var clusters = new List<BoardCluster>(regions.Count);
            for (int i = 0; i < regions.Count; i++)
            {
                BoardFit fit = FitBoard(surfaceTriangles, regions[i], settings);
                var cluster = new BoardCluster(regions[i], fit);
                cluster.Split = FindBestSplit(surfaceTriangles, cluster, settings);
                clusters.Add(cluster);
            }

            int initialPrimitiveCount = regions.Count + primitives.Count;
            int availableSurfaceBoards = Mathf.Max(0, hardBudget - primitives.Count);
            if (clusters.Count > availableSurfaceBoards)
            {
                clusters.Sort((left, right) =>
                    SurfaceClusterImportance(right).CompareTo(SurfaceClusterImportance(left)));
                clusters.RemoveRange(availableSurfaceBoards, clusters.Count - availableSurfaceBoards);
            }

            // Never merge disconnected or sharply different regions merely to hit a number.
            // Such a merge would bridge openings. The budget controls only optional refinement.
            int effectiveBudget = Mathf.Max(initialPrimitiveCount, automaticBudget);
            effectiveBudget = Mathf.Min(hardBudget, effectiveBudget);
            while (clusters.Count + primitives.Count < effectiveBudget &&
                   TryApplyBestSplit(surfaceTriangles, clusters, settings, false))
            {
            }

            // Preserve local openings even when the normal refinement budget was consumed
            // elsewhere on a large combined mesh. This allowance is deliberately bounded.
            int openingBudget = hardBudget;
            while (clusters.Count + primitives.Count < openingBudget &&
                   TryApplyBestSplit(surfaceTriangles, clusters, settings, true))
            {
            }

            // When the hard limit is already full, preserve a significant opening by
            // replacing the least important residual surface with the required second
            // half of the opening. Collider count stays constant and no square lid is
            // allowed to remain over a seat, doorway or recess merely because of budget.
            int replacementGuard = hardBudget * 2;
            while (clusters.Count > 1 && replacementGuard-- > 0 &&
                   TryReplaceLowPrioritySurfaceWithOpeningSplit(surfaceTriangles, clusters, settings))
            {
            }

            var boards = new List<OrientedBoard>(primitives.Count + clusters.Count);
            boards.AddRange(primitives);
            int unresolved = 0;
            for (int i = 0; i < clusters.Count; i++)
            {
                BoardFit fit = clusters[i].Fit;
                boards.Add(OrientedBoard.CreateBox(fit.Center, fit.Rotation, fit.Size));
                if (clusters.Count + primitives.Count >= effectiveBudget && fit.NeedsRefinement)
                    unresolved++;
            }

            return new BoardGenerationResult(
                boards,
                meshScale,
                baseThickness,
                initialPrimitiveCount,
                unresolved);
        }

        private static bool TryReplaceLowPrioritySurfaceWithOpeningSplit(
            List<SurfaceTriangle> triangles,
            List<BoardCluster> clusters,
            BoardFittingSettings settings)
        {
            int targetIndex = -1;
            float targetPriority = 0f;
            for (int i = 0; i < clusters.Count; i++)
            {
                BoardCluster cluster = clusters[i];
                if (cluster.Fit.UnsupportedFraction <= 0.012f ||
                    !cluster.Split.IsValid ||
                    cluster.Split.Priority <= targetPriority)
                    continue;
                targetIndex = i;
                targetPriority = cluster.Split.Priority;
            }

            if (targetIndex < 0)
                return false;

            int donorIndex = -1;
            float donorImportance = float.PositiveInfinity;
            for (int i = 0; i < clusters.Count; i++)
            {
                if (i == targetIndex)
                    continue;
                float importance = SurfaceClusterImportance(clusters[i]);
                if (importance >= donorImportance)
                    continue;
                donorImportance = importance;
                donorIndex = i;
            }

            if (donorIndex < 0)
                return false;

            SplitCandidate split = clusters[targetIndex].Split;
            var left = new BoardCluster(split.LeftTriangles, split.LeftFit);
            var right = new BoardCluster(split.RightTriangles, split.RightFit);
            left.Split = FindBestSplit(triangles, left, settings);
            right.Split = FindBestSplit(triangles, right, settings);

            clusters[targetIndex] = left;
            clusters.RemoveAt(donorIndex);
            clusters.Add(right);
            return true;
        }

        private static float PrimitiveImportance(OrientedBoard primitive)
        {
            if (primitive.Kind == PrimitiveKind.Capsule)
                return Mathf.Max(primitive.Height, primitive.Radius * 2f) * primitive.Radius;

            Vector3 size = primitive.Size;
            float faceXY = size.x * size.y;
            float faceXZ = size.x * size.z;
            float faceYZ = size.y * size.z;
            return Mathf.Max(faceXY, Mathf.Max(faceXZ, faceYZ));
        }

        private static float SurfaceClusterImportance(BoardCluster cluster)
        {
            return cluster.Fit.RectangleArea * Mathf.Max(0.05f, cluster.Fit.Coverage);
        }

        private static void RecognizePrimitiveAssembliesFromSurfaceRegions(
            List<SurfaceTriangle> allTriangles,
            List<List<int>> regions,
            bool[] coveredTriangles,
            float meshScale,
            float minimumThickness,
            List<OrientedBoard> primitives)
        {
            if (regions.Count == 0)
                return;

            var regionTriangles = new List<List<SurfaceTriangle>>(regions.Count);
            var regionShapes = new List<ElongatedComponent>(regions.Count);
            for (int regionIndex = 0; regionIndex < regions.Count; regionIndex++)
            {
                List<int> triangleIndices = regions[regionIndex];
                var triangles = new List<SurfaceTriangle>(triangleIndices.Count);
                for (int i = 0; i < triangleIndices.Count; i++)
                    triangles.Add(allTriangles[triangleIndices[i]]);
                regionTriangles.Add(triangles);
                regionShapes.Add(AnalyzeElongatedComponent(triangles));
            }

            var regionClaimed = new bool[regions.Count];
            RecognizeCapsuleAssemblies(
                allTriangles,
                regions,
                regionTriangles,
                regionShapes,
                regionClaimed,
                coveredTriangles,
                meshScale,
                primitives);
            RecognizeBoxAssemblies(
                allTriangles,
                regions,
                regionTriangles,
                regionShapes,
                regionClaimed,
                coveredTriangles,
                meshScale,
                minimumThickness,
                primitives);
        }

        private static bool TryApplyBestSplit(
            List<SurfaceTriangle> triangles,
            List<BoardCluster> clusters,
            BoardFittingSettings settings,
            bool openingsOnly)
        {
            int bestClusterIndex = -1;
            float bestPriority = 0f;
            for (int i = 0; i < clusters.Count; i++)
            {
                if (openingsOnly && clusters[i].Fit.UnsupportedFraction <= 0.012f)
                    continue;

                SplitCandidate candidate = clusters[i].Split;
                if (candidate.IsValid && candidate.Priority > bestPriority)
                {
                    bestPriority = candidate.Priority;
                    bestClusterIndex = i;
                }
            }

            if (bestClusterIndex < 0)
                return false;

            SplitCandidate best = clusters[bestClusterIndex].Split;
            var left = new BoardCluster(best.LeftTriangles, best.LeftFit);
            var right = new BoardCluster(best.RightTriangles, best.RightFit);
            left.Split = FindBestSplit(triangles, left, settings);
            right.Split = FindBestSplit(triangles, right, settings);
            clusters[bestClusterIndex] = left;
            clusters.Add(right);
            return true;
        }

        private static void RecognizeCapsuleAssemblies(
            List<SurfaceTriangle> allTriangles,
            List<List<int>> connectedComponents,
            List<List<SurfaceTriangle>> componentTriangles,
            List<ElongatedComponent> componentShapes,
            bool[] componentClaimed,
            bool[] coveredTriangles,
            float meshScale,
            List<OrientedBoard> primitives)
        {
            for (int seedIndex = 0; seedIndex < componentShapes.Count; seedIndex++)
            {
                if (componentClaimed[seedIndex] || !componentShapes[seedIndex].IsElongated)
                    continue;

                List<int> group = CollectCoaxialComponents(seedIndex, componentShapes, componentClaimed, meshScale);
                if (group.Count < 5)
                    continue;

                List<SurfaceTriangle> combined = CombineComponentTriangles(group, componentTriangles);
                Bounds bounds = CalculateTriangleBounds(combined);
                float scale = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                if (!TryFitSingleCapsule(combined, scale, out OrientedBoard capsule))
                    continue;

                primitives.Add(capsule);
                for (int i = 0; i < group.Count; i++)
                    ClaimComponent(group[i], connectedComponents, componentClaimed, coveredTriangles);
                CoverTrianglesInsidePrimitive(allTriangles, capsule, coveredTriangles, meshScale * 0.0025f);
                SynchronizeCoveredComponents(connectedComponents, componentClaimed, coveredTriangles);
            }
        }

        private static void RecognizeBoxAssemblies(
            List<SurfaceTriangle> allTriangles,
            List<List<int>> connectedComponents,
            List<List<SurfaceTriangle>> componentTriangles,
            List<ElongatedComponent> componentShapes,
            bool[] componentClaimed,
            bool[] coveredTriangles,
            float meshScale,
            float minimumThickness,
            List<OrientedBoard> primitives)
        {
            for (int seedIndex = 0; seedIndex < componentShapes.Count; seedIndex++)
            {
                if (componentClaimed[seedIndex] || !componentShapes[seedIndex].IsElongated)
                    continue;

                List<int> group = CollectCoaxialComponents(seedIndex, componentShapes, componentClaimed, meshScale);
                if (group.Count < 3)
                    continue;

                // Nearby parallel details can be present around a beam. Test the complete
                // group first, then progressively tighter neighbourhoods so unrelated
                // geometry cannot force the beam back into separate face boards.
                group.Sort((left, right) =>
                    ComponentDistanceFromAxis(componentShapes[seedIndex], componentShapes[left])
                        .CompareTo(ComponentDistanceFromAxis(componentShapes[seedIndex], componentShapes[right])));

                OrientedBoard bestBox = default;
                int bestCount = 0;
                int largestPrefix = Mathf.Min(group.Count, 16);
                for (int count = 3; count <= largestPrefix; count++)
                {
                    List<int> candidateIndices = group.GetRange(0, count);
                    List<SurfaceTriangle> combined = CombineComponentTriangles(candidateIndices, componentTriangles);
                    Bounds bounds = CalculateTriangleBounds(combined);
                    float scale = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                    float thickness = Mathf.Max(minimumThickness, DeriveAutomaticThickness(bounds, scale));
                    if (!TryFitSingleBox(combined, scale, thickness, out OrientedBoard box))
                        continue;

                    bestBox = box;
                    bestCount = count;
                }

                if (bestCount == 0)
                    continue;

                primitives.Add(bestBox);
                for (int i = 0; i < bestCount; i++)
                    ClaimComponent(group[i], connectedComponents, componentClaimed, coveredTriangles);
                CoverTrianglesInsidePrimitive(allTriangles, bestBox, coveredTriangles, meshScale * 0.0025f);
                SynchronizeCoveredComponents(connectedComponents, componentClaimed, coveredTriangles);
            }
        }

        private static void SynchronizeCoveredComponents(
            List<List<int>> components,
            bool[] componentClaimed,
            bool[] coveredTriangles)
        {
            for (int componentIndex = 0; componentIndex < components.Count; componentIndex++)
            {
                if (componentClaimed[componentIndex])
                    continue;

                List<int> triangleIndices = components[componentIndex];
                bool allCovered = true;
                for (int i = 0; i < triangleIndices.Count; i++)
                {
                    if (coveredTriangles[triangleIndices[i]])
                        continue;
                    allCovered = false;
                    break;
                }

                componentClaimed[componentIndex] = allCovered;
            }
        }

        private static void CoverTrianglesInsidePrimitive(
            List<SurfaceTriangle> triangles,
            OrientedBoard primitive,
            bool[] covered,
            float tolerance)
        {
            Quaternion inverse = Quaternion.Inverse(primitive.Rotation);
            Vector3 halfSize = primitive.Size * 0.5f + Vector3.one * tolerance;
            float capsuleHalfHeight = primitive.Height * 0.5f + tolerance;
            float capsuleRadius = primitive.Radius + tolerance;
            float capsuleRadiusSquared = capsuleRadius * capsuleRadius;

            for (int i = 0; i < triangles.Count; i++)
            {
                if (covered[i])
                    continue;

                SurfaceTriangle triangle = triangles[i];
                if (!PointInsidePrimitive(triangle.APosition) ||
                    !PointInsidePrimitive(triangle.BPosition) ||
                    !PointInsidePrimitive(triangle.CPosition))
                    continue;

                covered[i] = true;
            }

            bool PointInsidePrimitive(Vector3 point)
            {
                Vector3 local = inverse * (point - primitive.Center);
                if (primitive.Kind == PrimitiveKind.Box)
                {
                    return Mathf.Abs(local.x) <= halfSize.x &&
                           Mathf.Abs(local.y) <= halfSize.y &&
                           Mathf.Abs(local.z) <= halfSize.z;
                }

                // Use the cylindrical envelope of the accepted capsule while filtering
                // source faces. Flat end caps and bevels are already represented by the
                // capsule and must not produce two additional board colliders.
                return Mathf.Abs(local.y) <= capsuleHalfHeight &&
                       local.x * local.x + local.z * local.z <= capsuleRadiusSquared;
            }
        }

        private static List<int> CollectCoaxialComponents(
            int seedIndex,
            List<ElongatedComponent> shapes,
            bool[] claimed,
            float meshScale)
        {
            var result = new List<int> { seedIndex };
            var included = new bool[shapes.Count];
            included[seedIndex] = true;

            // Use transitive collection so two coaxial sections that meet end-to-end can
            // become one long primitive even when the original mesh split them in two.
            for (int cursor = 0; cursor < result.Count; cursor++)
            {
                ElongatedComponent current = shapes[result[cursor]];
                for (int candidateIndex = 0; candidateIndex < shapes.Count; candidateIndex++)
                {
                    if (included[candidateIndex] || claimed[candidateIndex])
                        continue;

                    ElongatedComponent candidate = shapes[candidateIndex];
                    if (!candidate.IsElongated || !AreCoaxialNeighbours(current, candidate, meshScale))
                        continue;

                    included[candidateIndex] = true;
                    result.Add(candidateIndex);
                }
            }

            return result;
        }

        private static bool AreCoaxialNeighbours(
            ElongatedComponent left,
            ElongatedComponent right,
            float meshScale)
        {
            float axisDot = Mathf.Abs(Vector3.Dot(left.Axis, right.Axis));
            if (axisDot < Mathf.Cos(7f * Mathf.Deg2Rad))
                return false;

            float lengthRatio = Mathf.Min(left.Length, right.Length) /
                                Mathf.Max(left.Length, right.Length);
            if (lengthRatio < 0.42f)
                return false;

            Vector3 separation = right.Center - left.Center;
            float along = Vector3.Dot(separation, left.Axis);
            float perpendicular = (separation - left.Axis * along).magnitude;
            float maximumPerpendicular = Mathf.Max(
                meshScale * 0.0025f,
                (left.CrossRadius + right.CrossRadius) * 4f);
            if (perpendicular > maximumPerpendicular)
                return false;

            float longitudinalGap = Mathf.Abs(along) - (left.Length + right.Length) * 0.5f;
            float allowedGap = Mathf.Max(
                meshScale * 0.0025f,
                Mathf.Max(left.CrossRadius, right.CrossRadius) * 3.5f);
            return longitudinalGap <= allowedGap;
        }

        private static float ComponentDistanceFromAxis(
            ElongatedComponent axisSource,
            ElongatedComponent candidate)
        {
            Vector3 separation = candidate.Center - axisSource.Center;
            separation -= axisSource.Axis * Vector3.Dot(separation, axisSource.Axis);
            return separation.sqrMagnitude;
        }

        private static ElongatedComponent AnalyzeElongatedComponent(List<SurfaceTriangle> triangles)
        {
            Vector3 center = CalculatePointMean(triangles);
            Vector3 axis = FindPrincipalAxis(triangles, center);
            if (axis.sqrMagnitude < 0.5f)
                return default;
            axis.Normalize();

            float minimum = float.PositiveInfinity;
            float maximum = float.NegativeInfinity;
            float crossRadius = 0f;
            for (int i = 0; i < triangles.Count; i++)
            {
                MeasureElongatedPoint(triangles[i].APosition, center, axis, ref minimum, ref maximum, ref crossRadius);
                MeasureElongatedPoint(triangles[i].BPosition, center, axis, ref minimum, ref maximum, ref crossRadius);
                MeasureElongatedPoint(triangles[i].CPosition, center, axis, ref minimum, ref maximum, ref crossRadius);
            }

            float length = maximum - minimum;
            bool isElongated = length > Mathf.Max(crossRadius * 5f, 0.00001f);
            return new ElongatedComponent(center, axis, length, crossRadius, isElongated);
        }

        private static void MeasureElongatedPoint(
            Vector3 point,
            Vector3 center,
            Vector3 axis,
            ref float minimum,
            ref float maximum,
            ref float crossRadius)
        {
            Vector3 offset = point - center;
            float along = Vector3.Dot(offset, axis);
            minimum = Mathf.Min(minimum, along);
            maximum = Mathf.Max(maximum, along);
            crossRadius = Mathf.Max(crossRadius, (offset - axis * along).magnitude);
        }

        private static List<SurfaceTriangle> CombineComponentTriangles(
            List<int> componentIndices,
            List<List<SurfaceTriangle>> components)
        {
            int capacity = 0;
            for (int i = 0; i < componentIndices.Count; i++)
                capacity += components[componentIndices[i]].Count;

            var combined = new List<SurfaceTriangle>(capacity);
            for (int i = 0; i < componentIndices.Count; i++)
                combined.AddRange(components[componentIndices[i]]);
            return combined;
        }

        private static void ClaimComponent(
            int componentIndex,
            List<List<int>> connectedComponents,
            bool[] componentClaimed,
            bool[] coveredTriangles)
        {
            componentClaimed[componentIndex] = true;
            List<int> triangleIndices = connectedComponents[componentIndex];
            for (int i = 0; i < triangleIndices.Count; i++)
                coveredTriangles[triangleIndices[i]] = true;
        }

        private static Bounds CalculateTriangleBounds(List<SurfaceTriangle> triangles)
        {
            Bounds bounds = new Bounds(triangles[0].APosition, Vector3.zero);
            for (int i = 0; i < triangles.Count; i++)
            {
                bounds.Encapsulate(triangles[i].APosition);
                bounds.Encapsulate(triangles[i].BPosition);
                bounds.Encapsulate(triangles[i].CPosition);
            }

            return bounds;
        }

        private static float DeriveAutomaticThickness(Bounds bounds, float meshScale)
        {
            float smallestPositiveExtent = float.PositiveInfinity;
            Vector3 size = bounds.size;
            if (size.x > meshScale * 0.00001f)
                smallestPositiveExtent = Mathf.Min(smallestPositiveExtent, size.x);
            if (size.y > meshScale * 0.00001f)
                smallestPositiveExtent = Mathf.Min(smallestPositiveExtent, size.y);
            if (size.z > meshScale * 0.00001f)
                smallestPositiveExtent = Mathf.Min(smallestPositiveExtent, size.z);

            float thickness = Mathf.Max(meshScale * 0.0015f, 0.0005f);
            if (!float.IsInfinity(smallestPositiveExtent))
                thickness = Mathf.Min(thickness, Mathf.Max(0.0005f, smallestPositiveExtent * 0.12f));
            return thickness;
        }

        private static bool TryFitSingleCapsule(
            List<SurfaceTriangle> triangles,
            float meshScale,
            out OrientedBoard capsule)
        {
            capsule = default;
            Vector3 mean = CalculatePointMean(triangles);
            Vector3 axis = FindPrincipalAxis(triangles, mean);
            if (axis.sqrMagnitude < 0.5f)
                return false;
            axis.Normalize();

            Vector3 reference = Mathf.Abs(axis.y) < 0.85f ? Vector3.up : Vector3.right;
            Vector3 radialU = Vector3.Cross(reference, axis).normalized;
            Vector3 radialV = Vector3.Cross(axis, radialU).normalized;

            float minimumAxis = float.PositiveInfinity;
            float maximumAxis = float.NegativeInfinity;
            float minimumU = float.PositiveInfinity;
            float maximumU = float.NegativeInfinity;
            float minimumV = float.PositiveInfinity;
            float maximumV = float.NegativeInfinity;
            for (int i = 0; i < triangles.Count; i++)
            {
                SurfaceTriangle triangle = triangles[i];
                EncapsulateCapsulePoint(triangle.APosition, axis, radialU, radialV,
                    ref minimumAxis, ref maximumAxis, ref minimumU, ref maximumU, ref minimumV, ref maximumV);
                EncapsulateCapsulePoint(triangle.BPosition, axis, radialU, radialV,
                    ref minimumAxis, ref maximumAxis, ref minimumU, ref maximumU, ref minimumV, ref maximumV);
                EncapsulateCapsulePoint(triangle.CPosition, axis, radialU, radialV,
                    ref minimumAxis, ref maximumAxis, ref minimumU, ref maximumU, ref minimumV, ref maximumV);
            }

            float middleU = (minimumU + maximumU) * 0.5f;
            float middleV = (minimumV + maximumV) * 0.5f;
            float axisLength = maximumAxis - minimumAxis;
            float radius = 0f;
            for (int i = 0; i < triangles.Count; i++)
            {
                SurfaceTriangle triangle = triangles[i];
                radius = Mathf.Max(radius, DistanceFromCapsuleAxis(triangle.APosition, radialU, radialV, middleU, middleV));
                radius = Mathf.Max(radius, DistanceFromCapsuleAxis(triangle.BPosition, radialU, radialV, middleU, middleV));
                radius = Mathf.Max(radius, DistanceFromCapsuleAxis(triangle.CPosition, radialU, radialV, middleU, middleV));
            }

            if (radius <= meshScale * 0.00001f || axisLength < radius * 3.25f)
                return false;

            float totalArea = 0f;
            float sideArea = 0f;
            float alignedSideArea = 0f;
            float radialCentroidSum = 0f;
            var radialDirections = new HashSet<int>();
            const int directionBins = 18;
            float minimumRadialAlignment = Mathf.Cos(25f * Mathf.Deg2Rad);

            for (int i = 0; i < triangles.Count; i++)
            {
                SurfaceTriangle triangle = triangles[i];
                totalArea += triangle.Area;
                if (Mathf.Abs(Vector3.Dot(triangle.Normal, axis)) > 0.55f)
                    continue;

                float centroidU = Vector3.Dot(triangle.Centroid, radialU) - middleU;
                float centroidV = Vector3.Dot(triangle.Centroid, radialV) - middleV;
                float centroidRadius = Mathf.Sqrt(centroidU * centroidU + centroidV * centroidV);
                if (centroidRadius <= radius * 0.05f)
                    continue;

                sideArea += triangle.Area;
                radialCentroidSum += Mathf.Clamp01(centroidRadius / radius) * triangle.Area;
                Vector3 radialDirection = (radialU * centroidU + radialV * centroidV) / centroidRadius;
                if (Mathf.Abs(Vector3.Dot(triangle.Normal, radialDirection)) >= minimumRadialAlignment)
                    alignedSideArea += triangle.Area;

                float angle = Mathf.Atan2(centroidV, centroidU) * Mathf.Rad2Deg;
                if (angle < 0f)
                    angle += 360f;
                radialDirections.Add(Mathf.FloorToInt(angle / (360f / directionBins)) % directionBins);
            }

            if (totalArea <= 0.0000001f ||
                sideArea / totalArea < 0.4f ||
                alignedSideArea / Mathf.Max(sideArea, 0.0000001f) < 0.65f ||
                radialCentroidSum / Mathf.Max(sideArea, 0.0000001f) < 0.68f ||
                radialDirections.Count < 5)
                return false;

            Vector3 center = axis * ((minimumAxis + maximumAxis) * 0.5f) +
                             radialU * middleU +
                             radialV * middleV;
            Quaternion rotation = Quaternion.FromToRotation(Vector3.up, axis);
            capsule = OrientedBoard.CreateCapsule(center, rotation, radius, axisLength);
            return true;
        }

        private static void EncapsulateCapsulePoint(
            Vector3 point,
            Vector3 axis,
            Vector3 radialU,
            Vector3 radialV,
            ref float minimumAxis,
            ref float maximumAxis,
            ref float minimumU,
            ref float maximumU,
            ref float minimumV,
            ref float maximumV)
        {
            float alongAxis = Vector3.Dot(point, axis);
            float alongU = Vector3.Dot(point, radialU);
            float alongV = Vector3.Dot(point, radialV);
            minimumAxis = Mathf.Min(minimumAxis, alongAxis);
            maximumAxis = Mathf.Max(maximumAxis, alongAxis);
            minimumU = Mathf.Min(minimumU, alongU);
            maximumU = Mathf.Max(maximumU, alongU);
            minimumV = Mathf.Min(minimumV, alongV);
            maximumV = Mathf.Max(maximumV, alongV);
        }

        private static float DistanceFromCapsuleAxis(
            Vector3 point,
            Vector3 radialU,
            Vector3 radialV,
            float middleU,
            float middleV)
        {
            float u = Vector3.Dot(point, radialU) - middleU;
            float v = Vector3.Dot(point, radialV) - middleV;
            return Mathf.Sqrt(u * u + v * v);
        }

        private static Vector3 CalculatePointMean(List<SurfaceTriangle> triangles)
        {
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < triangles.Count; i++)
            {
                sum += triangles[i].APosition;
                sum += triangles[i].BPosition;
                sum += triangles[i].CPosition;
            }

            return sum / Mathf.Max(1, triangles.Count * 3);
        }

        private static Vector3 FindPrincipalAxis(List<SurfaceTriangle> triangles, Vector3 mean)
        {
            float xx = 0f;
            float xy = 0f;
            float xz = 0f;
            float yy = 0f;
            float yz = 0f;
            float zz = 0f;

            for (int i = 0; i < triangles.Count; i++)
            {
                AccumulateCovariance(triangles[i].APosition - mean, ref xx, ref xy, ref xz, ref yy, ref yz, ref zz);
                AccumulateCovariance(triangles[i].BPosition - mean, ref xx, ref xy, ref xz, ref yy, ref yz, ref zz);
                AccumulateCovariance(triangles[i].CPosition - mean, ref xx, ref xy, ref xz, ref yy, ref yz, ref zz);
            }

            Vector3 bestAxis = Vector3.right;
            float bestValue = float.NegativeInfinity;
            Vector3[] seeds = { Vector3.right, Vector3.up, Vector3.forward };
            for (int seedIndex = 0; seedIndex < seeds.Length; seedIndex++)
            {
                Vector3 candidate = seeds[seedIndex];
                for (int iteration = 0; iteration < 12; iteration++)
                {
                    Vector3 multiplied = MultiplyCovariance(candidate, xx, xy, xz, yy, yz, zz);
                    if (multiplied.sqrMagnitude <= 0.0000000001f)
                        break;
                    candidate = multiplied.normalized;
                }

                float value = Vector3.Dot(candidate, MultiplyCovariance(candidate, xx, xy, xz, yy, yz, zz));
                if (value > bestValue)
                {
                    bestValue = value;
                    bestAxis = candidate;
                }
            }

            return bestAxis.normalized;
        }

        private static void AccumulateCovariance(
            Vector3 point,
            ref float xx,
            ref float xy,
            ref float xz,
            ref float yy,
            ref float yz,
            ref float zz)
        {
            xx += point.x * point.x;
            xy += point.x * point.y;
            xz += point.x * point.z;
            yy += point.y * point.y;
            yz += point.y * point.z;
            zz += point.z * point.z;
        }

        private static Vector3 MultiplyCovariance(
            Vector3 value,
            float xx,
            float xy,
            float xz,
            float yy,
            float yz,
            float zz)
        {
            return new Vector3(
                xx * value.x + xy * value.y + xz * value.z,
                xy * value.x + yy * value.y + yz * value.z,
                xz * value.x + yz * value.y + zz * value.z);
        }

        private static bool TryFitSingleBox(
            List<SurfaceTriangle> triangles,
            float meshScale,
            float minimumThickness,
            out OrientedBoard box)
        {
            box = default;
            var allTriangleIndices = new List<int>(triangles.Count);
            for (int i = 0; i < triangles.Count; i++)
                allTriangleIndices.Add(i);

            var normalAxes = new List<WeightedAxis>();
            const float sameDirection = 0.985f;
            for (int i = 0; i < triangles.Count; i++)
            {
                Vector3 normal = triangles[i].Normal;
                int matchingAxis = -1;
                for (int axisIndex = 0; axisIndex < normalAxes.Count; axisIndex++)
                {
                    if (Mathf.Abs(Vector3.Dot(normal, normalAxes[axisIndex].Axis)) >= sameDirection)
                    {
                        matchingAxis = axisIndex;
                        break;
                    }
                }

                if (matchingAxis < 0)
                {
                    normalAxes.Add(new WeightedAxis(normal, triangles[i].Area));
                }
                else
                {
                    WeightedAxis existing = normalAxes[matchingAxis];
                    if (Vector3.Dot(normal, existing.Axis) < 0f)
                        normal = -normal;
                    Vector3 accumulated = existing.Axis * existing.Weight + normal * triangles[i].Area;
                    normalAxes[matchingAxis] = new WeightedAxis(
                        accumulated.sqrMagnitude > 0.00000001f ? accumulated.normalized : existing.Axis,
                        existing.Weight + triangles[i].Area);
                }
            }

            normalAxes.Sort((left, right) => right.Weight.CompareTo(left.Weight));
            if (normalAxes.Count > 16)
                normalAxes.RemoveRange(16, normalAxes.Count - 16);

            float bestScore = float.NegativeInfinity;
            for (int primaryIndex = 0; primaryIndex < normalAxes.Count; primaryIndex++)
            for (int secondaryIndex = primaryIndex + 1; secondaryIndex < normalAxes.Count; secondaryIndex++)
            {
                Vector3 axisY = normalAxes[primaryIndex].Axis;
                Vector3 secondaryNormal = normalAxes[secondaryIndex].Axis;
                if (Mathf.Abs(Vector3.Dot(axisY, secondaryNormal)) > 0.42f)
                    continue;

                Vector3 axisX = (secondaryNormal - axisY * Vector3.Dot(secondaryNormal, axisY)).normalized;
                if (axisX.sqrMagnitude < 0.5f)
                    continue;
                Vector3 axisZ = Vector3.Cross(axisX, axisY).normalized;

                if (!EvaluateBoxCandidate(
                        triangles,
                        allTriangleIndices,
                        axisX,
                        axisY,
                        axisZ,
                        meshScale,
                        minimumThickness,
                        out OrientedBoard candidate,
                        out float score) ||
                    score <= bestScore)
                    continue;

                bestScore = score;
                box = candidate;
            }

            return bestScore > float.NegativeInfinity;
        }

        private static bool EvaluateBoxCandidate(
            List<SurfaceTriangle> triangles,
            List<int> allTriangleIndices,
            Vector3 axisX,
            Vector3 axisY,
            Vector3 axisZ,
            float meshScale,
            float minimumThickness,
            out OrientedBoard box,
            out float score)
        {
            box = default;
            score = float.NegativeInfinity;
            ProjectedBounds bounds = CalculateProjectedBounds(triangles, allTriangleIndices, axisX, axisY, axisZ);
            Vector3 size = new Vector3(
                bounds.MaxU - bounds.MinU,
                bounds.MaxN - bounds.MinN,
                bounds.MaxV - bounds.MinV);
            if (size.x <= 0.000001f || size.y <= minimumThickness * 0.2f || size.z <= 0.000001f)
                return false;

            float expectedSurfaceArea = 2f * (size.x * size.y + size.x * size.z + size.y * size.z);
            float totalArea = 0f;
            float outerPlaneArea = 0f;
            float alignedNormalArea = 0f;
            float planeTolerance = Mathf.Max(meshScale * 0.0025f, Mathf.Min(size.x, Mathf.Min(size.y, size.z)) * 0.06f);
            float minimumAlignment = Mathf.Cos(16f * Mathf.Deg2Rad);

            for (int i = 0; i < triangles.Count; i++)
            {
                SurfaceTriangle triangle = triangles[i];
                totalArea += triangle.Area;
                float u = Vector3.Dot(triangle.Centroid, axisX);
                float n = Vector3.Dot(triangle.Centroid, axisY);
                float v = Vector3.Dot(triangle.Centroid, axisZ);
                float nearestPlane = Mathf.Min(
                    Mathf.Min(Mathf.Abs(u - bounds.MinU), Mathf.Abs(bounds.MaxU - u)),
                    Mathf.Min(
                        Mathf.Min(Mathf.Abs(n - bounds.MinN), Mathf.Abs(bounds.MaxN - n)),
                        Mathf.Min(Mathf.Abs(v - bounds.MinV), Mathf.Abs(bounds.MaxV - v))));
                if (nearestPlane <= planeTolerance)
                    outerPlaneArea += triangle.Area;

                float alignment = Mathf.Max(
                    Mathf.Abs(Vector3.Dot(triangle.Normal, axisX)),
                    Mathf.Max(
                        Mathf.Abs(Vector3.Dot(triangle.Normal, axisY)),
                        Mathf.Abs(Vector3.Dot(triangle.Normal, axisZ))));
                if (alignment >= minimumAlignment)
                    alignedNormalArea += triangle.Area;
            }

            float surfaceRatio = totalArea / Mathf.Max(expectedSurfaceArea, 0.0000001f);
            float outerRatio = outerPlaneArea / Mathf.Max(totalArea, 0.0000001f);
            float alignedRatio = alignedNormalArea / Mathf.Max(totalArea, 0.0000001f);
            if (surfaceRatio < 0.52f || surfaceRatio > 1.65f ||
                outerRatio < 0.74f ||
                alignedRatio < 0.72f)
                return false;

            Vector3 center = axisX * ((bounds.MinU + bounds.MaxU) * 0.5f) +
                             axisY * ((bounds.MinN + bounds.MaxN) * 0.5f) +
                             axisZ * ((bounds.MinV + bounds.MaxV) * 0.5f);
            box = OrientedBoard.CreateBox(center, Quaternion.LookRotation(axisZ, axisY), size);
            float surfaceScore = 1f - Mathf.Min(1f, Mathf.Abs(1f - surfaceRatio));
            score = outerRatio * 0.45f + alignedRatio * 0.4f + surfaceScore * 0.15f;
            return true;
        }

        private static bool TryFitSinglePlanarBoard(
            List<SurfaceTriangle> triangles,
            float meshScale,
            float minimumThickness,
            out OrientedBoard board)
        {
            board = default;
            Vector3 referenceNormal = triangles[0].Normal;
            float parallelThreshold = Mathf.Cos(8f * Mathf.Deg2Rad);
            for (int i = 1; i < triangles.Count; i++)
            {
                if (Mathf.Abs(Vector3.Dot(referenceNormal, triangles[i].Normal)) < parallelThreshold)
                    return false;
            }

            var allTriangleIndices = new List<int>(triangles.Count);
            for (int i = 0; i < triangles.Count; i++)
                allTriangleIndices.Add(i);

            var settings = new BoardFittingSettings(
                meshScale,
                minimumThickness,
                Mathf.Max(meshScale / 400f, minimumThickness * 1.5f),
                8f,
                0.8f,
                0.02f,
                true);
            BoardFit fit = FitBoard(triangles, allTriangleIndices, settings);
            if (fit.Coverage < 0.78f || fit.UnsupportedFraction > 0.008f)
                return false;

            board = OrientedBoard.CreateBox(fit.Center, fit.Rotation, fit.Size);
            return true;
        }

        private static List<SurfaceTriangle> CreateSurfaceTriangles(Vector3[] vertices, int[] indices)
        {
            var result = new List<SurfaceTriangle>(indices.Length / 3);
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int aIndex = indices[i];
                int bIndex = indices[i + 1];
                int cIndex = indices[i + 2];
                if ((uint)aIndex >= (uint)vertices.Length ||
                    (uint)bIndex >= (uint)vertices.Length ||
                    (uint)cIndex >= (uint)vertices.Length)
                    continue;

                Vector3 a = vertices[aIndex];
                Vector3 b = vertices[bIndex];
                Vector3 c = vertices[cIndex];
                Vector3 cross = Vector3.Cross(b - a, c - a);
                float doubleArea = cross.magnitude;
                if (doubleArea <= 0.000000001f)
                    continue;

                result.Add(new SurfaceTriangle(
                    aIndex,
                    bIndex,
                    cIndex,
                    a,
                    b,
                    c,
                    cross / doubleArea,
                    doubleArea * 0.5f,
                    (a + b + c) / 3f));
            }

            return result;
        }

        private static List<int>[] BuildSurfaceAdjacency(
            Vector3[] vertices,
            List<SurfaceTriangle> triangles,
            float weldTolerance)
        {
            float inverseTolerance = 1f / weldTolerance;
            var weldedByPosition = new Dictionary<QuantizedVertex, int>(vertices.Length);
            var weldedIndices = new int[vertices.Length];
            int nextWeldedIndex = 0;

            for (int i = 0; i < vertices.Length; i++)
            {
                var key = new QuantizedVertex(vertices[i], inverseTolerance);
                if (!weldedByPosition.TryGetValue(key, out int weldedIndex))
                {
                    weldedIndex = nextWeldedIndex++;
                    weldedByPosition.Add(key, weldedIndex);
                }

                weldedIndices[i] = weldedIndex;
            }

            var edgeOwners = new Dictionary<SurfaceEdge, List<int>>(triangles.Count * 2);
            for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
            {
                SurfaceTriangle triangle = triangles[triangleIndex];
                AddEdgeOwner(edgeOwners, new SurfaceEdge(weldedIndices[triangle.A], weldedIndices[triangle.B]), triangleIndex);
                AddEdgeOwner(edgeOwners, new SurfaceEdge(weldedIndices[triangle.B], weldedIndices[triangle.C]), triangleIndex);
                AddEdgeOwner(edgeOwners, new SurfaceEdge(weldedIndices[triangle.C], weldedIndices[triangle.A]), triangleIndex);
            }

            var adjacency = new List<int>[triangles.Count];
            foreach (List<int> owners in edgeOwners.Values)
            {
                if (owners.Count < 2)
                    continue;

                for (int i = 0; i < owners.Count; i++)
                for (int j = i + 1; j < owners.Count; j++)
                {
                    adjacency[owners[i]] ??= new List<int>(3);
                    adjacency[owners[j]] ??= new List<int>(3);
                    adjacency[owners[i]].Add(owners[j]);
                    adjacency[owners[j]].Add(owners[i]);
                }
            }

            return adjacency;
        }

        private static void AddEdgeOwner(
            Dictionary<SurfaceEdge, List<int>> edgeOwners,
            SurfaceEdge edge,
            int triangleIndex)
        {
            if (!edgeOwners.TryGetValue(edge, out List<int> owners))
            {
                owners = new List<int>(2);
                edgeOwners.Add(edge, owners);
            }

            owners.Add(triangleIndex);
        }

        private static List<List<int>> SegmentConnectedComponents(
            List<SurfaceTriangle> triangles,
            List<int>[] adjacency)
        {
            var visited = new bool[triangles.Count];
            var queue = new Queue<int>();
            var components = new List<List<int>>();

            for (int start = 0; start < triangles.Count; start++)
            {
                if (visited[start])
                    continue;

                var component = new List<int>();
                visited[start] = true;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    component.Add(current);
                    List<int> neighbours = adjacency[current];
                    if (neighbours == null)
                        continue;

                    for (int i = 0; i < neighbours.Count; i++)
                    {
                        int neighbour = neighbours[i];
                        if (visited[neighbour])
                            continue;
                        visited[neighbour] = true;
                        queue.Enqueue(neighbour);
                    }
                }

                components.Add(component);
            }

            return components;
        }

        private static List<List<int>> SegmentSurfaceRegions(
            List<SurfaceTriangle> triangles,
            List<int>[] adjacency,
            float maximumAngle,
            bool[] excluded)
        {
            float minimumNormalDot = Mathf.Cos(maximumAngle * Mathf.Deg2Rad);
            var assigned = new bool[triangles.Count];
            var queue = new Queue<int>();
            var regions = new List<List<int>>();

            for (int start = 0; start < triangles.Count; start++)
            {
                if (assigned[start] || excluded[start])
                    continue;

                var region = new List<int>();
                Vector3 seedNormal = triangles[start].Normal;
                Vector3 accumulatedNormal = seedNormal * triangles[start].Area;
                assigned[start] = true;
                queue.Enqueue(start);

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    region.Add(current);
                    List<int> neighbours = adjacency[current];
                    if (neighbours == null)
                        continue;

                    Vector3 averageNormal = accumulatedNormal.sqrMagnitude > 0.00000001f
                        ? accumulatedNormal.normalized
                        : seedNormal;
                    for (int i = 0; i < neighbours.Count; i++)
                    {
                        int neighbour = neighbours[i];
                        if (assigned[neighbour] || excluded[neighbour])
                            continue;

                        Vector3 neighbourNormal = triangles[neighbour].Normal;
                        float averageDot = Vector3.Dot(neighbourNormal, averageNormal);
                        float seedDot = Vector3.Dot(neighbourNormal, seedNormal);
                        if (averageDot < minimumNormalDot || seedDot < minimumNormalDot)
                            continue;

                        assigned[neighbour] = true;
                        queue.Enqueue(neighbour);
                        accumulatedNormal += neighbourNormal * triangles[neighbour].Area;
                    }
                }

                regions.Add(region);
            }

            return regions;
        }

        private static BoardFit FitBoard(
            List<SurfaceTriangle> triangles,
            IReadOnlyList<int> triangleIndices,
            BoardFittingSettings settings)
        {
            SurfaceTriangle first = triangles[triangleIndices[0]];
            Vector3 referenceNormal = first.Normal;
            Vector3 normalSum = Vector3.zero;
            for (int i = 0; i < triangleIndices.Count; i++)
            {
                SurfaceTriangle triangle = triangles[triangleIndices[i]];
                normalSum += triangle.Normal * triangle.Area;
            }

            Vector3 boardNormal = normalSum.sqrMagnitude > 0.00000001f
                ? normalSum.normalized
                : referenceNormal;
            Vector3 referenceAxis = Mathf.Abs(boardNormal.y) < 0.85f ? Vector3.up : Vector3.right;
            Vector3 baseU = Vector3.Cross(referenceAxis, boardNormal).normalized;
            if (baseU.sqrMagnitude < 0.5f)
                baseU = Vector3.Cross(Vector3.forward, boardNormal).normalized;
            Vector3 baseV = Vector3.Cross(baseU, boardNormal).normalized;

            var orientationCandidates = new Dictionary<int, float> { { 0, 0f } };
            for (int i = 0; i < triangleIndices.Count; i++)
            {
                SurfaceTriangle triangle = triangles[triangleIndices[i]];
                AddOrientationCandidate(orientationCandidates, triangle.BPosition - triangle.APosition, boardNormal, baseU, baseV);
                AddOrientationCandidate(orientationCandidates, triangle.CPosition - triangle.BPosition, boardNormal, baseU, baseV);
                AddOrientationCandidate(orientationCandidates, triangle.APosition - triangle.CPosition, boardNormal, baseU, baseV);
            }

            float bestMetric = float.PositiveInfinity;
            Vector3 bestU = baseU;
            Vector3 bestV = baseV;
            ProjectedBounds bestBounds = default;
            foreach (float angle in orientationCandidates.Values)
            {
                float radians = angle * Mathf.Deg2Rad;
                Vector3 u = (baseU * Mathf.Cos(radians) + baseV * Mathf.Sin(radians)).normalized;
                Vector3 v = Vector3.Cross(u, boardNormal).normalized;
                ProjectedBounds projected = CalculateProjectedBounds(triangles, triangleIndices, u, boardNormal, v);
                float width = projected.MaxU - projected.MinU;
                float height = projected.MaxV - projected.MinV;
                float metric = width * height + (width + height) * settings.MeshScale * 0.000001f;
                if (metric < bestMetric)
                {
                    bestMetric = metric;
                    bestU = u;
                    bestV = v;
                    bestBounds = projected;
                }
            }

            float sizeU = Mathf.Max(0.000001f, bestBounds.MaxU - bestBounds.MinU);
            float sizeV = Mathf.Max(0.000001f, bestBounds.MaxV - bestBounds.MinV);
            float normalRange = Mathf.Max(0f, bestBounds.MaxN - bestBounds.MinN);
            float maximumPlanarSize = Mathf.Max(sizeU, sizeV);
            float localMinimumThickness = settings.Automatic
                ? Mathf.Max(
                    settings.MeshScale * 0.0002f,
                    Mathf.Min(settings.BaseThickness, maximumPlanarSize * 0.012f))
                : settings.BaseThickness;
            float thickness = Mathf.Max(localMinimumThickness, normalRange + localMinimumThickness * 0.35f);

            float projectedArea = 0f;
            float maximumNormalAngle = 0f;
            for (int i = 0; i < triangleIndices.Count; i++)
            {
                SurfaceTriangle triangle = triangles[triangleIndices[i]];
                projectedArea += triangle.Area * Mathf.Abs(Vector3.Dot(triangle.Normal, boardNormal));
                maximumNormalAngle = Mathf.Max(
                    maximumNormalAngle,
                    Mathf.Acos(Mathf.Clamp(Vector3.Dot(triangle.Normal, boardNormal), -1f, 1f)) * Mathf.Rad2Deg);
            }

            float rectangleArea = Mathf.Max(0.0000000001f, sizeU * sizeV);
            float coverage = Mathf.Clamp01(projectedArea / rectangleArea);
            float unsupportedFraction = CalculateLargestUnsupportedFraction(
                triangles,
                triangleIndices,
                bestU,
                bestV,
                bestBounds,
                sizeU,
                sizeV);
            float curvatureReference = Mathf.Max(localMinimumThickness, Mathf.Min(sizeU, sizeV));
            float curvature = normalRange / curvatureReference;
            float coverageError = Mathf.Max(0f, settings.TargetCoverage - coverage) / settings.TargetCoverage;
            float curvatureError = Mathf.Max(0f, curvature - settings.MaximumCurvature) * 5f;
            float normalError = Mathf.Max(0f, maximumNormalAngle - settings.MergeAngle * 0.7f) /
                                Mathf.Max(1f, settings.MergeAngle) * 0.35f;
            float cost = rectangleArea * (coverageError + curvatureError + normalError + unsupportedFraction * 2f);
            bool needsRefinement = triangleIndices.Count > 1 &&
                                   maximumPlanarSize > settings.MinimumBoardSpan * 2f &&
                                   (coverage < settings.TargetCoverage ||
                                    unsupportedFraction > 0.012f ||
                                    curvature > settings.MaximumCurvature ||
                                    maximumNormalAngle > settings.MergeAngle * 0.8f);

            Vector3 center = bestU * ((bestBounds.MinU + bestBounds.MaxU) * 0.5f) +
                             boardNormal * ((bestBounds.MinN + bestBounds.MaxN) * 0.5f) +
                             bestV * ((bestBounds.MinV + bestBounds.MaxV) * 0.5f);
            Quaternion rotation = Quaternion.LookRotation(bestV, boardNormal);

            return new BoardFit(
                center,
                rotation,
                new Vector3(sizeU, thickness, sizeV),
                bestU,
                boardNormal,
                bestV,
                rectangleArea,
                coverage,
                unsupportedFraction,
                curvature,
                maximumNormalAngle,
                cost,
                needsRefinement);
        }

        private static void AddOrientationCandidate(
            Dictionary<int, float> candidates,
            Vector3 edge,
            Vector3 normal,
            Vector3 baseU,
            Vector3 baseV)
        {
            Vector3 projected = edge - normal * Vector3.Dot(edge, normal);
            if (projected.sqrMagnitude < 0.0000000001f)
                return;

            float angle = Mathf.Atan2(Vector3.Dot(projected, baseV), Vector3.Dot(projected, baseU)) * Mathf.Rad2Deg;
            angle %= 90f;
            if (angle < 0f)
                angle += 90f;

            const float bucketSize = 4f;
            int bucket = Mathf.RoundToInt(angle / bucketSize);
            if (!candidates.ContainsKey(bucket))
                candidates.Add(bucket, angle);
        }

        private static ProjectedBounds CalculateProjectedBounds(
            List<SurfaceTriangle> triangles,
            IReadOnlyList<int> triangleIndices,
            Vector3 u,
            Vector3 n,
            Vector3 v)
        {
            var bounds = ProjectedBounds.Empty;
            for (int i = 0; i < triangleIndices.Count; i++)
            {
                SurfaceTriangle triangle = triangles[triangleIndices[i]];
                bounds.Encapsulate(triangle.APosition, u, n, v);
                bounds.Encapsulate(triangle.BPosition, u, n, v);
                bounds.Encapsulate(triangle.CPosition, u, n, v);
            }

            return bounds;
        }

        private static float CalculateLargestUnsupportedFraction(
            List<SurfaceTriangle> triangles,
            IReadOnlyList<int> triangleIndices,
            Vector3 axisU,
            Vector3 axisV,
            ProjectedBounds bounds,
            float sizeU,
            float sizeV)
        {
            const int gridSize = 16;
            if (sizeU <= 0.000001f || sizeV <= 0.000001f)
                return 0f;

            float cellU = sizeU / gridSize;
            float cellV = sizeV / gridSize;
            var supported = new bool[gridSize * gridSize];

            for (int i = 0; i < triangleIndices.Count; i++)
            {
                SurfaceTriangle triangle = triangles[triangleIndices[i]];
                Vector2 a = new Vector2(Vector3.Dot(triangle.APosition, axisU), Vector3.Dot(triangle.APosition, axisV));
                Vector2 b = new Vector2(Vector3.Dot(triangle.BPosition, axisU), Vector3.Dot(triangle.BPosition, axisV));
                Vector2 c = new Vector2(Vector3.Dot(triangle.CPosition, axisU), Vector3.Dot(triangle.CPosition, axisV));
                float triangleMinU = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
                float triangleMaxU = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
                float triangleMinV = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
                float triangleMaxV = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
                int minimumX = Mathf.Clamp(Mathf.FloorToInt((triangleMinU - bounds.MinU) / cellU), 0, gridSize - 1);
                int maximumX = Mathf.Clamp(Mathf.FloorToInt((triangleMaxU - bounds.MinU) / cellU), 0, gridSize - 1);
                int minimumY = Mathf.Clamp(Mathf.FloorToInt((triangleMinV - bounds.MinV) / cellV), 0, gridSize - 1);
                int maximumY = Mathf.Clamp(Mathf.FloorToInt((triangleMaxV - bounds.MinV) / cellV), 0, gridSize - 1);

                for (int y = minimumY; y <= maximumY; y++)
                for (int x = minimumX; x <= maximumX; x++)
                {
                    Vector2 center = new Vector2(
                        bounds.MinU + (x + 0.5f) * cellU,
                        bounds.MinV + (y + 0.5f) * cellV);
                    if (TriangleOverlapsRectangle(a, b, c, center, new Vector2(cellU * 0.5f, cellV * 0.5f)))
                        supported[x + y * gridSize] = true;
                }
            }

            var emptyHeights = new int[gridSize];
            int largestEmptyRectangle = 0;
            for (int y = 0; y < gridSize; y++)
            {
                for (int x = 0; x < gridSize; x++)
                    emptyHeights[x] = supported[x + y * gridSize] ? 0 : emptyHeights[x] + 1;

                for (int left = 0; left < gridSize; left++)
                {
                    int minimumHeight = int.MaxValue;
                    for (int right = left; right < gridSize; right++)
                    {
                        minimumHeight = Mathf.Min(minimumHeight, emptyHeights[right]);
                        if (minimumHeight == 0)
                            break;
                        largestEmptyRectangle = Mathf.Max(largestEmptyRectangle, minimumHeight * (right - left + 1));
                    }
                }
            }

            return largestEmptyRectangle / (float)(gridSize * gridSize);
        }

        private static bool TriangleOverlapsRectangle(
            Vector2 a,
            Vector2 b,
            Vector2 c,
            Vector2 center,
            Vector2 halfSize)
        {
            Vector2 localA = a - center;
            Vector2 localB = b - center;
            Vector2 localC = c - center;
            if (!OverlapsAxis2D(Vector2.right, localA, localB, localC, halfSize) ||
                !OverlapsAxis2D(Vector2.up, localA, localB, localC, halfSize))
                return false;

            Vector2 edgeA = localB - localA;
            Vector2 edgeB = localC - localB;
            Vector2 edgeC = localA - localC;
            return OverlapsAxis2D(new Vector2(-edgeA.y, edgeA.x), localA, localB, localC, halfSize) &&
                   OverlapsAxis2D(new Vector2(-edgeB.y, edgeB.x), localA, localB, localC, halfSize) &&
                   OverlapsAxis2D(new Vector2(-edgeC.y, edgeC.x), localA, localB, localC, halfSize);
        }

        private static bool OverlapsAxis2D(
            Vector2 axis,
            Vector2 a,
            Vector2 b,
            Vector2 c,
            Vector2 halfSize)
        {
            if (axis.sqrMagnitude <= 0.0000000001f)
                return true;

            float projectionA = Vector2.Dot(a, axis);
            float projectionB = Vector2.Dot(b, axis);
            float projectionC = Vector2.Dot(c, axis);
            float minimum = Mathf.Min(projectionA, Mathf.Min(projectionB, projectionC));
            float maximum = Mathf.Max(projectionA, Mathf.Max(projectionB, projectionC));
            float radius = halfSize.x * Mathf.Abs(axis.x) + halfSize.y * Mathf.Abs(axis.y);
            return minimum <= radius && maximum >= -radius;
        }

        private static SplitCandidate FindBestSplit(
            List<SurfaceTriangle> triangles,
            BoardCluster cluster,
            BoardFittingSettings settings)
        {
            if (!cluster.Fit.NeedsRefinement || cluster.Triangles.Count < 2)
                return default;

            SplitCandidate best = default;
            EvaluateSplitAxis(triangles, cluster, settings, cluster.Fit.AxisU, ref best);
            EvaluateSplitAxis(triangles, cluster, settings, cluster.Fit.AxisV, ref best);
            return best;
        }

        private static void EvaluateSplitAxis(
            List<SurfaceTriangle> triangles,
            BoardCluster cluster,
            BoardFittingSettings settings,
            Vector3 axis,
            ref SplitCandidate best)
        {
            var sorted = new List<int>(cluster.Triangles);
            sorted.Sort((left, right) =>
                Vector3.Dot(triangles[left].Centroid, axis).CompareTo(Vector3.Dot(triangles[right].Centroid, axis)));

            var cuts = new HashSet<int>
            {
                sorted.Count / 2,
                sorted.Count / 3,
                sorted.Count * 2 / 3
            };

            float totalArea = 0f;
            for (int i = 0; i < sorted.Count; i++)
                totalArea += triangles[sorted[i]].Area;

            float accumulatedArea = 0f;
            int nextAreaQuartile = 1;
            for (int i = 0; i < sorted.Count - 1 && nextAreaQuartile <= 3; i++)
            {
                accumulatedArea += triangles[sorted[i]].Area;
                if (accumulatedArea < totalArea * (nextAreaQuartile * 0.25f))
                    continue;

                cuts.Add(i + 1);
                nextAreaQuartile++;
            }

            float largestGap = 0f;
            int largestGapCut = -1;
            int minimumSideCount = Mathf.Max(1, sorted.Count / 8);
            for (int i = minimumSideCount; i <= sorted.Count - minimumSideCount; i++)
            {
                if (i <= 0 || i >= sorted.Count)
                    continue;

                float previous = Vector3.Dot(triangles[sorted[i - 1]].Centroid, axis);
                float next = Vector3.Dot(triangles[sorted[i]].Centroid, axis);
                float gap = next - previous;
                if (gap > largestGap)
                {
                    largestGap = gap;
                    largestGapCut = i;
                }
            }

            if (largestGapCut > 0)
                cuts.Add(largestGapCut);

            foreach (int cut in cuts)
            {
                if (cut <= 0 || cut >= sorted.Count)
                    continue;

                var leftTriangles = sorted.GetRange(0, cut);
                var rightTriangles = sorted.GetRange(cut, sorted.Count - cut);
                BoardFit leftFit = FitBoard(triangles, leftTriangles, settings);
                BoardFit rightFit = FitBoard(triangles, rightTriangles, settings);
                float childCost = leftFit.Cost + rightFit.Cost;
                float improvement = cluster.Fit.Cost - childCost;

                float parentSpan = Mathf.Max(cluster.Fit.Size.x, cluster.Fit.Size.z);
                float cutGap = Vector3.Dot(triangles[sorted[cut]].Centroid, axis) -
                               Vector3.Dot(triangles[sorted[cut - 1]].Centroid, axis);
                float normalizedImprovement = improvement / Mathf.Max(cluster.Fit.RectangleArea, 0.0000001f);
                float gapBonus = parentSpan > 0.000001f
                    ? Mathf.Max(0f, cutGap) / parentSpan * 0.15f
                    : 0f;
                float coverageDeficit = Mathf.Max(0f, settings.TargetCoverage - cluster.Fit.Coverage);
                float curvatureDeficit = Mathf.Max(0f, cluster.Fit.Curvature - settings.MaximumCurvature);

                // A ring or U-shaped patch may need one neutral split before the following
                // split exposes its opening. Keep a small look-ahead allowance for those
                // concave silhouettes instead of accepting one large box across the hole.
                float explorationBonus = coverageDeficit * 0.08f +
                                         cluster.Fit.UnsupportedFraction * 2f +
                                         Mathf.Min(curvatureDeficit, 1f) * 0.1f;
                float priority = normalizedImprovement + gapBonus + explorationBonus;
                const float minimumUsefulImprovement = 0.0025f;
                // A surface with a real void must be allowed to take a temporarily neutral
                // split. Otherwise an annulus remains one square plate because its first
                // two halves still contain part of the same opening.
                if (cluster.Fit.UnsupportedFraction <= 0.012f &&
                    childCost > cluster.Fit.Cost + cluster.Fit.RectangleArea * 0.12f)
                    continue;
                if (priority <= minimumUsefulImprovement || (best.IsValid && priority <= best.Priority))
                    continue;

                best = new SplitCandidate(
                    leftTriangles,
                    rightTriangles,
                    leftFit,
                    rightFit,
                    priority);
            }
        }

        private static bool TryReadMesh(Mesh mesh, out Vector3[] vertices, out int[] triangles, out string error)
        {
            vertices = Array.Empty<Vector3>();
            triangles = Array.Empty<int>();
            error = null;

            try
            {
                using (Mesh.MeshDataArray meshDataArray = Mesh.AcquireReadOnlyMeshData(mesh))
                {
                    Mesh.MeshData meshData = meshDataArray[0];
                    using (var vertexData = new NativeArray<Vector3>(meshData.vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
                    {
                        meshData.GetVertices(vertexData);
                        vertices = vertexData.ToArray();
                    }

                    var allTriangles = new List<int>();
                    for (int subMesh = 0; subMesh < meshData.subMeshCount; subMesh++)
                    {
                        SubMeshDescriptor descriptor = meshData.GetSubMesh(subMesh);
                        if (descriptor.topology != MeshTopology.Triangles || descriptor.indexCount < 3)
                            continue;

                        using (var indices = new NativeArray<int>(descriptor.indexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
                        {
                            meshData.GetIndices(indices, subMesh, true);
                            for (int i = 0; i < indices.Length; i++)
                                allTriangles.Add(indices[i]);
                        }
                    }

                    triangles = allTriangles.ToArray();
                }

                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static List<MeshSource> CollectSources(
            GameObject[] roots,
            bool includeInactiveObjects,
            bool skipNonSolidVisuals)
        {
            var sources = new List<MeshSource>();
            var keys = new HashSet<SourceKey>();

            foreach (GameObject root in roots)
            {
                GameObject generationRoot = ResolveGenerationRoot(root);
                foreach (MeshCollider meshCollider in root.GetComponentsInChildren<MeshCollider>(includeInactiveObjects))
                {
                    if (meshCollider.sharedMesh == null ||
                        IsGenerated(meshCollider.transform) ||
                        (skipNonSolidVisuals && IsNamedNonSolidVisual(meshCollider.transform, meshCollider.sharedMesh)))
                        continue;

                    SourceKey key = new SourceKey(meshCollider.transform, meshCollider.sharedMesh);
                    if (keys.Add(key))
                        sources.Add(new MeshSource(generationRoot, meshCollider.transform, meshCollider.sharedMesh, meshCollider));
                }

                foreach (MeshFilter meshFilter in root.GetComponentsInChildren<MeshFilter>(includeInactiveObjects))
                {
                    if (meshFilter.sharedMesh == null ||
                        IsGenerated(meshFilter.transform) ||
                        (skipNonSolidVisuals && IsNamedNonSolidVisual(meshFilter.transform, meshFilter.sharedMesh)))
                        continue;

                    // A dedicated MeshCollider is a more intentional collision source than the render mesh.
                    // Avoid generating both when their meshes differ.
                    MeshCollider sameObjectCollider = meshFilter.GetComponent<MeshCollider>();
                    if (sameObjectCollider != null && sameObjectCollider.sharedMesh != null &&
                        sameObjectCollider.sharedMesh != meshFilter.sharedMesh)
                        continue;

                    SourceKey key = new SourceKey(meshFilter.transform, meshFilter.sharedMesh);
                    if (keys.Add(key))
                    {
                        sources.Add(new MeshSource(generationRoot, meshFilter.transform, meshFilter.sharedMesh,
                            sameObjectCollider != null && sameObjectCollider.sharedMesh == meshFilter.sharedMesh ? sameObjectCollider : null));
                    }
                }
            }

            return sources;
        }

        private static bool IsNamedNonSolidVisual(Transform source, Mesh mesh)
        {
            string combinedName = (source != null ? source.name : string.Empty) + " " +
                                  (mesh != null ? mesh.name : string.Empty);
            return combinedName.IndexOf("sail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combinedName.IndexOf("flag", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combinedName.IndexOf("banner", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combinedName.IndexOf("cloth", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static GameObject ResolveGenerationRoot(GameObject selectedRoot)
        {
            PrefabStage prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null &&
                prefabStage.prefabContentsRoot != null &&
                selectedRoot.scene == prefabStage.scene)
                return prefabStage.prefabContentsRoot;

            return selectedRoot;
        }

        private static GameObject[] GetGenerationRoots(GameObject[] selectedRoots)
        {
            var result = new List<GameObject>(selectedRoots.Length);
            var seen = new HashSet<int>();
            for (int i = 0; i < selectedRoots.Length; i++)
            {
                GameObject root = ResolveGenerationRoot(selectedRoots[i]);
                if (root != null && seen.Add(root.GetInstanceID()))
                    result.Add(root);
            }

            return result.ToArray();
        }

        private static bool IsGenerated(Transform transform)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                if (current.name.StartsWith(GeneratedPrefix, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static Transform GetOrCreateGeneratedRoot(
            GameObject sourceRoot,
            Dictionary<int, Transform> generatedRoots)
        {
            int rootId = sourceRoot.GetInstanceID();
            if (generatedRoots.TryGetValue(rootId, out Transform existing) && existing != null)
                return existing;

            var rootObject = new GameObject(GeneratedPrefix);
            Undo.RegisterCreatedObjectUndo(rootObject, "Create Generated Collision Root");
            rootObject.layer = sourceRoot.layer;
            Transform rootTransform = rootObject.transform;
            rootTransform.SetParent(sourceRoot.transform, false);
            rootTransform.localPosition = Vector3.zero;
            rootTransform.localRotation = Quaternion.identity;
            rootTransform.localScale = Vector3.one;
            generatedRoots.Add(rootId, rootTransform);
            return rootTransform;
        }

        private static void CopyRelativeTransform(Transform source, Transform generatedRoot, Transform destination)
        {
            Matrix4x4 relative = generatedRoot.worldToLocalMatrix * source.localToWorldMatrix;
            destination.localPosition = relative.GetColumn(3);
            destination.localRotation = relative.rotation;
            destination.localScale = relative.lossyScale;
        }

        private static string GetGeneratedName(Transform source, Mesh mesh)
        {
            string sourceName = string.IsNullOrWhiteSpace(source.name) ? "Source" : source.name.Replace('/', '_').Replace('\\', '_');
            string meshName = string.IsNullOrWhiteSpace(mesh.name) ? "Mesh" : mesh.name.Replace('/', '_').Replace('\\', '_');
            return $"Collision_{sourceName}_{meshName}_{mesh.GetInstanceID()}";
        }

        private static int RemoveGenerated(GameObject[] roots)
        {
            var objectsToRemove = new List<GameObject>();
            foreach (GameObject root in roots)
            {
                Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
                foreach (Transform transform in transforms)
                {
                    if (transform != root.transform && transform.name.StartsWith(GeneratedPrefix, StringComparison.Ordinal))
                        objectsToRemove.Add(transform.gameObject);
                }
            }

            // Remove deepest objects first in case selections overlap unexpectedly.
            objectsToRemove.Sort((a, b) => GetDepth(b.transform).CompareTo(GetDepth(a.transform)));
            var removedIds = new HashSet<int>();
            int removed = 0;
            foreach (GameObject target in objectsToRemove)
            {
                if (target == null || !removedIds.Add(target.GetInstanceID()))
                    continue;
                Undo.DestroyObjectImmediate(target);
                removed++;
            }

            return removed;
        }

        private static int GetDepth(Transform transform)
        {
            int depth = 0;
            while (transform.parent != null)
            {
                depth++;
                transform = transform.parent;
            }

            return depth;
        }

        private static GameObject[] GetSelectionRoots()
        {
            GameObject[] selected = Selection.gameObjects ?? Array.Empty<GameObject>();
            var selectedSet = new HashSet<GameObject>(selected);
            var roots = new List<GameObject>();

            foreach (GameObject candidate in selected)
            {
                if (candidate == null || EditorUtility.IsPersistent(candidate))
                    continue;

                bool hasSelectedAncestor = false;
                for (Transform parent = candidate.transform.parent; parent != null; parent = parent.parent)
                {
                    if (selectedSet.Contains(parent.gameObject))
                    {
                        hasSelectedAncestor = true;
                        break;
                    }
                }

                if (!hasSelectedAncestor)
                    roots.Add(candidate);
            }

            return roots.ToArray();
        }

        private readonly struct SurfaceTriangle
        {
            public readonly int A;
            public readonly int B;
            public readonly int C;
            public readonly Vector3 APosition;
            public readonly Vector3 BPosition;
            public readonly Vector3 CPosition;
            public readonly Vector3 Normal;
            public readonly float Area;
            public readonly Vector3 Centroid;

            public SurfaceTriangle(
                int a,
                int b,
                int c,
                Vector3 aPosition,
                Vector3 bPosition,
                Vector3 cPosition,
                Vector3 normal,
                float area,
                Vector3 centroid)
            {
                A = a;
                B = b;
                C = c;
                APosition = aPosition;
                BPosition = bPosition;
                CPosition = cPosition;
                Normal = normal;
                Area = area;
                Centroid = centroid;
            }
        }

        private readonly struct QuantizedVertex : IEquatable<QuantizedVertex>
        {
            private readonly long x;
            private readonly long y;
            private readonly long z;

            public QuantizedVertex(Vector3 position, float inverseTolerance)
            {
                x = (long)Math.Round(position.x * inverseTolerance);
                y = (long)Math.Round(position.y * inverseTolerance);
                z = (long)Math.Round(position.z * inverseTolerance);
            }

            public bool Equals(QuantizedVertex other) => x == other.x && y == other.y && z == other.z;
            public override bool Equals(object obj) => obj is QuantizedVertex other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = x.GetHashCode();
                    hash = (hash * 397) ^ y.GetHashCode();
                    hash = (hash * 397) ^ z.GetHashCode();
                    return hash;
                }
            }
        }

        private readonly struct SurfaceEdge : IEquatable<SurfaceEdge>
        {
            private readonly int minimum;
            private readonly int maximum;

            public SurfaceEdge(int a, int b)
            {
                minimum = Mathf.Min(a, b);
                maximum = Mathf.Max(a, b);
            }

            public bool Equals(SurfaceEdge other) => minimum == other.minimum && maximum == other.maximum;
            public override bool Equals(object obj) => obj is SurfaceEdge other && Equals(other);
            public override int GetHashCode() => (minimum * 397) ^ maximum;
        }

        private struct ProjectedBounds
        {
            public float MinU;
            public float MaxU;
            public float MinN;
            public float MaxN;
            public float MinV;
            public float MaxV;

            public static ProjectedBounds Empty => new ProjectedBounds
            {
                MinU = float.PositiveInfinity,
                MaxU = float.NegativeInfinity,
                MinN = float.PositiveInfinity,
                MaxN = float.NegativeInfinity,
                MinV = float.PositiveInfinity,
                MaxV = float.NegativeInfinity
            };

            public void Encapsulate(Vector3 point, Vector3 u, Vector3 n, Vector3 v)
            {
                float projectedU = Vector3.Dot(point, u);
                float projectedN = Vector3.Dot(point, n);
                float projectedV = Vector3.Dot(point, v);
                MinU = Mathf.Min(MinU, projectedU);
                MaxU = Mathf.Max(MaxU, projectedU);
                MinN = Mathf.Min(MinN, projectedN);
                MaxN = Mathf.Max(MaxN, projectedN);
                MinV = Mathf.Min(MinV, projectedV);
                MaxV = Mathf.Max(MaxV, projectedV);
            }
        }

        private readonly struct BoardFittingSettings
        {
            public readonly float MeshScale;
            public readonly float BaseThickness;
            public readonly float MinimumBoardSpan;
            public readonly float MergeAngle;
            public readonly float TargetCoverage;
            public readonly float MaximumCurvature;
            public readonly bool Automatic;

            public BoardFittingSettings(
                float meshScale,
                float baseThickness,
                float minimumBoardSpan,
                float mergeAngle,
                float targetCoverage,
                float maximumCurvature,
                bool automatic)
            {
                MeshScale = meshScale;
                BaseThickness = baseThickness;
                MinimumBoardSpan = minimumBoardSpan;
                MergeAngle = mergeAngle;
                TargetCoverage = targetCoverage;
                MaximumCurvature = maximumCurvature;
                Automatic = automatic;
            }
        }

        private readonly struct ElongatedComponent
        {
            public readonly Vector3 Center;
            public readonly Vector3 Axis;
            public readonly float Length;
            public readonly float CrossRadius;
            public readonly bool IsElongated;

            public ElongatedComponent(
                Vector3 center,
                Vector3 axis,
                float length,
                float crossRadius,
                bool isElongated)
            {
                Center = center;
                Axis = axis;
                Length = length;
                CrossRadius = crossRadius;
                IsElongated = isElongated;
            }
        }

        private readonly struct WeightedAxis
        {
            public readonly Vector3 Axis;
            public readonly float Weight;

            public WeightedAxis(Vector3 axis, float weight)
            {
                Axis = axis;
                Weight = weight;
            }
        }

        private readonly struct BoardFit
        {
            public readonly Vector3 Center;
            public readonly Quaternion Rotation;
            public readonly Vector3 Size;
            public readonly Vector3 AxisU;
            public readonly Vector3 Normal;
            public readonly Vector3 AxisV;
            public readonly float RectangleArea;
            public readonly float Coverage;
            public readonly float UnsupportedFraction;
            public readonly float Curvature;
            public readonly float MaximumNormalAngle;
            public readonly float Cost;
            public readonly bool NeedsRefinement;

            public BoardFit(
                Vector3 center,
                Quaternion rotation,
                Vector3 size,
                Vector3 axisU,
                Vector3 normal,
                Vector3 axisV,
                float rectangleArea,
                float coverage,
                float unsupportedFraction,
                float curvature,
                float maximumNormalAngle,
                float cost,
                bool needsRefinement)
            {
                Center = center;
                Rotation = rotation;
                Size = size;
                AxisU = axisU;
                Normal = normal;
                AxisV = axisV;
                RectangleArea = rectangleArea;
                Coverage = coverage;
                UnsupportedFraction = unsupportedFraction;
                Curvature = curvature;
                MaximumNormalAngle = maximumNormalAngle;
                Cost = cost;
                NeedsRefinement = needsRefinement;
            }
        }

        private sealed class BoardCluster
        {
            public readonly List<int> Triangles;
            public readonly BoardFit Fit;
            public SplitCandidate Split;

            public BoardCluster(List<int> triangles, BoardFit fit)
            {
                Triangles = triangles;
                Fit = fit;
                Split = default;
            }
        }

        private readonly struct SplitCandidate
        {
            public readonly List<int> LeftTriangles;
            public readonly List<int> RightTriangles;
            public readonly BoardFit LeftFit;
            public readonly BoardFit RightFit;
            public readonly float Priority;

            public bool IsValid => LeftTriangles != null && RightTriangles != null;

            public SplitCandidate(
                List<int> leftTriangles,
                List<int> rightTriangles,
                BoardFit leftFit,
                BoardFit rightFit,
                float priority)
            {
                LeftTriangles = leftTriangles;
                RightTriangles = rightTriangles;
                LeftFit = leftFit;
                RightFit = rightFit;
                Priority = priority;
            }
        }

        private enum PrimitiveKind
        {
            Box,
            Capsule
        }

        private readonly struct OrientedBoard
        {
            public readonly PrimitiveKind Kind;
            public readonly Vector3 Center;
            public readonly Quaternion Rotation;
            public readonly Vector3 Size;
            public readonly float Radius;
            public readonly float Height;

            private OrientedBoard(
                PrimitiveKind kind,
                Vector3 center,
                Quaternion rotation,
                Vector3 size,
                float radius,
                float height)
            {
                Kind = kind;
                Center = center;
                Rotation = rotation;
                Size = size;
                Radius = radius;
                Height = height;
            }

            public static OrientedBoard CreateBox(Vector3 center, Quaternion rotation, Vector3 size)
            {
                return new OrientedBoard(PrimitiveKind.Box, center, rotation, size, 0f, 0f);
            }

            public static OrientedBoard CreateCapsule(Vector3 center, Quaternion rotation, float radius, float height)
            {
                return new OrientedBoard(
                    PrimitiveKind.Capsule,
                    center,
                    rotation,
                    new Vector3(radius * 2f, height, radius * 2f),
                    radius,
                    height);
            }
        }

        private readonly struct BoardGenerationResult
        {
            public static BoardGenerationResult Empty => new BoardGenerationResult(
                new List<OrientedBoard>(), 0f, 0f, 0, 0);

            public readonly List<OrientedBoard> Boards;
            public readonly float MeshScale;
            public readonly float MinimumThickness;
            public readonly int InitialRegionCount;
            public readonly int UnresolvedRegionCount;

            public BoardGenerationResult(
                List<OrientedBoard> boards,
                float meshScale,
                float minimumThickness,
                int initialRegionCount,
                int unresolvedRegionCount)
            {
                Boards = boards;
                MeshScale = meshScale;
                MinimumThickness = minimumThickness;
                InitialRegionCount = initialRegionCount;
                UnresolvedRegionCount = unresolvedRegionCount;
            }
        }

        private readonly struct MeshSource
        {
            public readonly GameObject Root;
            public readonly Transform Transform;
            public readonly Mesh Mesh;
            public readonly MeshCollider SourceCollider;

            public MeshSource(GameObject root, Transform transform, Mesh mesh, MeshCollider sourceCollider)
            {
                Root = root;
                Transform = transform;
                Mesh = mesh;
                SourceCollider = sourceCollider;
            }
        }

        private readonly struct SourceKey : IEquatable<SourceKey>
        {
            private readonly int transformId;
            private readonly int meshId;

            public SourceKey(Transform transform, Mesh mesh)
            {
                transformId = transform.GetInstanceID();
                meshId = mesh.GetInstanceID();
            }

            public bool Equals(SourceKey other) => transformId == other.transformId && meshId == other.meshId;
            public override bool Equals(object obj) => obj is SourceKey other && Equals(other);
            public override int GetHashCode() => (transformId * 397) ^ meshId;
        }

    }
}
