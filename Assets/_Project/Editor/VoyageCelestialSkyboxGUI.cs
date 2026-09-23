using UnityEditor;
using UnityEngine;

namespace WaveByWave.Editor
{
    // The material serializes the sprite's atlas texture and UV rect; no scene object is needed.
    public sealed class VoyageCelestialSkyboxGUI : ShaderGUI
    {
        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            materialEditor.PropertiesDefaultGUI(properties);
            var material = materialEditor.target as Material;
            if (material == null) return;

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Sprite assets (optional)", EditorStyles.boldLabel);
            DrawSprite(materialEditor, material, "Sun Sprite", "_SunSprite", "_SunSpriteRect", "_UseSunSprite");
            DrawSprite(materialEditor, material, "Moon Sprite", "_MoonSprite", "_MoonSpriteRect", "_UseMoonSprite");
            EditorGUILayout.HelpBox("Without a sprite, each enabled celestial body is drawn as a procedural disc. " +
                "This material also works when it is the only skybox assigned in Lighting settings.", MessageType.Info);
        }

        private static void DrawSprite(MaterialEditor editor, Material material, string label,
            string textureProperty, string rectProperty, string enabledProperty)
        {
            var current = FindSprite(material, textureProperty, rectProperty);
            EditorGUI.BeginChangeCheck();
            var selected = (Sprite)EditorGUILayout.ObjectField(label, current, typeof(Sprite), false);
            if (!EditorGUI.EndChangeCheck()) return;
            if (selected != null && selected.packed && selected.packingMode == SpritePackingMode.Tight)
            {
                EditorUtility.DisplayDialog("Skybox sprite", "A tightly packed sprite has no rectangular UV area. Use an unpacked or rectangle-packed sprite.", "OK");
                return;
            }
            Undo.RecordObjects(editor.targets, "Change skybox celestial sprite");
            foreach (var target in editor.targets)
            {
                if (target is not Material skybox) continue;
                skybox.SetTexture(textureProperty, selected != null ? selected.texture : null);
                var rect = selected != null ? selected.textureRect : default;
                skybox.SetVector(rectProperty, selected != null
                    ? new Vector4(rect.x / selected.texture.width, rect.y / selected.texture.height,
                        rect.width / selected.texture.width, rect.height / selected.texture.height)
                    : new Vector4(0f, 0f, 1f, 1f));
                skybox.SetFloat(enabledProperty, selected != null ? 1f : 0f);
                EditorUtility.SetDirty(skybox);
            }
        }

        private static Sprite FindSprite(Material material, string textureProperty, string rectProperty)
        {
            if (material.GetFloat(textureProperty == "_SunSprite" ? "_UseSunSprite" : "_UseMoonSprite") < 0.5f)
                return null;
            var texture = material.GetTexture(textureProperty);
            if (texture == null) return null;
            var path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path)) return null;
            var rect = material.GetVector(rectProperty);
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is not Sprite sprite || sprite.texture != texture) continue;
                if (sprite.packed && sprite.packingMode == SpritePackingMode.Tight) continue;
                var spriteRect = sprite.textureRect;
                if (Mathf.Abs(spriteRect.x / texture.width - rect.x) < 0.0001f &&
                    Mathf.Abs(spriteRect.y / texture.height - rect.y) < 0.0001f &&
                    Mathf.Abs(spriteRect.width / texture.width - rect.z) < 0.0001f &&
                    Mathf.Abs(spriteRect.height / texture.height - rect.w) < 0.0001f)
                    return sprite;
            }
            return null;
        }
    }
}
