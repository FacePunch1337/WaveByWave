using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using WaveByWave.Ships;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    public static class ShipFloodingSetup
    {
        public const string Folder = "Assets/_Project/Generated/ShipDamage";
        private const string Request = "Temp/ShipFloodingSetup.request";
        [InitializeOnLoadMethod]
        private static void Install() => EditorApplication.update += Requested;
        private static void Requested()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (SourcesNewerThanAssembly()) { AssetDatabase.Refresh(); return; }
            File.Delete(Request);
            try { Configure(); ShipFloodingChecks.Run(); File.WriteAllText("Temp/ShipFloodingSetup.result", "PASS: configured ship, UV sites, materials, bucket spray and flooding checks."); }
            catch (Exception e) { File.WriteAllText("Temp/ShipFloodingSetup.result", e.ToString()); Debug.LogException(e); }
        }

        private static bool SourcesNewerThanAssembly()
        {
            var editor = File.GetLastWriteTimeUtc(typeof(ShipFloodingSetup).Assembly.Location);
            var runtime = File.GetLastWriteTimeUtc(typeof(ShipFlooding).Assembly.Location);
            foreach (var file in new[] { "ShipFloodingSetup.cs", "ShipFloodingChecks.cs" })
                if (File.GetLastWriteTimeUtc("Assets/_Project/Editor/" + file) > editor) return true;
            foreach (var file in new[] { "Ships/ShipFlooding.cs", "Ships/ShipHullHoles.cs", "Ships/ShipWaterVolume.cs",
                "Physics/KinematicShipCollision.cs", "Player/PlayerEquipment.cs", "Player/NetworkPlayerController.cs" })
                if (File.GetLastWriteTimeUtc("Assets/_Project/Runtime/" + file) > runtime) return true;
            return false;
        }

        [MenuItem("Tools/Wave by Wave/Ships/Configure player flooding and water cut")]
        public static void Configure()
        {
            Directory.CreateDirectory(Folder); AssetDatabase.Refresh();
            var hole = HoleTexture();
            var cut = Material("WaterCut", "WaveByWave/Ships/Water Cut");
            var water = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/Stylized Water 3/Materials/StylizedWater3_Smooth.mat");
            if (water == null) throw new InvalidOperationException("Stylized Water 3 material is missing.");
            var spray = Material("LeakSpray", "Universal Render Pipeline/Particles/Unlit");
            spray.SetFloat("_Surface", 1); spray.SetFloat("_Blend", 0);
            spray.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); spray.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            spray.SetFloat("_ZWrite", 0); spray.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); spray.renderQueue = 2998;
            spray.SetColor("_BaseColor", Color.white);
            EditorUtility.SetDirty(spray);
            const string path = "Assets/_Project/Prefabs/Ship.prefab";
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var hullFilter = root.GetComponentsInChildren<MeshFilter>(true).First(x => x.name == "StylShip_Body");
                var hull = hullFilter.GetComponent<ShipHullHoles>();
                var newHull = hull == null;
                if (newHull) hull = hullFilter.gameObject.AddComponent<ShipHullHoles>();
                var renderer = hullFilter.GetComponent<MeshRenderer>();
                var old = renderer.sharedMaterial;
                var material = Material("PlayerHull", "WaveByWave/Ships/Hull Holes");
                if (old != null && old != material)
                {
                    var texName = old.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
                    if (old.HasProperty(texName))
                    { material.SetTexture("_BaseMap", old.GetTexture(texName)); material.SetTextureScale("_BaseMap", old.GetTextureScale(texName)); material.SetTextureOffset("_BaseMap", old.GetTextureOffset(texName)); }
                    var colorName = old.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
                    if (old.HasProperty(colorName)) material.SetColor("_BaseColor", old.GetColor(colorName));
                }
                material.SetTexture("_HoleTex", hole); renderer.sharedMaterial = material; EditorUtility.SetDirty(material);
                var deck = root.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(x => x.name == "StylShip_FloorMid");
                var deckY = deck != null ? root.transform.InverseTransformPoint(deck.transform.TransformPoint(
                    new Vector3(deck.sharedMesh.bounds.center.x, deck.sharedMesh.bounds.max.y, deck.sharedMesh.bounds.center.z))).y : 2.1f;
                if (newHull)
                {
                    var bounds = hullFilter.sharedMesh.bounds;
                    var deckLocal = hull.transform.InverseTransformPoint(root.transform.TransformPoint(new Vector3(0, deckY, 0))).y;
                    hull.AllowedHeight = new Vector2(deckLocal + 0.05f, bounds.max.y - bounds.size.y * 0.12f);
                    hull.HoleRadius = 0.15f / Mathf.Max(0.01f, hull.transform.lossyScale.x);
                    hull.ShowAllowedRegions = false;
                }
                RebuildSites(hull);
                if (hull.Sites.Length == 0) throw new InvalidOperationException("The hull has no valid UV sites. Check UV rectangles and height range.");
                var flooding = root.GetComponent<ShipFlooding>();
                if (flooding == null) flooding = root.AddComponent<ShipFlooding>();
                flooding.Hull = hull; flooding.SprayMaterial = spray;
                if (flooding.WaterVolume == null)
                {
                    var volumeObject = new GameObject("Flood water volume"); volumeObject.transform.SetParent(root.transform, false);
                    var volume = volumeObject.AddComponent<ShipWaterVolume>();
                    var vertices = hullFilter.sharedMesh.vertices.Select(p => root.transform.InverseTransformPoint(hull.transform.TransformPoint(p))).ToArray();
                    var bounds = new Bounds(vertices[0], Vector3.zero);
                    foreach (var p in vertices) bounds.Encapsulate(p);
                    var outline = ConvexHull(vertices.Select(p => new Vector2(p.x, p.z)).ToArray());
                    var center = new Vector2(bounds.center.x, bounds.center.z);
                    for (var i = 0; i < outline.Length; i++) outline[i] = center + (outline[i] - center) * 0.86f;
                    var bottom = bounds.min.y + bounds.size.y * 0.2f;
                    var top = bounds.max.y - bounds.size.y * 0.12f;
                    var mesh = CreateVolumeMesh(outline, bottom, top);
                    var existingMesh = AssetDatabase.LoadAssetAtPath<Mesh>(Folder + "/DefaultHullVolume.asset");
                    if (existingMesh == null) AssetDatabase.CreateAsset(mesh, Folder + "/DefaultHullVolume.asset");
                    else { EditorUtility.CopySerialized(mesh, existingMesh); Object.DestroyImmediate(mesh); mesh = existingMesh; }
                    volume.LocalBounds = mesh.bounds;
                    var min = volume.LocalBounds.min; min.y = Mathf.Min(top - 0.3f, deckY + 0.015f);
                    var fillBounds = volume.LocalBounds; fillBounds.SetMinMax(min, fillBounds.max); volume.LocalBounds = fillBounds;
                    volume.Footprint = outline;
                    volume.StylizedWaterMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                        "Assets/Stylized Water 3/Materials/StylizedWater3_Smooth.mat");
                    volume.EditorFillPreview = 0f;
                    volume.OceanCutout = AddMesh(volume.transform, "Ocean cutout", mesh, cut);
                    volume.InteriorWater = AddMesh(volume.transform, "Interior water", mesh, water);
                    flooding.WaterVolume = volume;
                }
                if (flooding.WaterVolume.StylizedWaterMaterial == null)
                    flooding.WaterVolume.StylizedWaterMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                        "Assets/Stylized Water 3/Materials/StylizedWater3_Smooth.mat");
                if (flooding.WaterVolume.InteriorWater != null)
                    flooding.WaterVolume.InteriorWater.sharedMaterial = flooding.WaterVolume.StylizedWaterMaterial;
                var oceanCut = flooding.WaterVolume.OceanCutout.GetComponent<ShipOceanCutout>();
                if (oceanCut == null) oceanCut = flooding.WaterVolume.OceanCutout.gameObject.AddComponent<ShipOceanCutout>();
                ShipOceanCutoutBaker.Bake(oceanCut);
                flooding.WaterVolume.Present(flooding.WaterVolume.EditorFillPreview, false);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                File.WriteAllText("Temp/ShipFloodingGeometry.txt", $"Hull local bounds: {hullFilter.sharedMesh.bounds}\nHull root position: {root.transform.InverseTransformPoint(hull.transform.position)}\nSites: {hull.Sites.Length}\nVolume: {flooding.WaterVolume.LocalBounds}\n" +
                    string.Join("\n", root.GetComponentsInChildren<MeshFilter>(true).Where(f => f.name.Contains("Floor") || f.name.Contains("Deck") || f.name.Contains("Body")).Select(f => $"{f.name}: {root.transform.InverseTransformPoint(f.transform.TransformPoint(f.sharedMesh.bounds.center))}, size {f.sharedMesh.bounds.size}")));
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            ConfigureBucketSpray();
            AssetDatabase.SaveAssets();
            Debug.Log("[Ship flooding] Ship prefab, materials, water profile, breach sites and bucket spray configured.");
        }

        private static MeshRenderer AddMesh(Transform parent, string name, Mesh mesh, Material material)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer)); go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.GetComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            return renderer;
        }

        private static Material Material(string name, string shaderName)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) throw new InvalidOperationException("Missing shader " + shaderName);
            var path = Folder + "/" + name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
            else mat.shader = shader;
            return mat;
        }

        private static Texture2D HoleTexture()
        {
            var path = Folder + "/HullBreach.png";
            if (!File.Exists(path))
            {
                const int size = 256;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                var colors = new Color[size * size];
                for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
                {
                    var p = (new Vector2(x, y) + Vector2.one * 0.5f) / size * 2f - Vector2.one;
                    var angle = Mathf.Atan2(p.y, p.x);
                    var edge = 0.59f + 0.055f * Mathf.Sin(angle * 11f) + 0.025f * Mathf.Sin(angle * 23f + 1f);
                    var radius = p.magnitude;
                    var alpha = Mathf.SmoothStep(0f, 1f, (radius - edge) / 0.035f);
                    var rim = Mathf.SmoothStep(0f, 1f, (radius - edge - 0.02f) / 0.2f);
                    var color = Color.Lerp(new Color(0.18f, 0.075f, 0.025f), Color.white, rim);
                    color.a = alpha; colors[y * size + x] = color;
                }
                texture.SetPixels(colors); texture.Apply(); File.WriteAllBytes(path, texture.EncodeToPNG()); Object.DestroyImmediate(texture);
                AssetDatabase.ImportAsset(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.alphaSource = TextureImporterAlphaSource.FromInput; importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp; importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        public static void RebuildSites(ShipHullHoles hull)
        {
            var mesh = hull.GetComponent<MeshFilter>().sharedMesh;
            if (mesh == null) return;
            var vertices = mesh.vertices; var uv = mesh.uv; var indices = mesh.triangles;
            if (uv.Length != vertices.Length) throw new InvalidOperationException("Hull requires UV0.");
            var sites = new List<HullHoleSite>();
            var random = new System.Random(61739);
            var validCount = 0;
            for (var t = 0; t < indices.Length; t += 3)
            {
                var a = indices[t]; var b = indices[t + 1]; var c = indices[t + 2];
                var ab = vertices[b] - vertices[a]; var ac = vertices[c] - vertices[a];
                var cross = Vector3.Cross(ab, ac); var area = cross.magnitude * 0.5f;
                if (area < 0.000001f) continue;
                var normal = cross.normalized;
                var density = new Vector2((Mathf.Abs(uv[b].x - uv[a].x) + Mathf.Abs(uv[c].x - uv[a].x)) /
                    Mathf.Max(0.00001f, ab.magnitude + ac.magnitude),
                    (Mathf.Abs(uv[b].y - uv[a].y) + Mathf.Abs(uv[c].y - uv[a].y)) / Mathf.Max(0.00001f, ab.magnitude + ac.magnitude));
                density = Vector2.Max(density * 2f, Vector2.one * 0.002f);
                var count = Mathf.Clamp(Mathf.CeilToInt(area * 24f), 3, 160);
                for (var sample = 0; sample < count; sample++)
                {
                    var u = Mathf.Sqrt((float)random.NextDouble()); var v = (float)random.NextDouble();
                    var wa = 1f - u; var wb = u * (1f - v); var wc = u * v;
                    var site = new HullHoleSite { Position = vertices[a] * wa + vertices[b] * wb + vertices[c] * wc,
                        UV = uv[a] * wa + uv[b] * wb + uv[c] * wc, Normal = normal, UVPerMetre = density };
                    if (!hull.IsAllowed(site.UV, hull.RadiusUV(site), site.Position.y, site.Normal)) continue;
                    validCount++;
                    if (sites.Count < 2048) sites.Add(site);
                    else
                    {
                        // Reservoir sampling covers the entire hull even when the model
                        // contains many more triangles than our fixed runtime site budget.
                        var replace = random.Next(validCount);
                        if (replace < sites.Count) sites[replace] = site;
                    }
                }
            }
            hull.Sites = sites.ToArray(); EditorUtility.SetDirty(hull);
        }

        public static Vector2[] ConvexHull(Vector2[] source)
        {
            var points = source.Distinct().OrderBy(p => p.x).ThenBy(p => p.y).ToArray();
            if (points.Length < 3) return points;
            var result = new List<Vector2>();
            float Cross(Vector2 a, Vector2 b, Vector2 c) => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            foreach (var p in points)
            { while (result.Count >= 2 && Cross(result[^2], result[^1], p) <= 0f) result.RemoveAt(result.Count - 1); result.Add(p); }
            var lower = result.Count;
            for (var i = points.Length - 2; i >= 0; i--)
            { var p = points[i]; while (result.Count > lower && Cross(result[^2], result[^1], p) <= 0f) result.RemoveAt(result.Count - 1); result.Add(p); }
            result.RemoveAt(result.Count - 1); return result.ToArray();
        }

        public static Mesh CreateVolumeMesh(Vector2[] outline, float bottom, float top)
        {
            var n = outline.Length; var vertices = new Vector3[n * 2]; var triangles = new List<int>();
            for (var i = 0; i < n; i++)
            {
                vertices[i] = new Vector3(outline[i].x, bottom, outline[i].y);
                vertices[i + n] = new Vector3(outline[i].x, top, outline[i].y);
                var j = (i + 1) % n;
                triangles.AddRange(new[] { i, i + n, j + n, i, j + n, j });
                if (i > 0 && i < n - 1) triangles.AddRange(new[] { 0, i + 1, i, n, n + i, n + i + 1 });
            }
            var mesh = new Mesh { name = "Default hull water volume" }; mesh.vertices = vertices;
            mesh.triangles = triangles.ToArray(); mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }

        private static void ConfigureBucketSpray()
        {
            const string path = "Assets/_Project/Prefabs/Effects/BucketPour.prefab";
            var go = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var ps = go.GetComponent<ParticleSystem>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = ps.main; main.startSpeed = new ParticleSystem.MinMaxCurve(3.8f, 4.8f);
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.85f); main.gravityModifier = 1f;
                main.simulationSpace = ParticleSystemSimulationSpace.World; main.maxParticles = 100;
                var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = 12f; shape.radius = 0.08f;
                var velocity = ps.velocityOverLifetime; velocity.enabled = true; velocity.space = ParticleSystemSimulationSpace.World;
                velocity.y = 1.3f;
                var emission = ps.emission; emission.rateOverTime = 0f; emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 48) });
                var renderer = go.GetComponent<ParticleSystemRenderer>();
                var spray = AssetDatabase.LoadAssetAtPath<Material>(Folder + "/LeakSpray.mat");
                var previous = renderer.sharedMaterial;
                if (previous != null && previous != spray && previous.HasProperty("_BaseMap"))
                { spray.SetTexture("_BaseMap", previous.GetTexture("_BaseMap")); EditorUtility.SetDirty(spray); }
                renderer.sharedMaterial = spray;
                PrefabUtility.SaveAsPrefabAsset(go, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(go); }
        }
    }
}
