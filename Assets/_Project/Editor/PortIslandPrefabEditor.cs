using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using WaveByWave.Generation;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    [InitializeOnLoad]
    public static class PortIslandPrefabBaker
    {
        public const string PrefabPath = "Assets/_Project/Prefabs/Generation/PortIsland.prefab";
        private const string MeshAssetPath = "Assets/_Project/Generated/PortIsland/PortIslandMeshes.asset";

        static PortIslandPrefabBaker() => EditorApplication.delayCall += EnsureDefaultPrefab;

        [MenuItem("Tools/Wave by Wave/Create or Rebuild Port Island Prefab")]
        public static void CreateOrRebuild()
        {
            EnsureDefaultPrefab();
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null) BakePrefab(PrefabPath);
        }

        private static void EnsureDefaultPrefab()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            { EditorApplication.delayCall += EnsureDefaultPrefab; return; }
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (existing != null)
            {
                var existingAuthoring = existing.GetComponent<SceneIsland>();
                if (existingAuthoring != null && !existingAuthoring.BakeMatchesSettings) BakePrefab(PrefabPath);
                return;
            }
            var settings = AssetDatabase.LoadAssetAtPath<OceanGenerationSettings>(
                "Assets/_Project/Resources/OceanGeneration.asset");
            if (settings == null) return;
            Folder(Path.GetDirectoryName(PrefabPath).Replace('\\', '/'));
            var root = new GameObject("Port Island");
            try
            {
                root.AddComponent<ProceduralIsland>();
                var authoring = root.AddComponent<SceneIsland>();
                authoring.Settings = settings; authoring.Size = IslandSize.Medium;
                authoring.Seed = 24681357; authoring.SpawnChests = false; authoring.NetworkIslandId = -10001;
                var geometry = new GameObject("Baked Geometry"); geometry.transform.SetParent(root.transform, false);
                var decorations = new GameObject("Decorations"); decorations.transform.SetParent(root.transform, false);
                authoring.ConfigureForBake(settings, geometry.transform, decorations.transform);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally { Object.DestroyImmediate(root); }
            BakePrefab(PrefabPath);
        }

        public static void BakePrefab(string path)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var authoring = root.GetComponent<SceneIsland>();
                if (authoring == null) throw new InvalidOperationException("Port Island prefab has no SceneIsland component.");
                Bake(authoring);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            AssetDatabase.SaveAssets();
            ValidatePrefab(path);
            Debug.Log($"[Ocean] Deterministic Port Island baked into {path}");
        }

        private static void Bake(SceneIsland authoring)
        {
            var settings = authoring.Settings != null ? authoring.Settings :
                AssetDatabase.LoadAssetAtPath<OceanGenerationSettings>("Assets/_Project/Resources/OceanGeneration.asset");
            if (settings == null || settings.GroundMaterial == null)
                throw new InvalidOperationException("OceanGeneration settings or Island Ground material is missing.");
            var geometry = Child(authoring.transform, "Baked Geometry");
            var decorations = Child(authoring.transform, "Decorations");
            authoring.ConfigureForBake(settings, geometry, decorations);
            ClearChildren(geometry); ClearChildren(decorations);

            var parameters = ProceduralIsland.CreateParameters(authoring.Size, settings);
            parameters.Seed = (uint)Mathf.Max(1, authoring.Seed);
            var sampleCount = parameters.Points.x * parameters.Points.y * parameters.Points.z;
            if (sampleCount > 8_000_000)
                throw new InvalidOperationException($"Island requests {sampleCount:N0} voxels. Increase Voxel Size or reduce its diameter.");

            Folder(Path.GetDirectoryName(MeshAssetPath).Replace('\\', '/'));
            var collection = AssetDatabase.LoadAssetAtPath<BakedIslandMeshCollection>(MeshAssetPath);
            if (collection == null)
            {
                collection = ScriptableObject.CreateInstance<BakedIslandMeshCollection>();
                AssetDatabase.CreateAsset(collection, MeshAssetPath);
            }
            foreach (var mesh in collection.Meshes)
                if (mesh != null) Object.DestroyImmediate(mesh, true);
            collection.Meshes.Clear();

            using var density = new NativeArray<float2>(sampleCount, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);
            new IslandInitializeJob { Parameters = parameters, Density = density }
                .Schedule(density.Length, 128).Complete();
            var cells = parameters.Points - 1;
            // A scene island is permanently present, so cap the hierarchy to roughly 8 chunks
            // per horizontal axis. Runtime ocean islands keep their smaller streaming chunks.
            var chunkSize = Mathf.Max(Mathf.Clamp(settings.ChunkCells, 4, 16),
                Mathf.CeilToInt(cells.x / 8f));
            for (var z = 0; z < cells.z; z += chunkSize)
            for (var y = 0; y < cells.y; y += chunkSize)
            for (var x = 0; x < cells.x; x += chunkSize)
            {
                var minimum = new int3(x, y, z);
                var maximum = math.min(minimum + chunkSize, parameters.Points - 1);
                var mesh = BuildMesh(parameters, density, minimum, maximum);
                if (mesh != null)
                {
                    mesh.name = $"Port Island {x},{y},{z}";
                    AssetDatabase.AddObjectToAsset(mesh, collection); collection.Meshes.Add(mesh);
                }
                var go = new GameObject($"Ground {x},{y},{z}", typeof(MeshFilter), typeof(MeshRenderer),
                    typeof(MeshCollider), typeof(BakedIslandChunk));
                go.transform.SetParent(geometry, false);
                var filter = go.GetComponent<MeshFilter>(); filter.sharedMesh = mesh;
                var renderer = go.GetComponent<MeshRenderer>(); renderer.sharedMaterial = settings.GroundMaterial;
                renderer.enabled = mesh != null; renderer.shadowCastingMode = ShadowCastingMode.On;
                go.GetComponent<MeshCollider>().sharedMesh = mesh;
                go.GetComponent<BakedIslandChunk>().Configure(
                    new Vector3Int(minimum.x, minimum.y, minimum.z),
                    new Vector3Int(maximum.x, maximum.y, maximum.z));
            }
            BuildDecorations(authoring, parameters, density, decorations);
            authoring.MarkBaked();
            EditorUtility.SetDirty(collection); EditorUtility.SetDirty(authoring);
        }

        private static void ValidatePrefab(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var authoring = prefab != null ? prefab.GetComponent<SceneIsland>() : null;
            if (authoring == null || !authoring.BakeMatchesSettings)
                throw new InvalidOperationException("Port Island bake metadata is incomplete.");
            var chunks = authoring.BakedGeometryRoot.GetComponentsInChildren<BakedIslandChunk>(true);
            if (chunks.Length == 0) throw new InvalidOperationException("Port Island contains no baked chunks.");
            var visible = 0;
            foreach (var chunk in chunks)
            {
                if (!chunk.TryGetComponents(out var filter, out var renderer, out var collider))
                    throw new InvalidOperationException($"Invalid baked chunk: {chunk.name}");
                if (filter.sharedMesh == null)
                {
                    if (renderer.enabled || collider.sharedMesh != null)
                        throw new InvalidOperationException($"Empty chunk is still active: {chunk.name}");
                    continue;
                }
                visible++;
                if (!renderer.enabled || collider.sharedMesh != filter.sharedMesh ||
                    renderer.sharedMaterial != authoring.Settings.GroundMaterial)
                    throw new InvalidOperationException($"Baked render/collision references differ: {chunk.name}");
            }
            if (visible == 0) throw new InvalidOperationException("Port Island has no visible mesh chunks.");
            Debug.Log($"[Ocean] Port Island validation PASS: {chunks.Length} chunks, {visible} visible, " +
                $"seed {authoring.Seed}, diameter {authoring.Settings.Diameter(authoring.Size):0.#} m, " +
                $"chests {(authoring.SpawnChests ? "enabled" : "disabled")}.", prefab);
        }

        private static Mesh BuildMesh(IslandFieldParameters parameters, NativeArray<float2> density,
            int3 minimum, int3 maximum)
        {
            var job = new IslandMeshJob
            {
                Parameters = parameters, Density = density, Minimum = minimum, Maximum = maximum,
                Vertices = new NativeList<float3>(1024, Allocator.TempJob),
                Normals = new NativeList<float3>(1024, Allocator.TempJob),
                Colors = new NativeList<Color32>(1024, Allocator.TempJob)
            };
            try
            {
                job.Schedule().Complete();
                if (job.Vertices.Length == 0) return null;
                var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(job.Vertices.AsArray()); mesh.SetNormals(job.Normals.AsArray());
                mesh.SetColors(job.Colors.AsArray());
                var indices = new int[job.Vertices.Length];
                for (var i = 0; i < indices.Length; i++) indices[i] = i;
                mesh.SetIndices(indices, MeshTopology.Triangles, 0); mesh.RecalculateBounds();
                return mesh;
            }
            finally
            { job.Vertices.Dispose(); job.Normals.Dispose(); job.Colors.Dispose(); }
        }

        private static void BuildDecorations(SceneIsland authoring, IslandFieldParameters parameters,
            NativeArray<float2> density, Transform root)
        {
            var random = new Unity.Mathematics.Random(((uint)Mathf.Max(1, authoring.Seed)) | 1u);
            var reserved = new List<Vector3>();
            foreach (var entry in authoring.Settings.Decorations)
            {
                if (entry?.Prefab == null) continue;
                var count = Mathf.RoundToInt(parameters.Diameter * parameters.Diameter *
                    Mathf.Max(0f, entry.InstancesPerSquareMetre));
                for (var n = 0; n < count; n++)
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    var p = random.NextFloat2(-parameters.Diameter * 0.45f, parameters.Diameter * 0.45f);
                    if (!TrySurface(p.x, p.y, parameters, density, out var point, out var normal) ||
                        point.y < entry.MinimumHeightAboveWater ||
                        Vector3.Angle(normal, Vector3.up) > entry.MaximumSlope) continue;
                    var crowded = false;
                    foreach (var existing in reserved)
                        if ((existing - point).sqrMagnitude < entry.Spacing * entry.Spacing)
                        { crowded = true; break; }
                    if (crowded) continue;
                    reserved.Add(point);
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(entry.Prefab, root);
                    instance.transform.localPosition = point;
                    instance.transform.localRotation = (entry.AlignToSurface
                        ? Quaternion.FromToRotation(Vector3.up, normal) : Quaternion.identity) *
                        Quaternion.Euler(0f, random.NextFloat(0f, 360f), 0f);
                    var minimumScale = Mathf.Max(0.01f, entry.ScaleRange.x);
                    var scale = random.NextFloat(minimumScale, Mathf.Max(minimumScale + 0.001f, entry.ScaleRange.y));
                    instance.transform.localScale *= scale;
                    break;
                }
            }
        }

        private static bool TrySurface(float x, float z, IslandFieldParameters parameters,
            NativeArray<float2> density, out Vector3 point, out Vector3 normal)
        {
            var p = new float3(x, parameters.SandHeight + 0.5f, z); var previous = p.y;
            for (; p.y > parameters.Origin.y; p.y -= parameters.CellSize * 0.5f)
            {
                if (math.cmax(Sample(p, parameters, density)) <= 0f) { previous = p.y; continue; }
                var lower = p.y; var upper = previous;
                for (var n = 0; n < 8; n++)
                {
                    p.y = (lower + upper) * 0.5f;
                    if (math.cmax(Sample(p, parameters, density)) > 0f) lower = p.y; else upper = p.y;
                }
                p.y = upper; point = p;
                var h = parameters.CellSize * 0.5f;
                normal = new Vector3(
                    math.cmax(Sample(p - new float3(h, 0, 0), parameters, density)) -
                    math.cmax(Sample(p + new float3(h, 0, 0), parameters, density)),
                    math.cmax(Sample(p - new float3(0, h, 0), parameters, density)) -
                    math.cmax(Sample(p + new float3(0, h, 0), parameters, density)),
                    math.cmax(Sample(p - new float3(0, 0, h), parameters, density)) -
                    math.cmax(Sample(p + new float3(0, 0, h), parameters, density))).normalized;
                return true;
            }
            point = default; normal = Vector3.up; return false;
        }

        private static float2 Sample(float3 position, IslandFieldParameters parameters, NativeArray<float2> density)
        {
            var grid = (position - parameters.Origin) / parameters.CellSize;
            var p = math.clamp((int3)math.floor(grid), int3.zero, parameters.Points - 2);
            var t = math.saturate(math.frac(grid));
            float2 Read(int3 q) => density[parameters.Index(q)];
            var a = math.lerp(Read(p), Read(p + new int3(1, 0, 0)), t.x);
            var b = math.lerp(Read(p + new int3(0, 1, 0)), Read(p + new int3(1, 1, 0)), t.x);
            var c = math.lerp(Read(p + new int3(0, 0, 1)), Read(p + new int3(1, 0, 1)), t.x);
            var d = math.lerp(Read(p + new int3(0, 1, 1)), Read(p + new int3(1, 1, 1)), t.x);
            return math.lerp(math.lerp(a, b, t.y), math.lerp(c, d, t.y), t.z);
        }

        private static Transform Child(Transform parent, string name)
        {
            var child = parent.Find(name);
            if (child != null) return child;
            var go = new GameObject(name); go.transform.SetParent(parent, false); return go.transform;
        }
        private static void ClearChildren(Transform root)
        { for (var i = root.childCount - 1; i >= 0; i--) Object.DestroyImmediate(root.GetChild(i).gameObject); }
        private static void Folder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/'); Folder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }

    [CustomEditor(typeof(SceneIsland))]
    public sealed class SceneIslandEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var island = (SceneIsland)target;
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(island.BakeMatchesSettings
                ? "Геометрия запечена и совпадает с текущими Size, Seed, Diameter и Voxel Size."
                : "Настройки изменились или геометрия ещё не запечена. Откройте prefab и нажмите кнопку генерации.",
                island.BakeMatchesSettings ? MessageType.Info : MessageType.Warning);
            var isAsset = PrefabUtility.IsPartOfPrefabAsset(island);
            var path = isAsset ? AssetDatabase.GetAssetPath(island.gameObject) : string.Empty;
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || !isAsset))
                if (GUILayout.Button("Сгенерировать и запечь остров")) PortIslandPrefabBaker.BakePrefab(path);
            if (!isAsset)
                EditorGUILayout.HelpBox("Это экземпляр на сцене. Размер и seed настраиваются и запекаются в самом PortIsland.prefab, после чего все его экземпляры обновятся.", MessageType.Info);
        }
    }
}
