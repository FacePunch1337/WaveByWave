using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Unity.Collections;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using WaveByWave.Collision;

namespace WaveByWave.Editor
{
    // This is asset preparation, not a simulation or a test. It runs on import,
    // before entering play mode, and before building so unreadable FBX models work.
    [InitializeOnLoad]
    public sealed class ShipCollisionGeometryBaker : AssetPostprocessor, IPreprocessBuildWithReport
    {
        private const string LibraryPath = "Assets/_Project/Resources/ShipCollisionMeshLibrary.asset";
        private const string ShipPath = "Assets/_Project/Prefabs/Ship.prefab";
        private static bool _queued;
        private static bool _baking;
        public int callbackOrder => -100;

        static ShipCollisionGeometryBaker()
        {
            QueueBake();
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode)
                    Bake();
            };
        }

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted,
            string[] moved, string[] movedFrom)
        {
            if (_baking) return;
            if (imported.Concat(deleted).Concat(moved).Concat(movedFrom).Any(IsGeometrySource))
                QueueBake();
        }

        private static bool IsGeometrySource(string path)
        {
            if (path == LibraryPath) return false;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension is ".fbx" or ".obj" or ".blend" or ".dae" or ".prefab" or ".unity" ||
                   path.StartsWith("Assets/_Project/Generated/CollisionMeshes/", StringComparison.Ordinal);
        }

        private static void QueueBake()
        {
            if (_queued) return;
            _queued = true;
            EditorApplication.delayCall += BakeWhenIdle;
        }

        private static void BakeWhenIdle()
        {
            if (!_queued) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += BakeWhenIdle;
                return;
            }
            Bake();
        }

        public void OnPreprocessBuild(BuildReport report) => Bake();

        [MenuItem("Tools/Wave by Wave/Physics/Bake Ship Collision Geometry")]
        public static void Bake()
        {
            if (_baking) return;
            _baking = true;
            _queued = false;
            try
            {
                var meshes = new HashSet<Mesh>();
                var ship = AssetDatabase.LoadAssetAtPath<GameObject>(ShipPath);
                if (ship != null)
                {
                    foreach (var filter in ship.GetComponentsInChildren<MeshFilter>(true))
                        if (filter.sharedMesh != null && KinematicShipCollision.IsSolidModel(filter))
                            meshes.Add(filter.sharedMesh);
                }

                // Include collision meshes currently placed in loaded scenes and project
                // prefabs. Ignore generated box pieces: they are not the model geometry.
                foreach (var collider in UnityEngine.Object.FindObjectsByType<UnityEngine.MeshCollider>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (collider.sharedMesh != null) meshes.Add(collider.sharedMesh);
                foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_Project" }))
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                    if (prefab == null) continue;
                    foreach (var collider in prefab.GetComponentsInChildren<UnityEngine.MeshCollider>(true))
                        if (collider.sharedMesh != null) meshes.Add(collider.sharedMesh);
                }

                var scenePaths = new HashSet<string>(EditorBuildSettings.scenes
                    .Where(scene => scene.enabled).Select(scene => scene.path));
                foreach (var guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets/_Project" }))
                    scenePaths.Add(AssetDatabase.GUIDToAssetPath(guid));
                foreach (var path in scenePaths)
                    CollectSceneMeshes(path, meshes);

                var entries = meshes.OrderBy(mesh => AssetDatabase.GetAssetPath(mesh), StringComparer.Ordinal)
                    .ThenBy(mesh => mesh.name, StringComparer.Ordinal).Select(ReadMesh).ToArray();
                EnsureFolder("Assets/_Project/Resources");
                var library = AssetDatabase.LoadAssetAtPath<ShipCollisionMeshLibrary>(LibraryPath);
                if (library == null)
                {
                    library = ScriptableObject.CreateInstance<ShipCollisionMeshLibrary>();
                    AssetDatabase.CreateAsset(library, LibraryPath);
                }
                library.SetEntries(entries);
                EditorUtility.SetDirty(library);
                AssetDatabase.SaveAssetIfDirty(library);
            }
            finally
            {
                _baking = false;
            }
        }

        private static void CollectSceneMeshes(string scenePath, HashSet<Mesh> meshes)
        {
            if (!File.Exists(scenePath)) return;
            var contents = File.ReadAllText(scenePath);
            foreach (var section in Regex.Split(contents, @"(?m)^--- !u!"))
            {
                if (!section.StartsWith("64 &", StringComparison.Ordinal)) continue;
                var reference = Regex.Match(section,
                    @"m_Mesh: \{fileID: (-?\d+), guid: ([a-fA-F0-9]{32}), type: \d+\}");
                if (!reference.Success) continue;
                var path = AssetDatabase.GUIDToAssetPath(reference.Groups[2].Value);
                var requestedId = long.Parse(reference.Groups[1].Value);
                foreach (var mesh in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>())
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string _, out long localId) &&
                        localId == requestedId)
                        meshes.Add(mesh);
            }
        }

        private static ShipCollisionMeshLibrary.Entry ReadMesh(Mesh source)
        {
            using var dataArray = MeshUtility.AcquireReadOnlyMeshData(source);
            var data = dataArray[0];
            using var vertices = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp);
            data.GetVertices(vertices);
            var triangles = new List<int>();
            for (var subMesh = 0; subMesh < data.subMeshCount; subMesh++)
            {
                var descriptor = data.GetSubMesh(subMesh);
                if (descriptor.topology != MeshTopology.Triangles) continue;
                using var indices = new NativeArray<int>(descriptor.indexCount, Allocator.Temp);
                data.GetIndices(indices, subMesh, true);
                triangles.AddRange(indices.ToArray());
            }
            return new ShipCollisionMeshLibrary.Entry
                { source = source, vertices = vertices.ToArray(), triangles = triangles.ToArray() };
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = path.Substring(0, path.LastIndexOf('/'));
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
