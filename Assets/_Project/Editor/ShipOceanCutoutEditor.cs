using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WaveByWave.Ships;

namespace WaveByWave.Editor
{
    [InitializeOnLoad]
    public static class ShipOceanCutoutBaker
    {
        private const string Folder = "Assets/_Project/Generated/ShipDamage";
        private static readonly HashSet<ShipOceanCutout> Pending = new();
        static ShipOceanCutoutBaker()
        {
            ShipOceanCutout.BakeRequested += Queue;
            EditorApplication.update += Update;
            EditorApplication.projectChanged += QueueLoaded;
            EditorApplication.delayCall += QueueLoaded;
            Undo.postprocessModifications += modifications =>
            {
                foreach (var modification in modifications)
                    if (modification.currentValue.target is MeshFilter filter && filter.TryGetComponent<ShipOceanCutout>(out var cut)) Queue(cut);
                return modifications;
            };
        }
        public static void Queue(ShipOceanCutout cut) { if (cut != null) Pending.Add(cut); }
        private static void QueueLoaded()
        {
            foreach (var cut in UnityEngine.Object.FindObjectsByType<ShipOceanCutout>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Queue(cut);
        }
        private static void Update()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (File.Exists("Temp/ShipOceanCutout.request"))
            {
                // A previous assembly can observe a request while the editor is still importing code.
                var editorTime = File.GetLastWriteTimeUtc(typeof(ShipOceanCutoutBaker).Assembly.Location);
                var runtimeTime = File.GetLastWriteTimeUtc(typeof(ShipOceanCutout).Assembly.Location);
                if (File.GetLastWriteTimeUtc("Assets/_Project/Editor/ShipOceanCutoutEditor.cs") > editorTime ||
                    File.GetLastWriteTimeUtc("Assets/_Project/Editor/ShipOceanCutoutChecks.cs") > editorTime ||
                    File.GetLastWriteTimeUtc("Assets/_Project/Runtime/Ships/ShipOceanCutout.cs") > runtimeTime)
                { AssetDatabase.Refresh(); return; }
                File.Delete("Temp/ShipOceanCutout.request");
                try
                {
                    ConfigurePlayer(); ShipOceanCutoutChecks.Run();
                    File.WriteAllText("Temp/ShipOceanCutout.result", "PASS: sealed hull windows and actual SW3 rendering through a window; open deck, outside ocean, moved ship and interior water.");
                }
                catch (Exception e) { File.WriteAllText("Temp/ShipOceanCutout.result", e.ToString()); Debug.LogException(e); }
            }
            if (Pending.Count == 0) return;
            var work = new List<ShipOceanCutout>(Pending); Pending.Clear();
            foreach (var cut in work) if (cut != null && cut.gameObject.scene.IsValid())
            {
                try { Bake(cut); }
                catch (Exception e) { Debug.LogException(e, cut); }
            }
        }

        [MenuItem("Tools/Wave by Wave/Ships/Configure ocean volume cutout")]
        public static void ConfigurePlayer()
        {
            const string path = "Assets/_Project/Prefabs/Ship.prefab";
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            var inStage = stage != null && stage.assetPath == path;
            var root = inStage ? stage.prefabContentsRoot : PrefabUtility.LoadPrefabContents(path);
            try
            {
                var volume = root.GetComponentInChildren<ShipWaterVolume>(true);
                if (volume == null || volume.OceanCutout == null) throw new InvalidOperationException("Ship has no Ocean Cutout renderer.");
                var cut = volume.OceanCutout.GetComponent<ShipOceanCutout>();
                if (cut == null) cut = volume.OceanCutout.gameObject.AddComponent<ShipOceanCutout>();
                Bake(cut);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                AssetDatabase.SaveAssets();
            }
            finally { if (!inStage) PrefabUtility.UnloadPrefabContents(root); }
        }

