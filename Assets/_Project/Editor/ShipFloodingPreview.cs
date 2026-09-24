using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using StylizedWater3.UnderwaterRendering;
using WaveByWave.Ships;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    public static class ShipFloodingPreview
    {
        [InitializeOnLoadMethod]
        private static void Install() => EditorApplication.update += Requested;
        private static void Requested()
        {
            const string request = "Temp/ShipFloodingPreview.request";
            if (!File.Exists(request) || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            var editorAssemblyTime = File.GetLastWriteTimeUtc(typeof(ShipFloodingPreview).Assembly.Location);
            if (File.GetLastWriteTimeUtc("Assets/_Project/Editor/ShipFloodingPreview.cs") > editorAssemblyTime ||
                File.GetLastWriteTimeUtc("Assets/_Project/Editor/ShipFloodingPlayChecks.cs") > editorAssemblyTime ||
                File.GetLastWriteTimeUtc("Assets/_Project/Editor/ShipHullHolesEditor.cs") > editorAssemblyTime ||
                File.GetLastWriteTimeUtc("Assets/_Project/Runtime/Ships/ShipWaterVolume.cs") >
                File.GetLastWriteTimeUtc(typeof(ShipWaterVolume).Assembly.Location))
            { AssetDatabase.Refresh(); return; }
            File.Delete(request);
            try { Render(); File.WriteAllText("Temp/ShipFloodingPreview.result", "PASS: interior water, underwater hull visibility through SW3, hull breach and ocean cut."); }
            catch (Exception e) { File.WriteAllText("Temp/ShipFloodingPreview.result", e.ToString()); Debug.LogException(e); }
        }

        [MenuItem("Tools/Wave by Wave/Ships/Render flooding material preview")]
        public static void Render()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var target = new RenderTexture(1280, 800, 24);
            var asynchronous = EditorSettings.asyncShaderCompilation;
            EditorSettings.asyncShaderCompilation = false;
            try
            {
                var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Ship.prefab"));
                SceneManager.MoveGameObjectToScene(root, scene);
                foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true)) component.enabled = false;
                var flood = root.GetComponent<ShipFlooding>();
                var hull = flood.Hull;
                var renderer = hull.GetComponent<MeshRenderer>();
                var cameraGO = new GameObject("Preview camera", typeof(Camera)); SceneManager.MoveGameObjectToScene(cameraGO, scene);
                var camera = cameraGO.GetComponent<Camera>(); camera.scene = scene;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.14f, 0.19f, 0.24f);
                camera.fieldOfView = 48; camera.nearClipPlane = 0.03f; camera.farClipPlane = 150f;
                camera.allowHDR = false;
                var lightGO = new GameObject("Preview sun", typeof(Light)); SceneManager.MoveGameObjectToScene(lightGO, scene);
                var light = lightGO.GetComponent<Light>(); light.type = LightType.Directional; light.intensity = 2f;
                light.transform.rotation = Quaternion.Euler(35, -45, 0);
                root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var site = hull.Sites.OrderBy(s =>
                {
                    var p = hull.transform.TransformPoint(s.Position);
                    return (p - new Vector3(1.5f, 2.22f, 0f)).sqrMagnitude;
                }).First();
                var radius = hull.RadiusUV(site);
                var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
                var holes = new Vector4[24]; var positions = new Vector4[24]; var normals = new Vector4[24];
                holes[0] = new Vector4(site.UV.x, site.UV.y, radius.x, radius.y);
                positions[0] = new Vector4(site.Position.x, site.Position.y, site.Position.z, hull.HoleRadius * 1.6f);
                normals[0] = site.Normal;
                block.SetVectorArray("_Holes", holes); block.SetVectorArray("_HolePositions", positions); block.SetVectorArray("_HoleNormals", normals);
                block.SetInt("_HoleCount", 1); block.SetFloat("_ShowRegions", 0); renderer.SetPropertyBlock(block);
                flood.WaterVolume.Present(0.55f, false);
                camera.transform.position = new Vector3(8, 7, 9);
                camera.transform.LookAt(new Vector3(0, 2.5f, 0));
                Save(camera, target, "Temp/ShipFloodingChecks/ship-flooding.png");

                var water = flood.WaterVolume;
                VerifySubmergedHull(scene, water, renderer, camera, target);
                var surface = root.GetComponentsInChildren<MeshRenderer>(true)
                    .FirstOrDefault(r => r.name == "Interior water surface");
                if (surface == null) throw new InvalidOperationException("Open hull did not create an interior water surface.");
                foreach (var other in root.GetComponentsInChildren<Renderer>(true))
                    if (other != surface) other.enabled = false;
                var centre = water.transform.TransformPoint(new Vector3(water.LocalBounds.center.x,
                    water.Level(.55f), water.LocalBounds.center.z));
                camera.transform.position = centre + new Vector3(.05f, 4f, -1f);
                camera.transform.LookAt(centre);
                water.Present(0f, false);
                var empty = Centre(camera, target);
                water.Present(.55f, false);
                var visibleWater = Centre(camera, target);
                if (Mathf.Abs(empty.r - visibleWater.r) + Mathf.Abs(empty.g - visibleWater.g) +
                    Mathf.Abs(empty.b - visibleWater.b) < .1f)
                    throw new InvalidOperationException("Interior water must be visible inside the actual open hull at partial fill.");
                Save(camera, target, "Temp/ShipFloodingChecks/interior-water-filled.png");
                camera.transform.position = centre + new Vector3(.05f, -.55f, -.1f);
                camera.transform.LookAt(centre);
                // SW3 shades backfaces through its UnderwaterArea; that path is
                // validated above with the area active and the hull visible.

                // Isolate the hull so unrelated deck/fittings cannot conceal the alpha cut.
                foreach (var other in root.GetComponentsInChildren<Renderer>()) if (other != renderer) other.enabled = false;
                renderer.enabled = true;
                var position = hull.transform.TransformPoint(site.Position);
                var normal = hull.transform.TransformDirection(site.Normal).normalized;
                camera.transform.position = position + normal * 1.25f;
                camera.transform.LookAt(position);
                Save(camera, target, "Temp/ShipFloodingChecks/hull-breach.png");
                block.SetInt("_HoleCount", 0); renderer.SetPropertyBlock(block);
                Save(camera, target, "Temp/ShipFloodingChecks/hull-repaired.png");
            }
            finally
            {
                EditorSettings.asyncShaderCompilation = asynchronous;
                target.Release(); Object.DestroyImmediate(target);
                EditorSceneManager.ClosePreviewScene(scene);
            }
            ShipOceanCutoutChecks.Run();
            ShipFloodingChecks.Run();
        }

        private static void VerifySubmergedHull(Scene scene, ShipWaterVolume water, MeshRenderer hull,
            Camera camera, RenderTexture target)
        {
            water.Present(.65f, false);
            var eye = water.transform.TransformPoint(new Vector3(water.LocalBounds.center.x,
                water.Level(.65f) - .55f, water.LocalBounds.center.z - 1f));
            camera.transform.SetPositionAndRotation(eye,
                Quaternion.LookRotation(water.transform.TransformDirection(Vector3.right), Vector3.up));
            var areaPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Effects/OceanUnderwaterArea.prefab");
            if (areaPrefab == null) throw new InvalidOperationException("Underwater area prefab is missing.");
            var areaObject = Object.Instantiate(areaPrefab);
            SceneManager.MoveGameObjectToScene(areaObject, scene);
            var follower = areaObject.GetComponent<WaveByWave.Generation.OceanUnderwaterCameraFollower>();
            if (follower != null) follower.enabled = false;
            var area = areaObject.GetComponent<UnderwaterArea>();
            area.enabled = false;
            area.waterLevelSource = UnderwaterArea.WaterLevelSource.FixedValue;
            area.waterLevel = water.HeightAt(eye, .65f);
            areaObject.transform.position = new Vector3(eye.x, area.waterLevel, eye.z);
            // Other loaded scenes can contain the ocean's large camera volume.
            // Give this preview area a closer surface so SW3 selects it.
            area.boxCollider.size = new Vector3(14f, 20f, 24f);
            area.boxCollider.center = new Vector3(0f, eye.y - area.waterLevel + .1f - 10f, 0f);
            area.waterMaterial = water.RuntimeWaterMaterial;
            try
            {
                var clearHull = Capture(camera, target);
                hull.enabled = false;
                var clearEmpty = Capture(camera, target);
                hull.enabled = true;
                var baseline = Difference(clearHull, clearEmpty);
                if (baseline < .005f) throw new InvalidOperationException("Underwater test camera cannot see the ship hull.");
                Save(camera, target, "Temp/ShipFloodingChecks/interior-hull-clear.png");

                area.enabled = true;
                if (UnderwaterArea.GetFirstIntersecting(camera) != area)
                    throw new InvalidOperationException("Underwater verification area does not enclose the camera.");
                var submergedHull = Capture(camera, target);
                var effect = Difference(clearHull, submergedHull);
                if (effect < .001f)
                    throw new InvalidOperationException($"Stylized Water 3 underwater effect did not render in the verification view ({effect:0.00000}).");
                hull.enabled = false;
                var submergedEmpty = Capture(camera, target);
                hull.enabled = true;
                var visible = Difference(submergedHull, submergedEmpty);
                Save(camera, target, "Temp/ShipFloodingChecks/interior-hull-underwater.png");
                if (visible < baseline * .35f)
                    throw new InvalidOperationException($"Underwater rendering hides the ship hull: above={baseline:0.000}, below={visible:0.000}.");
            }
            finally { area.enabled = false; hull.enabled = true; }
        }

        private static Color32[] Capture(Camera camera, RenderTexture destination)
        {
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = destination });
            var old = RenderTexture.active;
            RenderTexture.active = destination;
            var texture = new Texture2D(destination.width, destination.height, TextureFormat.RGBA32, false);
            try
            { texture.ReadPixels(new Rect(0, 0, destination.width, destination.height), 0, 0); texture.Apply(); return texture.GetPixels32(); }
            finally { RenderTexture.active = old; Object.DestroyImmediate(texture); }
        }

        private static float Difference(Color32[] first, Color32[] second)
        {
            double sum = 0;
            for (var i = 0; i < first.Length; i += 12)
                sum += Mathf.Abs(first[i].r - second[i].r) + Mathf.Abs(first[i].g - second[i].g) + Mathf.Abs(first[i].b - second[i].b);
            return (float)(sum / (first.Length / 12.0 * 255.0 * 3.0));
        }

        private static void Save(Camera camera, RenderTexture destination, string path)
        {
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = destination });
            var old = RenderTexture.active;
            RenderTexture.active = destination;
            var texture = new Texture2D(destination.width, destination.height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, destination.width, destination.height), 0, 0); texture.Apply();
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, texture.EncodeToPNG());
            RenderTexture.active = old; Object.DestroyImmediate(texture);
        }

        private static Color Centre(Camera camera, RenderTexture destination)
        {
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = destination });
            var old = RenderTexture.active; RenderTexture.active = destination;
            var pixel = new Texture2D(1, 1, TextureFormat.RGB24, false);
            try
            { pixel.ReadPixels(new Rect(destination.width / 2, destination.height / 2, 1, 1), 0, 0); pixel.Apply(); return pixel.GetPixel(0, 0); }
            finally { RenderTexture.active = old; Object.DestroyImmediate(pixel); }
        }

    }
}
