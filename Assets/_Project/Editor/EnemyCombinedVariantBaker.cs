using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using WaveByWave.Enemies;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    public static class EnemyCombinedVariantBaker
    {
        private const string Folder = "Assets/_Project/Data/Enemies/Combined";

        public static string SourceHash(DotsEnemyCatalog catalog)
        {
            var text = new StringBuilder("combined-v1|").Append(catalog.CombinedVariantsPerType)
                .Append('|').Append(catalog.BakeSourceHash);
            foreach (var part in catalog.BakedParts)
                text.Append('|').Append(AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(part.Mesh)));
            foreach (var material in catalog.SkeletonMaterials)
                if (material != null) text.Append('|').Append(AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(material)));
            return Hash128.Compute(text.ToString()).ToString();
        }

        private static Texture BaseTexture(EnemyBakedPart part, Material material, DotsEnemyCatalog catalog, int skin)
        {
            if (part.Category == EnemyBakedPartCategory.Body && skin < catalog.SkeletonMaterials.Length &&
                catalog.SkeletonMaterials[skin] != null)
                material = catalog.SkeletonMaterials[skin];
            return material != null && material.HasProperty("_BaseMap") && material.GetTexture("_BaseMap") != null
                ? material.GetTexture("_BaseMap") : Texture2D.whiteTexture;
        }

        [MenuItem("Tools/Wave by Wave/Enemies/Bake combined skeleton variants")]
        public static void BakeDefault() => Bake(AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(EnemyContentSetup.CatalogPath));

        public static void Bake(DotsEnemyCatalog catalog)
        {
            if (EditorApplication.isPlaying || catalog == null || !catalog.IsBaked)
                throw new InvalidOperationException("Stop Play Mode and bake the source animation parts first.");
            Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();
            var sources = new List<Texture>();
            foreach (var part in catalog.BakedParts)
            foreach (var material in part.Materials)
            for (var skin = 0; skin < Math.Max(1, catalog.SkeletonMaterials.Length); skin++)
            {
                var texture = BaseTexture(part, material, catalog, skin);
                if (!sources.Contains(texture)) sources.Add(texture);
            }
            var copies = new List<Texture2D>();
            var output = new List<EnemyBakedVariant>();
            Texture2D atlasScratch = null;
            try
            {
                foreach (var source in sources) copies.Add(ReadTexture(source));
                atlasScratch = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                var rects = atlasScratch.PackTextures(copies.ToArray(), 8, 4096, false);
                atlasScratch.name = "Skeleton combined color atlas";
                atlasScratch.wrapMode = TextureWrapMode.Clamp;
                atlasScratch.filterMode = FilterMode.Bilinear;
                var atlasPath = Folder + "/SkeletonColorAtlas.asset";
                var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(atlasPath);
                if (atlas == null)
                {
                    atlas = atlasScratch; atlasScratch = null;
                    AssetDatabase.CreateAsset(atlas, atlasPath);
                }
                else { EditorUtility.CopySerialized(atlasScratch, atlas); EditorUtility.SetDirty(atlas); }
                var count = Mathf.Clamp(catalog.CombinedVariantsPerType, 1, 32);
                for (var type = 0; type < 3; type++)
                for (var variant = 0; variant < count; variant++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Bake whole skeletons",
                        $"{(EnemyCombatType)type}: {variant + 1}/{count}", (type * count + variant) / (float)(count * 3)))
                        throw new OperationCanceledException("Combined skeleton bake cancelled.");
                    var seed = Unity.Mathematics.math.hash(new Unity.Mathematics.uint2((uint)type + 1, (uint)variant + 137)) | 1u;
                    var selected = new List<int>();
                    DotsEnemyPresentation.SelectParts(seed, (EnemyCombatType)type, catalog, selected);
                    var skin = variant % Math.Max(1, catalog.SkeletonMaterials.Length);
                    output.Add(new EnemyBakedVariant { CombatType = (EnemyCombatType)type, Seed = seed,
                        Visual = Combine(catalog, selected, skin, sources, rects, atlas,
                            $"{Folder}/Skeleton_{(EnemyCombatType)type}_{variant:00}.asset") });
                }
                catalog.CombinedVariants = output;
                catalog.CombinedSourceHash = SourceHash(catalog);
                EditorUtility.SetDirty(catalog);
                AssetDatabase.SaveAssets();
                Debug.Log($"[Enemies] Baked {output.Count} whole skeleton variants: one mesh and one material per variant.");
            }
            finally
            {
                foreach (var copy in copies) Object.DestroyImmediate(copy);
                if (atlasScratch != null) Object.DestroyImmediate(atlasScratch);
                EditorUtility.ClearProgressBar();
            }
        }

        private static Texture2D ReadTexture(Texture source)
        {
            var target = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            var oldSrgb = GL.sRGBWrite;
            try
            {
                GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                var result = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, false);
                result.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                result.Apply(false, false);
                return result;
            }
            finally { GL.sRGBWrite = oldSrgb; RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
        }

        private static EnemyBakedPart Combine(DotsEnemyCatalog catalog, List<int> selected, int skin,
            List<Texture> textures, Rect[] rects, Texture2D atlas, string path)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var lookup = new List<Vector2>();
            var colors = new List<Color>();
            var indices = new List<int>();
            var sourceVertices = new List<(EnemyBakedPart Part, int Vertex)>();
            var bounds = new Bounds();
            var hasBounds = false;
            foreach (var index in selected)
            {
                var part = catalog.BakedParts[index];
                if (!hasBounds) { bounds = part.Mesh.bounds; hasBounds = true; }
                else bounds.Encapsulate(part.Mesh.bounds);
                var partVertices = part.Mesh.vertices;
                var partNormals = part.Mesh.normals;
                var partUV = part.Mesh.uv;
                for (var sub = 0; sub < Math.Min(part.Mesh.subMeshCount, part.Materials.Length); sub++)
                {
                    var material = part.Materials[sub];
                    var rect = rects[textures.IndexOf(BaseTexture(part, material, catalog, skin))];
                    var tint = material != null && material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : Color.white;
                    var scale = material != null ? material.GetTextureScale("_BaseMap") : Vector2.one;
                    var offset = material != null ? material.GetTextureOffset("_BaseMap") : Vector2.zero;
                    var remap = new Dictionary<int, int>();
                    foreach (var vertex in part.Mesh.GetTriangles(sub))
                    {
                        if (!remap.TryGetValue(vertex, out var combined))
                        {
                            combined = vertices.Count;
                            remap.Add(vertex, combined);
                            var uv = vertex < partUV.Length ? partUV[vertex] : Vector2.zero;
                            uv = Vector2.Scale(uv, scale) + offset;
                            if (uv.x < -0.001f || uv.x > 1.001f || uv.y < -0.001f || uv.y > 1.001f)
                                throw new InvalidOperationException($"{part.Name}: repeating/out-of-range UVs need an atlas-compatible material before combining.");
                            uvs.Add(new Vector2(Mathf.Lerp(rect.xMin, rect.xMax, Mathf.Clamp01(uv.x)),
                                Mathf.Lerp(rect.yMin, rect.yMax, Mathf.Clamp01(uv.y))));
                            vertices.Add(partVertices[vertex]);
                            normals.Add(vertex < partNormals.Length ? partNormals[vertex] : Vector3.up);
                            colors.Add(tint);
                            lookup.Add(new Vector2(combined, 0));
                            sourceVertices.Add((part, vertex));
                        }
                        indices.Add(combined);
                    }
                }
            }
            var width = Mathf.NextPowerOfTwo(vertices.Count);
            if (vertices.Count == 0 || width > SystemInfo.maxTextureSize)
                throw new InvalidOperationException($"Combined skeleton has unsupported vertex count: {vertices.Count}.");
            var rows = catalog.BakedParts[0].Positions.height;
            var positionPixels = new Color[width * rows];
            var normalPixels = new Color[width * rows];
            var positionSources = new Dictionary<EnemyBakedPart, Color[]>();
            var normalSources = new Dictionary<EnemyBakedPart, Color[]>();
            foreach (var index in selected)
            {
                var part = catalog.BakedParts[index];
                if (part.Positions.height != rows || part.Normals.height != rows)
                    throw new InvalidOperationException("Animation layouts differ; rebake all source parts first.");
                positionSources[part] = part.Positions.GetPixels();
                normalSources[part] = part.Normals.GetPixels();
            }
            for (var row = 0; row < rows; row++)
            for (var vertex = 0; vertex < sourceVertices.Count; vertex++)
            {
                var source = sourceVertices[vertex];
                positionPixels[row * width + vertex] = positionSources[source.Part][row * source.Part.Positions.width + source.Vertex];
                normalPixels[row * width + vertex] = normalSources[source.Part][row * source.Part.Normals.width + source.Vertex];
            }
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null) { mesh = new Mesh(); AssetDatabase.CreateAsset(mesh, path); }
            mesh.Clear(); mesh.name = Path.GetFileNameWithoutExtension(path);
            mesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetUVs(0, uvs); mesh.SetUVs(3, lookup);
            mesh.SetColors(colors); mesh.SetTriangles(indices, 0); mesh.bounds = bounds;
            var positions = SaveTexture(path, "Positions", width, rows, positionPixels);
            var ns = SaveTexture(path, "Normals", width, rows, normalPixels);
            var mat = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>().FirstOrDefault();
            if (mat == null)
            {
                mat = new Material(Shader.Find("WaveByWave/EnemyVertexAnimation")) { name = "Combined material" };
                AssetDatabase.AddObjectToAsset(mat, path);
            }
            mat.enableInstancing = true;
            mat.SetTexture("_BaseMap", atlas); mat.SetColor("_BaseColor", Color.white);
            mat.SetTexture("_PositionFrames", positions); mat.SetTexture("_NormalFrames", ns);
            mat.SetFloat("_UseVertexColor", 1);
            EditorUtility.SetDirty(mesh); EditorUtility.SetDirty(mat);
            return new EnemyBakedPart { Name = mesh.name, Mesh = mesh, Positions = positions, Normals = ns,
                Materials = new[] { mat }, Clips = catalog.BakedParts[0].Clips };
        }

        private static Texture2D SaveTexture(string path, string name, int width, int rows, Color[] pixels)
        {
            var texture = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Texture2D>().FirstOrDefault(x => x.name == name);
            if (texture == null)
            {
                texture = new Texture2D(width, rows, TextureFormat.RGBAHalf, false, true) { name = name };
                AssetDatabase.AddObjectToAsset(texture, path);
            }
            else texture.Reinitialize(width, rows, TextureFormat.RGBAHalf, false);
            texture.filterMode = FilterMode.Point; texture.wrapMode = TextureWrapMode.Clamp;
            texture.SetPixels(pixels); texture.Apply(false, false); EditorUtility.SetDirty(texture);
            return texture;
        }
    }
}