        public static void Bake(ShipOceanCutout cut, bool persist = true, bool force = false)
        {
            var mesh = cut.GetComponent<MeshFilter>().sharedMesh;
            if (mesh == null) return;
            var meshPath = AssetDatabase.GetAssetPath(mesh);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long id);
            // v2 seals enclosed holes in the Y/Z silhouette (cannon windows,
            // gunports) without changing the open upper outline of the hull.
            var signature = $"sections-v2-{guid}-{id}-{AssetDatabase.GetAssetDependencyHash(meshPath)}";
            if (!force && cut.SourceMesh == mesh && cut.Sections != null && cut.SourceSignature == signature) return;
            var path = $"{Folder}/WaterCutSections-{Hash128.Compute(signature)}.asset";
            var texture = persist ? AssetDatabase.LoadAssetAtPath<Texture2D>(path) : null;
            if (texture == null || force)
            {
                var pixels = Build(mesh.vertices, mesh.triangles, mesh.bounds);
                if (texture == null)
                {
                    texture = new Texture2D(ShipOceanCutout.ProfileWidth, ShipOceanCutout.ProfileHeight, TextureFormat.RGFloat, false, true)
                    { name = mesh.name + " ocean cut sections", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                    if (persist) AssetDatabase.CreateAsset(texture, path);
                }
                texture.SetPixels(pixels); texture.Apply(false, false); EditorUtility.SetDirty(texture);
            }
            cut.SourceMesh = mesh; cut.SourceBounds = mesh.bounds; cut.Sections = texture; cut.SourceSignature = signature;
            cut.ProfileChanged(); EditorUtility.SetDirty(cut);
            if (PrefabUtility.IsPartOfPrefabInstance(cut)) PrefabUtility.RecordPrefabInstancePropertyModifications(cut);
        }

