using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using WaveByWave.Collision;

namespace WaveByWave.Editor
{
    public sealed class RigidbodyMeshCollisionGeneratorWindow : EditorWindow
    {
        private const string WindowTitle = "Mesh Collision";

        private enum GenerationMode
        {
            ExactDynamicSurface,
            CompoundConvex,
            ExactStatic
        }

        [SerializeField] private GameObject sourceRoot;
        [SerializeField] private Rigidbody rigidbodyOverride;
        [SerializeField] private GenerationMode mode = GenerationMode.ExactDynamicSurface;
        [SerializeField] private bool includeChildren;
        [SerializeField] private bool includeInactive;
        [SerializeField] private bool disableSourceMeshColliders = true;
        [SerializeField] private bool addRigidbodyIfMissing = true;
        [SerializeField] private bool configureRigidbody = true;
        [SerializeField] private int trianglesPerPiece = 24;
        [SerializeField] private int maximumPieces = 5000;
        [SerializeField] private float planarThickness = 0.01f;
        [SerializeField] private PhysicsMaterial physicsMaterial;

        private Vector2 scrollPosition;

        [MenuItem("Tools/Wave by Wave/Physics/Generate Mesh Collision")]
        private static void OpenFromMenu()
        {
            var window = GetWindow<RigidbodyMeshCollisionGeneratorWindow>(WindowTitle);
            window.minSize = new Vector2(430f, 520f);
            window.UseSelection();
            window.Show();
        }

        [MenuItem("CONTEXT/MeshFilter/Generate Rigidbody Collision...")]
        private static void OpenFromMeshFilter(MenuCommand command)
        {
            var window = GetWindow<RigidbodyMeshCollisionGeneratorWindow>(WindowTitle);
            window.minSize = new Vector2(430f, 520f);
            window.sourceRoot = ((MeshFilter)command.context).gameObject;
            window.includeChildren = false;
            window.rigidbodyOverride = FindRigidbody(window.sourceRoot);
            window.Show();
        }

        [MenuItem("Tools/Wave by Wave/Physics/Remove Generated Collision", true)]
        private static bool ValidateRemoveFromSelection()
        {
            return Selection.activeGameObject != null &&
                   RigidbodyMeshCollisionGenerator.HasGeneratedCollision(Selection.activeGameObject);
        }

        [MenuItem("Tools/Wave by Wave/Physics/Remove Generated Collision")]
        private static void RemoveFromSelection()
        {
            var removed = RigidbodyMeshCollisionGenerator.RemoveGeneratedCollision(Selection.activeGameObject, true);
            Debug.Log($"[Wave by Wave] Removed {removed} generated collision set(s).", Selection.activeGameObject);
        }

        private void OnEnable()
        {
            if (sourceRoot == null)
                UseSelection();
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Collision for a mesh Rigidbody", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "A moving non-kinematic Rigidbody cannot use a concave MeshCollider. " +
                "Exact Dynamic Surface reproduces it with convex triangle prisms; Compound Convex is the faster approximation.",
                MessageType.Info);

