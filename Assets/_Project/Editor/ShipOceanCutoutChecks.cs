using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using WaveByWave.Ships;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    public static class ShipOceanCutoutChecks
    {
        private static void Check(bool value, string reason)
        { if (!value) throw new InvalidOperationException(reason); }

        [MenuItem("Tools/Wave by Wave/Ships/Check ocean volume cutout")]
        public static void Run()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var target = new RenderTexture(800, 600, 24);
            var asynchronous = EditorSettings.asyncShaderCompilation;
            EditorSettings.asyncShaderCompilation = false;
            Material water = null, inside = null;
            try
            {
                var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Ship.prefab"));
                SceneManager.MoveGameObjectToScene(root, scene);
                foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                    if (component is not ShipOceanCutout) component.enabled = false;
                root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var volume = root.GetComponentInChildren<ShipWaterVolume>();
                var cut = volume.OceanCutout.GetComponent<ShipOceanCutout>();
                Check(cut != null && cut.Ready, "The player's authored hull has no prepared ocean mask.");
                var windowSample = CheckSealedWindows(cut, out var openSections);
                foreach (var renderer in root.GetComponentsInChildren<Renderer>()) renderer.enabled = renderer == volume.OceanCutout;

                var cameraGO = new GameObject("Water cut verification camera", typeof(Camera)); SceneManager.MoveGameObjectToScene(cameraGO, scene);
                var camera = cameraGO.GetComponent<Camera>(); camera.scene = scene;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.22f, .015f, .025f);
                camera.nearClipPlane = .02f; camera.farClipPlane = 150; camera.allowHDR = false;
                var lightGO = new GameObject("Water cut verification sun", typeof(Light)); SceneManager.MoveGameObjectToScene(lightGO, scene);
                var light = lightGO.GetComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.5f; light.transform.rotation = Quaternion.Euler(45, 20, 0);

                var ocean = GameObject.CreatePrimitive(PrimitiveType.Plane); SceneManager.MoveGameObjectToScene(ocean, scene);
                water = new Material(AssetDatabase.LoadAssetAtPath<Material>("Assets/Stylized Water 3/Materials/StylizedWater3_Smooth.mat"));
                water.shaderKeywords = Array.Empty<string>();
                water.SetFloat("_Cull", 0); water.SetFloat("_WaveHeight", 0); water.SetFloat("_EdgeFade", 0); water.SetFloat("_Speed", 0);
                water.SetColor("_BaseColor", new Color(.02f, .5f, .9f, 1)); water.SetColor("_ShallowColor", new Color(.02f, .5f, .9f, 1));
                ocean.GetComponent<Renderer>().sharedMaterial = water;
                ocean.transform.localScale = Vector3.one * 8;
                var sampleLocal = cut.SourceBounds.center;
                // A sample in the main hold, well below the open rim.
                sampleLocal.y = Mathf.Lerp(cut.SourceBounds.min.y, cut.SourceBounds.max.y, .48f);
                sampleLocal.z = Mathf.Lerp(cut.SourceBounds.min.z, cut.SourceBounds.max.z, .52f);
                var point = cut.transform.TransformPoint(sampleLocal);
                Check(cut.Contains(point), "Authored open hull must contain its hold, despite having no top cap.");
                Check(volume.MasksOceanAt(point), "Water queries must use the moved/scaled cutout child transform.");
                Check(!cut.Contains(cut.transform.TransformPoint(sampleLocal + Vector3.right * cut.SourceBounds.size.x)), "The ocean outside the hull must remain unmasked.");
                // Verify that the profile follows the bow rather than cutting its whole bounds box.
                var corner = cut.SourceBounds.min + Vector3.Scale(cut.SourceBounds.size, new Vector3(.03f, .48f, .03f));
                Check(!cut.Contains(cut.transform.TransformPoint(corner)), "A point outside the narrow bow must not be cut just because it is inside the mesh AABB.");

                Color Pixel(string name = null)
                {
                    ShipOceanCutout.Upload(camera);
                    RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                    var old = RenderTexture.active; RenderTexture.active = target;
                    var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                    try
                    {
                        image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
                        if (name != null)
                        {
                            Directory.CreateDirectory("Temp/ShipFloodingChecks");
                            File.WriteAllBytes("Temp/ShipFloodingChecks/" + name + ".png", image.EncodeToPNG());
                        }
                        return image.GetPixel(target.width / 2, target.height / 2);
                    }
                    finally { RenderTexture.active = old; Object.DestroyImmediate(image); }
                }
                float Difference(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);
                void View(Vector3 at, Vector3 offset) { camera.transform.position = at + offset; camera.transform.LookAt(at); }
                ocean.transform.position = new Vector3(point.x, point.y, point.z);
                View(point, new Vector3(.2f, 5, -2));
                ocean.SetActive(false); var background = Pixel(); ocean.SetActive(true);
                cut.enabled = false; var visible = Pixel("ocean-open-hull-before");
                Check(Difference(visible, background) > .15f, "Test must render visible actual SW3 ocean before masking.");
                cut.enabled = true; var masked = Pixel("ocean-open-hull-after");
                Check(Difference(masked, background) < .03f, $"Ocean still visible through the open top. before={visible}, masked={masked}, clear={background}");

                // Render through a real opening in the user's hull with the old
                // section data, then the sealed section data at the same point.
                var window = cut.transform.TransformPoint(windowSample);
                ocean.transform.position = window;
                View(window, new Vector3(.2f, 5, -2));
                var sealedSections = cut.Sections;
                var openTexture = new Texture2D(ShipOceanCutout.ProfileWidth, ShipOceanCutout.ProfileHeight, TextureFormat.RGFloat, false, true);
                try
                {
                    openTexture.SetPixels(openSections); openTexture.Apply(false, false);
                    cut.Sections = openTexture; cut.ProfileChanged();
                    var leak = Pixel("ocean-window-before");
                    cut.Sections = sealedSections; cut.ProfileChanged();
                    var closed = Pixel("ocean-window-after");
                    Check(Difference(leak, background) > .15f && Difference(closed, background) < .03f,
                        $"Ocean is still visible through a sealed window: old={leak}, new={closed}, clear={background}");
                }
                finally { cut.Sections = sealedSections; cut.ProfileChanged(); Object.DestroyImmediate(openTexture); }
                ocean.transform.position = new Vector3(point.x, point.y, point.z);

                View(point, new Vector3(.05f, -.3f, -.25f));
                Check(cut.Contains(camera.transform.position), "Underwater-view test camera must actually be inside the cutout.");
                var fromInside = Pixel("ocean-inside-hold");
                Check(Difference(fromInside, background) < .03f, "Ocean must also disappear when the camera is inside the hull, looking up.");
                var outside = point + Vector3.right * 4;
                View(outside, new Vector3(.1f, 4, -.5f));
                var outsideMasked = Pixel(); cut.enabled = false; var outsideUnmasked = Pixel(); cut.enabled = true;
                Check(Difference(outsideMasked, outsideUnmasked) < .03f && Difference(outsideMasked, background) > .15f, "Mask removed ocean beyond the ship's sides.");

                root.transform.SetPositionAndRotation(new Vector3(12, -3, 7), Quaternion.Euler(11, 37, -8));
                root.transform.localScale = new Vector3(1.1f, .9f, 1.2f);
                point = cut.transform.TransformPoint(sampleLocal);
                Check(cut.Contains(point) && volume.MasksOceanAt(point), "Moving, rotating and scaling the ship must transform the mask and CPU query together.");
                ocean.transform.position = new Vector3(point.x, point.y, point.z);
                View(point, new Vector3(.2f, 5, -2));
                Check(Difference(Pixel("ocean-moving-hull"), background) < .03f, "GPU mask did not follow the transformed ship.");
                volume.Present(0, true);
                Check(cut.Contains(point) && Difference(Pixel(), background) < .03f,
                    "Sinking must keep the ocean out of the hull so its walls stay visible underwater.");
                cut.enabled = false;
                Check(!cut.Contains(point) && Difference(Pixel(), background) > .15f,
                    "Disabling the cut renderer must restore ocean immediately.");
                cut.enabled = true;
                volume.Present(0, false);

                // A real interior-water shader drawn AFTER the ocean must stay visible.
                var interior = GameObject.CreatePrimitive(PrimitiveType.Plane); SceneManager.MoveGameObjectToScene(interior, scene);
                inside = new Material(Shader.Find("WaveByWave/Ships/Interior Water")) { renderQueue = 3001 };
                inside.SetColor("_DeepColor", new Color(.05f, .9f, .1f, 1)); inside.SetColor("_CrestColor", new Color(.05f, .9f, .1f, 1));
                inside.SetVector("_Ripples", Vector4.zero); inside.SetFloat("_FillHeight", 0);
                interior.GetComponent<Renderer>().sharedMaterial = inside; interior.transform.position = point - Vector3.up * .1f;
                interior.transform.localScale = Vector3.one * .1f;
                Check(Difference(Pixel("ocean-preserves-interior-water"), background) > .15f, "Ocean mask must not hide the separate interior-water surface.");
                interior.SetActive(false);

                // Actual ship view, with the user's open hull mesh and fittings unchanged.
                root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); root.transform.localScale = Vector3.one;
                foreach (var renderer in root.GetComponentsInChildren<Renderer>()) renderer.enabled = true;
                volume.Present(0, false); point = cut.transform.TransformPoint(sampleLocal);
                ocean.transform.position = new Vector3(0, point.y, 0);
                View(new Vector3(0, 2, .1f), new Vector3(1.8f, 4, -3));
                cut.enabled = false; Pixel("ship-ocean-before"); cut.enabled = true; Pixel("ship-ocean-after");
                Check(!ShaderUtil.ShaderHasError(water.shader), "SW3 ocean shader has compilation errors.");
                Debug.Log("[Ocean cutout checks] PASS: actual open hull, above/inside views, outside water, shape, moving transform, sinking and interior water.");
            }
            finally
            {
                EditorSettings.asyncShaderCompilation = asynchronous;
                if (water != null) Object.DestroyImmediate(water); if (inside != null) Object.DestroyImmediate(inside);
                target.Release(); Object.DestroyImmediate(target); EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static Vector3 CheckSealedWindows(ShipOceanCutout cut, out Color[] raw)
        {
            const int width = ShipOceanCutout.ProfileWidth, height = ShipOceanCutout.ProfileHeight;
            var profile = cut.Sections.GetPixels();
            var mesh = cut.GetComponent<MeshFilter>().sharedMesh;
            raw = ShipOceanCutoutBaker.Build(mesh.vertices, mesh.triangles, mesh.bounds, false);
            var sealedCount = 0;
            var sampleIndex = -1; var best = float.PositiveInfinity;
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i].r <= raw[i].g) continue;
                if (profile[i].r > profile[i].g) continue;
                sealedCount++;
                if (profile[i].g - profile[i].r < .15f) continue;
                var z = (i % width + .5f) / width; var y = (i / width + .5f) / height;
                var score = Mathf.Abs(z - .5f) + .5f * Mathf.Abs(y - .45f);
                if (score < best) { best = score; sampleIndex = i; }
            }
            Check(sealedCount > 0, "Authored window holes must be sealed in the saved profile.");
            Check(sampleIndex >= 0, "No testable central sample in a sealed hull opening.");
            var seam = new Color[7 * 7];
            for (var i = 0; i < seam.Length; i++) seam[i] = new Color(.2f, .8f, 0, 1);
            seam[3 * 7 + 3] = new Color(1, 0, 0, 1); // Window inside the hull.
            seam[6 * 7 + 3] = new Color(1, 0, 0, 1); // Outside the bow.
            seam[5 * 7 + 3] = new Color(1, 0, 0, 1);
            Check(ShipOceanCutoutBaker.SealEnclosedHoles(seam, 7, 7) == 1 &&
                seam[3 * 7 + 3].r <= seam[3 * 7 + 3].g && seam[6 * 7 + 3].r > seam[6 * 7 + 3].g,
                "Window holes must close while outside-connected gaps stay open.");
            // The open deck reaches the top border in this ship model and must
            // remain outside the masking volume after sealing the gunports.
            var openDeck = 0;
            for (var x = 0; x < width; x++)
                if (raw[(height - 1) * width + x].r > raw[(height - 1) * width + x].g &&
                    profile[(height - 1) * width + x].r > profile[(height - 1) * width + x].g) openDeck++;
            Check(openDeck > 0, "The open upper edge of the hull must remain unmasked.");
            var normalized = new Vector3(
                .5f * (profile[sampleIndex].r + profile[sampleIndex].g),
                (sampleIndex / width + .5f) / height,
                (sampleIndex % width + .5f) / width);
            return cut.SourceBounds.min + Vector3.Scale(cut.SourceBounds.size, normalized);
        }
    }
}