        // Rasterize the two outside hull boundaries along local X. Each texel is
        // a horizontal interval at local (Y,Z). Unlike a camera depth mask this
        // also fills an open hull between its sides, regardless of winding/UVs.
        public static Color[] Build(Vector3[] vertices, int[] triangles, Bounds bounds, bool sealWindows = true)
        {
            const int width = ShipOceanCutout.ProfileWidth, height = ShipOceanCutout.ProfileHeight;
            if (Mathf.Min(bounds.size.x, bounds.size.y, bounds.size.z) <= 0.00001f)
                throw new InvalidOperationException("Water Cut needs a hull/volume mesh with nonzero width, height and length.");
            var p = new Vector3[vertices.Length];
            for (var i = 0; i < p.Length; i++)
            {
                var v = vertices[i] - bounds.min;
                p[i] = new Vector3(v.x / bounds.size.x, v.y / bounds.size.y, v.z / bounds.size.z);
            }
            var pixels = new Color[width * height];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color(1, 0, 0, 1);
            for (var t = 0; t < triangles.Length; t += 3)
            {
                var a = p[triangles[t]]; var b = p[triangles[t + 1]]; var c = p[triangles[t + 2]];
                var denominator = (b.y - c.y) * (a.z - c.z) + (c.z - b.z) * (a.y - c.y);
                if (Mathf.Abs(denominator) < 1e-10f) continue;
                var minZ = Mathf.Clamp(Mathf.CeilToInt(Mathf.Min(a.z, b.z, c.z) * width - .5f), 0, width - 1);
                var maxZ = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(a.z, b.z, c.z) * width - .5f), 0, width - 1);
                var minY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Min(a.y, b.y, c.y) * height - .5f), 0, height - 1);
                var maxY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(a.y, b.y, c.y) * height - .5f), 0, height - 1);
                for (var y = minY; y <= maxY; y++) for (var z = minZ; z <= maxZ; z++)
                {
                    var sz = (z + .5f) / width; var sy = (y + .5f) / height;
                    var u = ((b.y - c.y) * (sz - c.z) + (c.z - b.z) * (sy - c.y)) / denominator;
                    var v = ((c.y - a.y) * (sz - c.z) + (a.z - c.z) * (sy - c.y)) / denominator;
                    if (u < -1e-5f || v < -1e-5f || u + v > 1.00001f) continue;
                    var x = u * a.x + v * b.x + (1 - u - v) * c.x;
                    ref var interval = ref pixels[y * width + z];
                    interval.r = Mathf.Min(interval.r, x); interval.g = Mathf.Max(interval.g, x);
                }
            }
            if (sealWindows) SealEnclosedHoles(pixels, width, height);
            return pixels;
        }

        // The model contains real openings, while separate window objects close
        // them in the ship. The mask only sees this mesh. Flood-fill empty texels
        // connected to the image border, then fill only enclosed empty islands.
        // The large open deck and the outside of the hull stay empty.
        public static int SealEnclosedHoles(Color[] pixels, int width, int height)
        {
            if (pixels == null || pixels.Length != width * height || width < 2 || height < 2)
                throw new ArgumentException("Invalid ocean cut section map.");
            var visited = new bool[pixels.Length];
            var queue = new int[pixels.Length];
            var head = 0; var tail = 0;
            bool Empty(int i) => pixels[i].r > pixels[i].g;
            void EnqueueOutside(int i)
            {
                if (!Empty(i) || visited[i]) return;
                visited[i] = true; queue[tail++] = i;
            }
            for (var x = 0; x < width; x++)
            { EnqueueOutside(x); EnqueueOutside((height - 1) * width + x); }
            for (var y = 1; y < height - 1; y++)
            { EnqueueOutside(y * width); EnqueueOutside(y * width + width - 1); }
            while (head < tail)
            {
                var i = queue[head++]; var x = i % width; var y = i / width;
                // Diagonal connections also lead outside; they must not become
                // artificial sealed pockets at the tapered bow/stern.
                for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                    if ((dx != 0 || dy != 0) && x + dx >= 0 && x + dx < width && y + dy >= 0 && y + dy < height)
                        EnqueueOutside(i + dy * width + dx);
            }
            var sealedPixels = 0;
            for (var start = 0; start < pixels.Length; start++)
            {
                if (!Empty(start) || visited[start]) continue;
                head = tail = 0; visited[start] = true; queue[tail++] = start;
                var left = 1f; var right = 0f;
                while (head < tail)
                {
                    var i = queue[head++]; var x = i % width; var y = i / width;
                    for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                    {
                        if ((dx == 0 && dy == 0) || x + dx < 0 || x + dx >= width || y + dy < 0 || y + dy >= height) continue;
                        var neighbour = i + dy * width + dx;
                        if (Empty(neighbour))
                        {
                            if (visited[neighbour]) continue;
                            visited[neighbour] = true; queue[tail++] = neighbour;
                        }
                        else
                        {
                            left = Mathf.Min(left, pixels[neighbour].r);
                            right = Mathf.Max(right, pixels[neighbour].g);
                        }
                    }
                }
                if (left > right) continue;
                var interval = new Color(left, right, 0, 1);
                for (var i = 0; i < tail; i++) pixels[queue[i]] = interval;
                sealedPixels += tail;
            }
            return sealedPixels;
        }
    }

    [CustomEditor(typeof(ShipOceanCutout))]
    public sealed class ShipOceanCutoutEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var cut = (ShipOceanCutout)target;
            EditorGUILayout.HelpBox("Убирает только океан внутри корпуса. Использует внешний левый и правый борта меша (локальная X — ширина, Y — высота, Z — длина). Верхняя крышка не нужна. Позиция, вращение и масштаб свободные. Коллайдер не нужен.", MessageType.Info);
            using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField("Сечения корпуса", cut.Sections, typeof(Texture2D), false);
            if (!cut.Ready) EditorGUILayout.HelpBox("Нужен Mesh Filter с объёмным мешем корпуса. Сечения создаются автоматически в редакторе.", MessageType.Warning);
            if (GUILayout.Button("Обновить форму из меша")) { ShipOceanCutoutBaker.Bake(cut, true, true); AssetDatabase.SaveAssets(); }
        }
    }

    public sealed class ShipOceanCutoutMaterialEditor : ShaderGUI
    {
        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            EditorGUILayout.HelpBox("Этот материал невидим. Для вырезания океана объекту с Mesh Filter нужен Ship Ocean Cutout. На корабле он уже подключён. Вырезается только Stylized Water 3, внутренняя вода и другие прозрачные эффекты остаются.", MessageType.Info);
            if (!GUILayout.Button("Добавить объёмную маску на выбранные объекты")) return;
            foreach (var go in Selection.gameObjects)
            {
                if (EditorUtility.IsPersistent(go) || !go.TryGetComponent<MeshRenderer>(out var renderer) || !go.TryGetComponent<MeshFilter>(out _)) continue;
                if (Array.IndexOf(renderer.sharedMaterials, materialEditor.target as Material) < 0) continue;
                var cut = go.GetComponent<ShipOceanCutout>();
                if (cut == null) cut = Undo.AddComponent<ShipOceanCutout>(go);
                ShipOceanCutoutBaker.Bake(cut);
            }
        }
    }
}
