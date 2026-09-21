using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WaveByWave.Enemies;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    public static class EnemyDiagnostics
    {
        [MenuItem("Tools/Wave by Wave/Enemies/Validate and render animation contact sheet")]
        public static void ValidateContent()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(EnemyContentSetup.CatalogPath);
            if (catalog == null || !catalog.IsBaked) throw new InvalidOperationException("Bake the catalog first.");
            foreach (var part in catalog.BakedParts)
            {
                if (part.Mesh == null || part.Positions == null || part.Normals == null || part.Materials.Any(m => m == null))
                    throw new InvalidOperationException("Incomplete baked part: " + part.Name);
                if (part.Clips.Length != 5 || part.Mesh.bounds.size.magnitude > 15 || part.Mesh.bounds.size.magnitude < 0.001f)
                    throw new InvalidOperationException("Invalid animation/bounds: " + part.Name + " " + part.Mesh.bounds);
                foreach (var material in part.Materials)
                    if (ShaderUtil.ShaderHasError(material.shader))
                        throw new InvalidOperationException("Enemy shader compilation failed.");
            }
            if (!EnemyHitGeometry.SegmentCapsule(new Vector3(0, 0.7f, -2), new Vector3(0, 0.7f, 2),
                    new Vector3(0, 0.3f, 0), new Vector3(0, 1.4f, 0), 0.3f, out var t) || Mathf.Abs(t - 0.425f) > 0.001f ||
                EnemyHitGeometry.SegmentCapsule(new Vector3(2, 0.7f, -2), new Vector3(2, 0.7f, 2),
                    new Vector3(0, 0.3f, 0), new Vector3(0, 1.4f, 0), 0.3f, out _))
                throw new InvalidOperationException("Enemy capsule hit regression failed.");
            var selected = new List<int>();
            var sheet = new Texture2D(1200, 900, TextureFormat.RGB24, false);
            var preview = new PreviewRenderUtility();
            try
            {
                preview.camera.fieldOfView = 32;
                preview.camera.nearClipPlane = 0.01f;
                preview.camera.farClipPlane = 30;
                preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = new Color(0.1f, 0.14f, 0.18f);
                preview.camera.transform.position = new Vector3(2.4f, 1.8f, 5.1f);
                preview.camera.transform.LookAt(new Vector3(0, 0.95f, 0));
                preview.lights[0].intensity = 1.4f;
                preview.lights[0].transform.rotation = Quaternion.Euler(35, 150, 0);
                preview.lights[1].intensity = 0.7f;
                preview.ambientColor = Color.gray;
                for (var cell = 0; cell < 12; cell++)
                {
                    var type = (EnemyCombatType)(cell % 3);
                    DotsEnemyPresentation.SelectParts((uint)(cell % 3 + 71), type, catalog, selected);
                    var clipIndex = cell < 3 ? 0 : cell < 6 ? 1 : (int)catalog.AttackAnimation(type);
                    var phase = cell < 9 ? 0.5f : 0.8f;
                    var materials = new List<Material>();
                    preview.BeginStaticPreview(new Rect(0, 0, 300, 300));
                    foreach (var index in selected)
                    {
                        var part = catalog.BakedParts[index];
                        var frame = part.Clips[clipIndex];
                        foreach (var mat in part.Materials.Select((value, submesh) => (value, submesh)))
                        {
                            var material = new Material(mat.value);
                            material.SetVector("_EnemyFrame", new Vector4(frame.FirstRow + Mathf.RoundToInt(phase * (frame.Count - 1)),
                                frame.FirstRow, 0, 0));
                            materials.Add(material);
                            preview.DrawMesh(part.Mesh, Matrix4x4.Scale(Vector3.one * catalog.VisualScale), material, mat.submesh);
                        }
                    }
                    preview.Render(true);
                    var image = preview.EndStaticPreview();
                    sheet.SetPixels((cell % 4) * 300, (2 - cell / 4) * 300, 300, 300, image.GetPixels());
                    Object.DestroyImmediate(image);
                    foreach (var material in materials) Object.DestroyImmediate(material);
                }
                sheet.Apply();
                Directory.CreateDirectory("Temp/EnemyDiagnostics");
                File.WriteAllBytes("Temp/EnemyDiagnostics/animations.png", sheet.EncodeToPNG());
                File.WriteAllText("Temp/EnemyDiagnostics/content.txt", $"PASS: {catalog.BakedParts.Count} parts, five clips, shader and capsule tests.\n" +
                    string.Join("\n", catalog.BakedParts.Select(p => $"{p.Name}: {p.Mesh.vertexCount} vertices; {p.Mesh.bounds}")));
                Debug.Log("[Enemies] Content/capsule validation passed. Contact sheet: Temp/EnemyDiagnostics/animations.png");
                var rig = Object.Instantiate(catalog.BakingRigPrefab);
                try
                {
                    foreach (var animator in rig.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(animator);
                    var bones = rig.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
                    var poses = new System.Text.StringBuilder();
                    for (var i = 3; i <= 4; i++)
                    {
                        var clip = catalog.Clip((EnemyAnimationState)i);
                        new EnemyContentSetup.ClipSampler(clip, bones).Sample(clip.length * 0.15f);
                        var hand = bones["CATRHand"];
                        poses.AppendLine($"Clip {clip.name}: hand={hand.position} right={hand.right} up={hand.up} forward={hand.forward}, left hand={bones["CATLHand"].position}");
                    }
                    foreach (var source in catalog.PartSources.Where(s => s.Category is EnemyBakedPartCategory.MeleeWeapon or EnemyBakedPartCategory.PistolWeapon or EnemyBakedPartCategory.RifleWeapon))
                        poses.AppendLine($"{source.Prefab.name}: {source.Prefab.GetComponentInChildren<MeshFilter>().sharedMesh.bounds}");
                    File.WriteAllText("Temp/EnemyDiagnostics/weapon-axes.txt", poses.ToString());
                }
                finally { Object.DestroyImmediate(rig); }
            }
            finally { preview.Cleanup(); Object.DestroyImmediate(sheet); }
        }
    }
}
