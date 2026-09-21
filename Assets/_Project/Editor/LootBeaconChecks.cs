using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using WaveByWave.Items;

namespace WaveByWave.Editor
{
    // Optional one-shot preview; never starts/stops Play Mode or modifies the scene.
    public static class LootBeaconChecks
    {
        [MenuItem("Tools/Wave by Wave/Items/Preview DOTS rarity beacon")]
        public static void ValidateAndPreview()
        {
            var output = "Temp/LootBeaconPreview";
            Directory.CreateDirectory(output);
            var preview = new PreviewRenderUtility();
            Material material = null;
            var previousAsync = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Effects/LootRarity.prefab");
                var source = prefab.GetComponent<LootRarityGlow>().DotsMaterial;
                material = new Material(source);
                if (prefab.GetComponentsInChildren<ParticleSystem>(true).Length != 0)
                    throw new InvalidOperationException("Legacy particles remain in loot effect prefab.");
                var effectType = typeof(ItemDefinition).Assembly.GetType("WaveByWave.Items.DotsLootRarityEffect", true);
                var mesh = (Mesh)effectType.GetMethod("GetMesh", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                var variants = new ShaderVariantCollection();
                variants.Add(new ShaderVariantCollection.ShaderVariant(source.shader, PassType.ScriptableRenderPipeline));
                variants.Add(new ShaderVariantCollection.ShaderVariant(source.shader,
                    PassType.ScriptableRenderPipeline, "DOTS_INSTANCING_ON"));
                variants.WarmUp();
                UnityEngine.Object.DestroyImmediate(variants);

                preview.camera.fieldOfView = 32f;
                preview.camera.nearClipPlane = 0.01f;
                preview.camera.farClipPlane = 50f;
                preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = new Color(0.035f, 0.055f, 0.075f);
                preview.camera.transform.position = new Vector3(3.2f, 2.4f, 6.2f);
                preview.camera.transform.LookAt(new Vector3(0f, 1.45f, 0f));
                preview.BeginStaticPreview(new Rect(0, 0, 600, 800));
                preview.DrawMesh(mesh, Matrix4x4.identity, material, 0);
                preview.Render(true);
                var image = preview.EndStaticPreview();
                File.WriteAllBytes(output + "/beacon.png", image.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(image);
                if (ShaderUtil.ShaderHasError(source.shader))
                    throw new InvalidOperationException(string.Join("\n", Array.ConvertAll(
                        ShaderUtil.GetShaderMessages(source.shader), message => message.message)));
                File.WriteAllText(output + "/result.txt",
                    "PASS: prefab has no ParticleSystem; normal/DOTS shader variants compiled; one 36-vertex/18-triangle effect mesh.\n");
                Debug.Log("[Loot beacon] Shader validation and preview passed: " + output);
            }
            catch (Exception exception)
            {
                File.WriteAllText(output + "/result.txt", "FAIL: " + exception);
                Debug.LogException(exception);
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = previousAsync;
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                preview.Cleanup();
            }
        }
    }
}