            EditorGUILayout.Space(4f);
            sourceRoot = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Source", "Select a GameObject containing a MeshFilter, or a hierarchy root."),
                sourceRoot,
                typeof(GameObject),
                true);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Use current selection", GUILayout.Width(160f)))
                    UseSelection();
            }

            includeChildren = EditorGUILayout.Toggle(
                new GUIContent("Include child meshes", "Generate collision from every MeshFilter below Source."),
                includeChildren);
            if (includeChildren)
            {
                includeInactive = EditorGUILayout.Toggle(
                    new GUIContent("Include inactive children"),
                    includeInactive);
            }

            EditorGUILayout.Space(8f);
            mode = (GenerationMode)EditorGUILayout.EnumPopup(
                new GUIContent("Mode"),
                mode);

            if (mode == GenerationMode.ExactDynamicSurface)
            {
                DrawExactDynamicSettings();
            }
            else if (mode == GenerationMode.CompoundConvex)
            {
                DrawCompoundSettings();
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Exact Static creates a triangle-accurate concave collider. It is valid only without a Rigidbody, " +
                    "or with a kinematic Rigidbody. It cannot be used for a physically simulated moving body.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(8f);
            rigidbodyOverride = (Rigidbody)EditorGUILayout.ObjectField(
                new GUIContent("Rigidbody", "Optional override. By default the nearest Rigidbody above Source is used."),
                rigidbodyOverride,
                typeof(Rigidbody),
                true);
            disableSourceMeshColliders = EditorGUILayout.Toggle(
                new GUIContent("Disable source MeshColliders", "Their original enabled states are restored when generated collision is removed."),
                disableSourceMeshColliders);
            physicsMaterial = (PhysicsMaterial)EditorGUILayout.ObjectField(
                new GUIContent("Physics Material"),
                physicsMaterial,
                typeof(PhysicsMaterial),
                false);

            EditorGUILayout.Space(8f);
            DrawSelectionSummary(out var canGenerate);

            EditorGUILayout.Space(12f);
            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                if (GUILayout.Button("Generate / Replace Collision", GUILayout.Height(34f)))
                    Generate();
            }

            using (new EditorGUI.DisabledScope(sourceRoot == null ||
                                                !RigidbodyMeshCollisionGenerator.HasGeneratedCollision(sourceRoot)))
            {
                if (GUILayout.Button("Remove generated collision"))
                    RigidbodyMeshCollisionGenerator.RemoveGeneratedCollision(sourceRoot, true);
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.EndScrollView();
        }

        private void DrawCompoundSettings()
        {
            trianglesPerPiece = EditorGUILayout.IntSlider(
                new GUIContent("Triangles per piece", "Lower values preserve concave detail but create more colliders."),
                trianglesPerPiece,
                4,
                40);
            maximumPieces = EditorGUILayout.IntField(
                new GUIContent("Maximum pieces", "Generation stops instead of silently creating unsafe oversized convex hulls."),
                maximumPieces);
            maximumPieces = Mathf.Clamp(maximumPieces, 1, 50000);
            planarThickness = EditorGUILayout.FloatField(
                new GUIContent("Minimum shell thickness", "Adds thickness to nearly planar surface pieces so PhysX can cook a valid solid convex collider."),
                planarThickness);
            planarThickness = Mathf.Max(0.0001f, planarThickness);

            addRigidbodyIfMissing = EditorGUILayout.Toggle(
                new GUIContent("Add Rigidbody if missing"),
                addRigidbodyIfMissing);
            configureRigidbody = EditorGUILayout.Toggle(
                new GUIContent("Configure robust collisions", "Uses Continuous Speculative detection and interpolation."),
                configureRigidbody);
        }

        private void DrawExactDynamicSettings()
        {
            EditorGUILayout.HelpBox(
                "Exact Dynamic Surface creates one thin convex triangular prism per source triangle. " +
                "The outside face stays exactly on the rendered mesh, including every concavity. " +
                "This is much more expensive than ordinary collision and requires consistent outward triangle winding.",
                MessageType.Warning);

            maximumPieces = EditorGUILayout.IntField(
                new GUIContent("Maximum triangle colliders", "Generation stops when the mesh is above this explicit performance limit."),
                maximumPieces);
            maximumPieces = Mathf.Clamp(maximumPieces, 1, 50000);
            planarThickness = EditorGUILayout.FloatField(
                new GUIContent("Inward shell thickness", "Thickness is extruded behind each source triangle; the outside contact face is not offset."),
                planarThickness);
            planarThickness = Mathf.Max(0.0001f, planarThickness);

            addRigidbodyIfMissing = EditorGUILayout.Toggle(
                new GUIContent("Add Rigidbody if missing"),
                addRigidbodyIfMissing);
            configureRigidbody = EditorGUILayout.Toggle(
                new GUIContent("Configure robust collisions", "Uses Continuous Speculative detection and interpolation."),
                configureRigidbody);
        }

        private void DrawSelectionSummary(out bool canGenerate)
        {
            canGenerate = sourceRoot != null && !EditorApplication.isPlayingOrWillChangePlaymode;
            if (sourceRoot == null)
            {
                EditorGUILayout.HelpBox("Select a GameObject with a MeshFilter.", MessageType.Warning);
                return;
            }

            if (EditorUtility.IsPersistent(sourceRoot))
            {
                EditorGUILayout.HelpBox("Open the prefab in Prefab Mode before generating collision.", MessageType.Error);
                canGenerate = false;
                return;
            }

            var filters = RigidbodyMeshCollisionGenerator.FindSources(sourceRoot, includeChildren, includeInactive);
            var triangleCount = RigidbodyMeshCollisionGenerator.CountTriangles(filters);
            var estimatedPieces = mode switch
            {
                GenerationMode.ExactDynamicSurface => triangleCount,
                GenerationMode.CompoundConvex => Mathf.CeilToInt(triangleCount / (float)Mathf.Max(1, trianglesPerPiece)),
                _ => triangleCount > 0 ? 1 : 0
            };

            EditorGUILayout.LabelField("Meshes", filters.Count.ToString());
            EditorGUILayout.LabelField("Triangles", triangleCount.ToString("N0"));
            EditorGUILayout.LabelField("Estimated collider pieces", estimatedPieces.ToString("N0"));

            if (filters.Count == 0 || triangleCount == 0)
            {
                EditorGUILayout.HelpBox("No triangle meshes were found in the selected source.", MessageType.Error);
                canGenerate = false;
            }

            var body = ResolveRigidbody();
            if (mode != GenerationMode.ExactStatic && body == null && !addRigidbodyIfMissing)
            {
                EditorGUILayout.HelpBox("No Rigidbody was found. Enable Add Rigidbody or assign one.", MessageType.Error);
                canGenerate = false;
            }

            if (mode == GenerationMode.ExactStatic && body != null && !body.isKinematic)
            {
                EditorGUILayout.HelpBox("Exact Static is invalid because the resolved Rigidbody is non-kinematic.", MessageType.Error);
                canGenerate = false;
            }

            if (mode != GenerationMode.ExactStatic && estimatedPieces > maximumPieces)
            {
                EditorGUILayout.HelpBox(
                    $"This quality needs about {estimatedPieces:N0} pieces, above the safety limit of {maximumPieces:N0}. " +
                    "Raise Maximum Pieces, select fewer meshes, or increase Triangles per piece.",
                    MessageType.Error);
                canGenerate = false;
            }

            if (mode == GenerationMode.ExactDynamicSurface && estimatedPieces > 1000 &&
                estimatedPieces <= maximumPieces)
            {
                EditorGUILayout.HelpBox(
                    $"Exact mode will create {estimatedPieces:N0} PhysX shapes. This can noticeably increase " +
                    "loading time and physics cost; test the target hardware with several objects present.",
                    MessageType.Warning);
            }

            var host = body != null ? body.transform : sourceRoot.transform;
            var scale = host.lossyScale;
            if (!ApproximatelyUniform(scale))
            {
                EditorGUILayout.HelpBox(
                    "The Rigidbody hierarchy has non-uniform scale. Collision is baked in the correct local shape, " +
                    "but a unit scale on the Rigidbody root is safer for PhysX.",
                    MessageType.Warning);
            }
        }

        private void Generate()
        {
            try
            {
                var settings = new RigidbodyMeshCollisionGenerator.Settings
                {
                    SourceRoot = sourceRoot,
                    Rigidbody = ResolveRigidbody(),
                    IncludeChildren = includeChildren,
                    IncludeInactive = includeInactive,
                    CompoundConvex = mode == GenerationMode.CompoundConvex,
                    ExactDynamicSurface = mode == GenerationMode.ExactDynamicSurface,
                    DisableSourceMeshColliders = disableSourceMeshColliders,
                    AddRigidbodyIfMissing = addRigidbodyIfMissing,
                    ConfigureRigidbody = configureRigidbody,
                    TrianglesPerPiece = trianglesPerPiece,
                    MaximumPieces = maximumPieces,
                    PlanarThickness = planarThickness,
                    Material = physicsMaterial
                };

                var result = RigidbodyMeshCollisionGenerator.Generate(settings);
                rigidbodyOverride = result.Rigidbody;
                Selection.activeGameObject = result.GeneratedRoot;
                EditorGUIUtility.PingObject(result.GeneratedRoot);
                Debug.Log(
                    $"[Wave by Wave] Generated {result.ColliderCount:N0} collider piece(s) " +
                    $"from {result.TriangleCount:N0} triangles for '{sourceRoot.name}'.",
                    result.GeneratedRoot);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, sourceRoot);
                EditorUtility.DisplayDialog("Collision generation failed", exception.Message, "OK");
            }
        }

        private void UseSelection()
        {
            if (Selection.activeGameObject == null)
                return;

            sourceRoot = Selection.activeGameObject;
            rigidbodyOverride = FindRigidbody(sourceRoot);
            Repaint();
        }

        private Rigidbody ResolveRigidbody()
        {
            if (rigidbodyOverride != null)
                return rigidbodyOverride;

            return FindRigidbody(sourceRoot);
        }

        private static Rigidbody FindRigidbody(GameObject source)
        {
            return source != null ? source.GetComponentInParent<Rigidbody>() : null;
        }

        private static bool ApproximatelyUniform(Vector3 scale)
        {
            var x = Mathf.Abs(scale.x);
            var y = Mathf.Abs(scale.y);
            var z = Mathf.Abs(scale.z);
            var largest = Mathf.Max(x, Mathf.Max(y, z));
            if (largest < 0.000001f)
                return false;

            return Mathf.Abs(x - y) / largest < 0.001f && Mathf.Abs(x - z) / largest < 0.001f;
        }
    }

    internal static class RigidbodyMeshCollisionGenerator
    {
        private const string GeneratedRootName = "__GeneratedCollision";
        private const string MeshAssetRoot = "Assets/_Project/Generated/CollisionMeshes";
        private const MeshColliderCookingOptions CookingOptions =
            MeshColliderCookingOptions.CookForFasterSimulation |
            MeshColliderCookingOptions.EnableMeshCleaning |
            MeshColliderCookingOptions.WeldColocatedVertices |
            MeshColliderCookingOptions.UseFastMidphase;

        internal sealed class Settings
        {
            public GameObject SourceRoot;
            public Rigidbody Rigidbody;
            public bool IncludeChildren;
            public bool IncludeInactive;
            public bool CompoundConvex;
            public bool ExactDynamicSurface;
            public bool DisableSourceMeshColliders;
            public bool AddRigidbodyIfMissing;
            public bool ConfigureRigidbody;
            public int TrianglesPerPiece;
            public int MaximumPieces;
            public float PlanarThickness;
            public PhysicsMaterial Material;

            public bool UsesConvexColliders => CompoundConvex || ExactDynamicSurface;
        }

        internal readonly struct Result
        {
            public Result(GameObject generatedRoot, Rigidbody rigidbody, int colliderCount, int triangleCount)
            {
                GeneratedRoot = generatedRoot;
                Rigidbody = rigidbody;
                ColliderCount = colliderCount;
                TriangleCount = triangleCount;
            }

            public GameObject GeneratedRoot { get; }
            public Rigidbody Rigidbody { get; }
            public int ColliderCount { get; }
            public int TriangleCount { get; }
        }

        private readonly struct Triangle
        {
            public Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 surfaceNormal)
            {
                A = a;
                B = b;
                C = c;
                Center = (a + b + c) / 3f;
                SurfaceNormal = surfaceNormal;
            }

            public Vector3 A { get; }
            public Vector3 B { get; }
            public Vector3 C { get; }
            public Vector3 Center { get; }
            public Vector3 SurfaceNormal { get; }
        }

        private readonly struct Edge : IEquatable<Edge>
        {
            public Edge(int a, int b)
            {
                if (a < b)
                {
                    A = a;
                    B = b;
                }
                else
                {
                    A = b;
                    B = a;
                }
            }

            public int A { get; }
            public int B { get; }

            public bool Equals(Edge other) => A == other.A && B == other.B;
            public override bool Equals(object obj) => obj is Edge other && Equals(other);
            public override int GetHashCode() => unchecked((A * 397) ^ B);
        }

        internal static Result Generate(Settings settings)
        {
            ValidateSettings(settings);

            var sources = FindSources(settings.SourceRoot, settings.IncludeChildren, settings.IncludeInactive);
            if (sources.Count == 0)
                throw new InvalidOperationException("No MeshFilter with a triangle mesh was found.");

            var body = settings.Rigidbody;
            if (body == null && settings.UsesConvexColliders && settings.AddRigidbodyIfMissing)
                body = Undo.AddComponent<Rigidbody>(settings.SourceRoot);

            if (settings.UsesConvexColliders && body == null)
                throw new InvalidOperationException("Dynamic collision generation requires a Rigidbody host.");
            if (!settings.UsesConvexColliders && body != null && !body.isKinematic)
                throw new InvalidOperationException("A concave MeshCollider cannot be used by a non-kinematic Rigidbody.");

            var host = body != null ? body.transform : settings.SourceRoot.transform;
            if (!settings.SourceRoot.transform.IsChildOf(host) && settings.SourceRoot.transform != host)
                throw new InvalidOperationException("The assigned Rigidbody must be on Source or one of its parents.");

            var sourceTriangles = ReadTriangles(sources, host);
            var triangleCount = sourceTriangles.Sum(item => item.Value.Count);
            if (triangleCount == 0)
                throw new InvalidOperationException("The selected meshes contain no usable triangles.");

            List<List<Triangle>> pieces;
            if (settings.ExactDynamicSurface)
            {
                if (triangleCount > settings.MaximumPieces)
                {
                    throw new InvalidOperationException(
                        $"Exact Dynamic Surface needs {triangleCount:N0} triangle colliders, above the explicit limit " +
                        $"of {settings.MaximumPieces:N0}.");
                }

                pieces = sourceTriangles
                    .SelectMany(item => item.Value)
                    .Select(triangle => new List<Triangle> { triangle })
                    .ToList();
            }
            else if (settings.CompoundConvex)
            {
                pieces = Decompose(sourceTriangles, settings.TrianglesPerPiece, settings.MaximumPieces);
            }
            else
            {
                pieces = new List<List<Triangle>>
                {
                    sourceTriangles.SelectMany(item => item.Value).ToList()
                };
            }

            var oldSets = FindGeneratedSets(settings.SourceRoot).ToArray();
            var disabledColliders = new List<MeshCollider>();
            var previousStates = new List<bool>();
            var createdAssetPaths = new List<string>();
            GameObject generatedRoot = null;

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Generate mesh collision");

            try
            {
                generatedRoot = new GameObject(GeneratedRootName);
                Undo.RegisterCreatedObjectUndo(generatedRoot, "Create generated collision");
                generatedRoot.transform.SetParent(host, false);
                generatedRoot.layer = settings.SourceRoot.layer;
                generatedRoot.isStatic = false;

                var marker = generatedRoot.AddComponent<GeneratedCollisionSet>();

                var assetFolder = CreateRunAssetFolder(settings.SourceRoot.name);
                var colliderCount = CreateColliders(
                    generatedRoot.transform,
                    pieces,
                    settings,
                    assetFolder,
                    createdAssetPaths);

                if (colliderCount == 0)
                    throw new InvalidOperationException("PhysX-compatible collision pieces could not be created from the selected mesh.");

                // Replace the previous set only after the new meshes were created successfully.
                // This keeps regeneration recoverable if mesh reading or asset creation fails.
                foreach (var oldSet in oldSets)
                    RemoveSet(oldSet, true);

                if (settings.DisableSourceMeshColliders)
                    DisableSourceColliders(sources, disabledColliders, previousStates);

                marker.Configure(settings.SourceRoot, settings.UsesConvexColliders, disabledColliders, previousStates);
                EditorUtility.SetDirty(marker);

                if (body != null && settings.UsesConvexColliders && settings.ConfigureRigidbody)
                {
                    Undo.RecordObject(body, "Configure Rigidbody collision");
                    body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                    if (!body.isKinematic && body.interpolation == RigidbodyInterpolation.None)
                        body.interpolation = RigidbodyInterpolation.Interpolate;
                    EditorUtility.SetDirty(body);
                }

                MarkDirty(host.gameObject);
                AssetDatabase.SaveAssets();
                Undo.CollapseUndoOperations(undoGroup);
                return new Result(generatedRoot, body, colliderCount, triangleCount);
            }
            catch
            {
                if (generatedRoot != null)
                    Undo.DestroyObjectImmediate(generatedRoot);

                foreach (var assetPath in createdAssetPaths)
                    AssetDatabase.DeleteAsset(assetPath);

                for (var i = 0; i < disabledColliders.Count; i++)
                {
                    if (disabledColliders[i] != null)
                        disabledColliders[i].enabled = previousStates[i];
                }

                Undo.RevertAllDownToGroup(undoGroup);
                throw;
            }
        }

        internal static List<MeshFilter> FindSources(GameObject sourceRoot, bool includeChildren, bool includeInactive)
        {
            if (sourceRoot == null)
                return new List<MeshFilter>();

            var filters = includeChildren
                ? sourceRoot.GetComponentsInChildren<MeshFilter>(includeInactive)
                : sourceRoot.GetComponents<MeshFilter>();

            return filters
                .Where(filter => filter != null &&
                                 filter.sharedMesh != null &&
                                 !IsBelowGeneratedRoot(filter.transform) &&
                                 HasTriangles(filter.sharedMesh))
                .ToList();
        }

        internal static int CountTriangles(IReadOnlyList<MeshFilter> filters)
        {
            long count = 0;
            foreach (var filter in filters)
            {
                var mesh = filter.sharedMesh;
                for (var subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    if (mesh.GetTopology(subMesh) == MeshTopology.Triangles)
                        count += (long)mesh.GetIndexCount(subMesh) / 3L;
                }
            }

            return count > int.MaxValue ? int.MaxValue : (int)count;
        }

        internal static bool HasGeneratedCollision(GameObject source)
        {
            return source != null && FindGeneratedSets(source).Any();
        }

        internal static int RemoveGeneratedCollision(GameObject source, bool restoreSourceColliders)
        {
            if (source == null)
                return 0;

            var sets = FindGeneratedSets(source).ToArray();
            var dirtyObjects = sets
                .Select(set => set.SourceRoot)
                .Where(item => item != null)
                .Distinct()
                .ToArray();
            foreach (var set in sets)
                RemoveSet(set, restoreSourceColliders);

            if (sets.Length > 0)
            {
                foreach (var dirtyObject in dirtyObjects)
                    MarkDirty(dirtyObject);
                AssetDatabase.SaveAssets();
            }

            return sets.Length;
        }

        private static void ValidateSettings(Settings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (settings.SourceRoot == null)
                throw new InvalidOperationException("Source is not assigned.");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Collision generation is available only in Edit Mode.");
            if (EditorUtility.IsPersistent(settings.SourceRoot))
                throw new InvalidOperationException("Open the prefab in Prefab Mode before generating collision.");
            if (settings.CompoundConvex && (settings.TrianglesPerPiece < 4 || settings.TrianglesPerPiece > 40))
                throw new ArgumentOutOfRangeException(nameof(settings.TrianglesPerPiece));
            if (settings.MaximumPieces < 1)
                throw new ArgumentOutOfRangeException(nameof(settings.MaximumPieces));
            if (settings.UsesConvexColliders && settings.PlanarThickness <= 0f)
                throw new ArgumentOutOfRangeException(nameof(settings.PlanarThickness));
        }

        private static Dictionary<MeshFilter, List<Triangle>> ReadTriangles(
            IReadOnlyList<MeshFilter> sources,
            Transform host)
        {
            var result = new Dictionary<MeshFilter, List<Triangle>>();
            var toHost = host.worldToLocalMatrix;

            foreach (var source in sources)
            {
                var mesh = source.sharedMesh;
                var sourceToHost = toHost * source.transform.localToWorldMatrix;
                var reversesWinding = sourceToHost.determinant < 0f;
                var triangles = new List<Triangle>();

                using (var dataArray = MeshUtility.AcquireReadOnlyMeshData(mesh))
                {
                    var meshData = dataArray[0];
                    using (var nativeVertices = new NativeArray<Vector3>(meshData.vertexCount, Allocator.Temp))
                    {
                        meshData.GetVertices(nativeVertices);
                        var vertices = new Vector3[nativeVertices.Length];
                        for (var i = 0; i < nativeVertices.Length; i++)
                            vertices[i] = sourceToHost.MultiplyPoint3x4(nativeVertices[i]);

                        for (var subMesh = 0; subMesh < meshData.subMeshCount; subMesh++)
                        {
                            var descriptor = meshData.GetSubMesh(subMesh);
                            if (descriptor.topology != MeshTopology.Triangles || descriptor.indexCount < 3)
                                continue;

                            using (var indices = new NativeArray<int>(descriptor.indexCount, Allocator.Temp))
                            {
                                meshData.GetIndices(indices, subMesh, true);
                                for (var index = 0; index + 2 < indices.Length; index += 3)
                                {
                                    var a = vertices[indices[index]];
                                    var b = vertices[indices[index + 1]];
                                    var c = vertices[indices[index + 2]];
                                    var normal = Vector3.Cross(b - a, c - a);
                                    if (normal.sqrMagnitude < 0.000000000001f)
                                        continue;

                                    normal.Normalize();
                                    if (reversesWinding)
                                        normal = -normal;
                                    triangles.Add(new Triangle(a, b, c, normal));
                                }
                            }
                        }
                    }
                }

                if (triangles.Count > 0)
                    result.Add(source, triangles);
            }

            return result;
        }

        private static List<List<Triangle>> Decompose(
            IReadOnlyDictionary<MeshFilter, List<Triangle>> sourceTriangles,
            int targetTriangleCount,
            int maximumPieces)
        {
            var pending = new Stack<List<Triangle>>();
            foreach (var source in sourceTriangles.Reverse())
            {
                var components = SplitConnectedComponents(source.Value);
                for (var i = components.Count - 1; i >= 0; i--)
                    pending.Push(components[i]);
            }

            if (pending.Count > maximumPieces)
            {
                throw new InvalidOperationException(
                    $"The mesh contains {pending.Count:N0} disconnected surfaces, above the limit of {maximumPieces:N0} pieces. " +
                    "Increase Maximum Pieces or select a smaller source.");
            }

            var result = new List<List<Triangle>>();
            while (pending.Count > 0)
            {
                var piece = pending.Pop();
                if (piece.Count <= targetTriangleCount)
                {
                    result.Add(piece);
                    continue;
                }

                if (result.Count + pending.Count + 2 > maximumPieces)
                {
                    throw new InvalidOperationException(
                        $"The requested detail needs more than {maximumPieces:N0} convex pieces. " +
                        "Increase Maximum Pieces, select fewer meshes, or increase Triangles per piece.");
                }

                SplitSpatially(piece, out var left, out var right);
                pending.Push(right);
                pending.Push(left);
            }

            return result;
        }

        private static List<List<Triangle>> SplitConnectedComponents(IReadOnlyList<Triangle> triangles)
        {
            var trianglesByVertex = new Dictionary<Vector3, List<int>>();
            for (var i = 0; i < triangles.Count; i++)
            {
                AddTriangleAtVertex(trianglesByVertex, triangles[i].A, i);
                AddTriangleAtVertex(trianglesByVertex, triangles[i].B, i);
                AddTriangleAtVertex(trianglesByVertex, triangles[i].C, i);
            }

            var components = new List<List<Triangle>>();
            var visited = new bool[triangles.Count];
            var queue = new Queue<int>();
            for (var start = 0; start < triangles.Count; start++)
            {
                if (visited[start])
                    continue;

                var component = new List<Triangle>();
                visited[start] = true;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var index = queue.Dequeue();
                    var triangle = triangles[index];
                    component.Add(triangle);
                    EnqueueNeighbours(trianglesByVertex[triangle.A], visited, queue);
                    EnqueueNeighbours(trianglesByVertex[triangle.B], visited, queue);
                    EnqueueNeighbours(trianglesByVertex[triangle.C], visited, queue);
                }

                components.Add(component);
            }

            return components;
        }

        private static void AddTriangleAtVertex(
            IDictionary<Vector3, List<int>> trianglesByVertex,
            Vector3 vertex,
            int triangleIndex)
        {
            if (!trianglesByVertex.TryGetValue(vertex, out var indices))
            {
                indices = new List<int>();
                trianglesByVertex.Add(vertex, indices);
            }

            indices.Add(triangleIndex);
        }

        private static void EnqueueNeighbours(IReadOnlyList<int> neighbours, bool[] visited, Queue<int> queue)
        {
            for (var i = 0; i < neighbours.Count; i++)
            {
                var neighbour = neighbours[i];
                if (visited[neighbour])
                    continue;

                visited[neighbour] = true;
                queue.Enqueue(neighbour);
            }
        }

        private static void SplitSpatially(List<Triangle> source, out List<Triangle> left, out List<Triangle> right)
        {
            var bounds = new Bounds(source[0].Center, Vector3.zero);
            for (var i = 1; i < source.Count; i++)
                bounds.Encapsulate(source[i].Center);

            var size = bounds.size;
            var axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            source.Sort((a, b) => Axis(a.Center, axis).CompareTo(Axis(b.Center, axis)));

            var midpoint = source.Count / 2;
            left = source.GetRange(0, midpoint);
            right = source.GetRange(midpoint, source.Count - midpoint);
        }

        private static float Axis(Vector3 value, int axis)
        {
            return axis == 0 ? value.x : axis == 1 ? value.y : value.z;
        }

        private static int CreateColliders(
            Transform generatedRoot,
            IReadOnlyList<List<Triangle>> pieces,
            Settings settings,
            string assetFolder,
            ICollection<string> createdAssetPaths)
        {
            var colliderCount = 0;
            var assetPath = AssetDatabase.GenerateUniqueAssetPath($"{assetFolder}/CollisionMeshes.asset");
            GameObject colliderObject = null;
            AssetDatabase.StartAssetEditing();
            try
            {
                for (var i = 0; i < pieces.Count; i++)
                {
                    var mesh = settings.ExactDynamicSurface
                        ? BuildTrianglePrism(pieces[i][0], settings.PlanarThickness, $"Collision_{i + 1:00000}")
                        : BuildMesh(
                            pieces[i],
                            settings.CompoundConvex ? settings.PlanarThickness : 0f,
                            $"Collision_{i + 1:000}");
                    if (mesh == null)
                        continue;

                    if (colliderCount == 0)
                    {
                        AssetDatabase.CreateAsset(mesh, assetPath);
                        createdAssetPaths.Add(assetPath);
                    }
                    else
                    {
                        AssetDatabase.AddObjectToAsset(mesh, assetPath);
                    }

                    if (colliderObject == null || !settings.ExactDynamicSurface || colliderCount % 64 == 0)
                    {
                        var objectName = settings.ExactDynamicSurface
                            ? $"ColliderBatch_{colliderCount / 64 + 1:000}"
                            : $"Collider_{i + 1:000}";
                        colliderObject = new GameObject(objectName);
                        colliderObject.transform.SetParent(generatedRoot, false);
                        colliderObject.layer = generatedRoot.gameObject.layer;
                    }

                    var collider = colliderObject.AddComponent<MeshCollider>();
                    collider.cookingOptions = CookingOptions;
                    collider.convex = settings.UsesConvexColliders;
                    collider.isTrigger = false;
                    collider.material = settings.Material;
                    collider.sharedMesh = mesh;
                    EditorUtility.SetDirty(collider);
                    colliderCount++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            if (colliderCount > 0)
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            return colliderCount;
        }

        private static Mesh BuildTrianglePrism(Triangle triangle, float thickness, string name)
        {
            var normal = triangle.SurfaceNormal;
            if (normal.sqrMagnitude < 0.5f)
                normal = Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A).normalized;

            var inwardOffset = normal * thickness;
            var vertices = new[]
            {
                triangle.A,
                triangle.B,
                triangle.C,
                triangle.A - inwardOffset,
                triangle.B - inwardOffset,
                triangle.C - inwardOffset
            };
            var indices = new[]
            {
                0, 1, 2,
                3, 5, 4,
                0, 3, 4, 0, 4, 1,
                1, 4, 5, 1, 5, 2,
                2, 5, 3, 2, 3, 0
            };

            var mesh = new Mesh { name = name };
            mesh.vertices = vertices;
            mesh.triangles = indices;
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildMesh(List<Triangle> triangles, float planarThickness, string name)
        {
            var vertices = new List<Vector3>();
            var indices = new List<int>(triangles.Count * 3);
            var vertexLookup = new Dictionary<Vector3, int>();

            foreach (var triangle in triangles)
            {
                indices.Add(GetVertexIndex(triangle.A, vertices, vertexLookup));
                indices.Add(GetVertexIndex(triangle.B, vertices, vertexLookup));
                indices.Add(GetVertexIndex(triangle.C, vertices, vertexLookup));
            }

            if (vertices.Count < 3 || indices.Count < 3)
                return null;

            if (planarThickness > 0f && IsNearlyPlanar(vertices, planarThickness, out var normal))
                Extrude(vertices, indices, normal, planarThickness);

            var mesh = new Mesh
            {
                name = name,
                indexFormat = vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(indices, 0, false);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static int GetVertexIndex(
            Vector3 vertex,
            ICollection<Vector3> vertices,
            IDictionary<Vector3, int> lookup)
        {
            if (lookup.TryGetValue(vertex, out var index))
                return index;

            index = vertices.Count;
            vertices.Add(vertex);
            lookup.Add(vertex, index);
            return index;
        }

        private static bool IsNearlyPlanar(IReadOnlyList<Vector3> vertices, float thickness, out Vector3 normal)
        {
            var first = vertices[0];
            var farthest = 1;
            var farthestDistance = 0f;
            for (var i = 1; i < vertices.Count; i++)
            {
                var distance = (vertices[i] - first).sqrMagnitude;
                if (distance > farthestDistance)
                {
                    farthest = i;
                    farthestDistance = distance;
                }
            }

            var line = vertices[farthest] - first;
            var third = -1;
            var farthestFromLine = 0f;
            for (var i = 1; i < vertices.Count; i++)
            {
                var crossMagnitude = Vector3.Cross(line, vertices[i] - first).sqrMagnitude;
                if (crossMagnitude > farthestFromLine)
                {
                    third = i;
                    farthestFromLine = crossMagnitude;
                }
            }

            if (third < 0 || farthestFromLine < 0.000000000001f)
            {
                normal = Vector3.Cross(line.normalized, Vector3.up);
                if (normal.sqrMagnitude < 0.0001f)
                    normal = Vector3.Cross(line.normalized, Vector3.right);
                normal.Normalize();
                return true;
            }

            normal = Vector3.Cross(line, vertices[third] - first).normalized;
            var minimum = 0f;
            var maximum = 0f;
            for (var i = 1; i < vertices.Count; i++)
            {
                var distance = Vector3.Dot(vertices[i] - first, normal);
                minimum = Mathf.Min(minimum, distance);
                maximum = Mathf.Max(maximum, distance);
            }

            return maximum - minimum < thickness;
        }

        private static void Extrude(List<Vector3> vertices, List<int> indices, Vector3 normal, float thickness)
        {
            var originalVertexCount = vertices.Count;
            var originalIndexCount = indices.Count;
            var offset = normal * (thickness * 0.5f);
            var originalVertices = vertices.ToArray();

            for (var i = 0; i < originalVertexCount; i++)
                vertices[i] = originalVertices[i] - offset;
            for (var i = 0; i < originalVertexCount; i++)
                vertices.Add(originalVertices[i] + offset);

            var edgeCounts = new Dictionary<Edge, int>();
            for (var i = 0; i < originalIndexCount; i += 3)
            {
                var a = indices[i];
                var b = indices[i + 1];
                var c = indices[i + 2];
                CountEdge(edgeCounts, new Edge(a, b));
                CountEdge(edgeCounts, new Edge(b, c));
                CountEdge(edgeCounts, new Edge(c, a));

                indices.Add(c + originalVertexCount);
                indices.Add(b + originalVertexCount);
                indices.Add(a + originalVertexCount);
            }

            foreach (var item in edgeCounts)
            {
                if (item.Value != 1)
                    continue;

                var a = item.Key.A;
                var b = item.Key.B;
                indices.Add(a);
                indices.Add(b);
                indices.Add(b + originalVertexCount);
                indices.Add(a);
                indices.Add(b + originalVertexCount);
                indices.Add(a + originalVertexCount);
            }
        }

        private static void CountEdge(IDictionary<Edge, int> counts, Edge edge)
        {
            counts.TryGetValue(edge, out var count);
            counts[edge] = count + 1;
        }

        private static void DisableSourceColliders(
            IReadOnlyList<MeshFilter> sources,
            ICollection<MeshCollider> disabledColliders,
            ICollection<bool> previousStates)
        {
            var colliders = sources
                .SelectMany(source => source.GetComponents<MeshCollider>())
                .Where(collider => collider != null && !IsBelowGeneratedRoot(collider.transform))
                .Distinct()
                .ToArray();

            foreach (var collider in colliders)
            {
                disabledColliders.Add(collider);
                previousStates.Add(collider.enabled);
                if (!collider.enabled)
                    continue;

                Undo.RecordObject(collider, "Disable source MeshCollider");
                collider.enabled = false;
                EditorUtility.SetDirty(collider);
            }
        }

        private static IEnumerable<GeneratedCollisionSet> FindGeneratedSets(GameObject source)
        {
            var containingSet = source.GetComponentInParent<GeneratedCollisionSet>();
            if (containingSet != null)
                return new[] { containingSet };

            var hierarchyRoot = source.transform.root;
            return hierarchyRoot
                .GetComponentsInChildren<GeneratedCollisionSet>(true)
                .Where(set => set != null && set.SourceRoot == source);
        }

        private static void RemoveSet(GeneratedCollisionSet set, bool restoreSourceColliders)
        {
            if (set == null)
                return;

            var assetPaths = set.GetComponentsInChildren<MeshCollider>(true)
                .Select(collider => collider.sharedMesh)
                .Where(mesh => mesh != null)
                .Select(AssetDatabase.GetAssetPath)
                .Where(path => path.StartsWith(MeshAssetRoot + "/", StringComparison.Ordinal))
                .Distinct()
                .ToArray();

            if (restoreSourceColliders)
            {
                var colliders = set.SourceColliders;
                var states = set.PreviousEnabledStates;
                var count = Mathf.Min(colliders.Count, states.Count);
                for (var i = 0; i < count; i++)
                {
                    var collider = colliders[i];
                    if (collider == null)
                        continue;

                    Undo.RecordObject(collider, "Restore source MeshCollider");
                    collider.enabled = states[i];
                    EditorUtility.SetDirty(collider);
                }
            }

            UnityEngine.Object.DestroyImmediate(set.gameObject);
            foreach (var path in assetPaths)
                AssetDatabase.DeleteAsset(path);

            foreach (var folder in assetPaths
                         .Select(path => Path.GetDirectoryName(path)?.Replace('\\', '/'))
                         .Where(path => !string.IsNullOrEmpty(path))
                         .Distinct())
            {
                var remainingAssets = AssetDatabase.FindAssets(string.Empty, new[] { folder })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Any(path => !string.Equals(path, folder, StringComparison.Ordinal));
                if (!remainingAssets)
                    AssetDatabase.DeleteAsset(folder);
            }
        }

        private static string CreateRunAssetFolder(string sourceName)
        {
            EnsureFolder(MeshAssetRoot);
            var safeName = string.Concat(sourceName.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "Mesh";

            var folderPath = AssetDatabase.GenerateUniqueAssetPath($"{MeshAssetRoot}/{safeName}_Collision");
            var parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            var folderName = Path.GetFileName(folderPath);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(folderName) ||
                string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, folderName)))
            {
                throw new IOException($"Could not create collision asset folder '{folderPath}'.");
            }

            return folderPath;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            var folderName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(folderName))
                throw new IOException($"Invalid asset folder '{path}'.");

            EnsureFolder(parent);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, folderName)))
                throw new IOException($"Could not create asset folder '{path}'.");
        }

        private static bool HasTriangles(Mesh mesh)
        {
            for (var subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) == MeshTopology.Triangles && mesh.GetIndexCount(subMesh) >= 3)
                    return true;
            }

            return false;
        }

        private static bool IsBelowGeneratedRoot(Transform transform)
        {
            while (transform != null)
            {
                if (transform.GetComponent<GeneratedCollisionSet>() != null)
                    return true;
                transform = transform.parent;
            }

            return false;
        }

        private static void MarkDirty(GameObject gameObject)
        {
            EditorUtility.SetDirty(gameObject);
            if (gameObject.scene.IsValid() && gameObject.scene.isLoaded)
                EditorSceneManager.MarkSceneDirty(gameObject.scene);

            PrefabUtility.RecordPrefabInstancePropertyModifications(gameObject.transform);
        }
    }
}
