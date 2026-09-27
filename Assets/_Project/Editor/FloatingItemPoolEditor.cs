using System;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using WaveByWave.Generation;
using WaveByWave.Items;

namespace WaveByWave.Editor
{
    [CustomEditor(typeof(FloatingItemPool))]
    public sealed class FloatingItemPoolEditor : UnityEditor.Editor
    {
        private SerializedProperty _items;
        private ReorderableList _list;
        private GUIStyle _nameStyle;
        private GUIStyle _rarityStyle;

        private void OnEnable()
        {
            _items = serializedObject.FindProperty("items");
            _list = new ReorderableList(serializedObject, _items, true, true, true, true)
            {
                elementHeight = 50f,
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, $"Floating Items ({_items.arraySize})"),
                drawElementCallback = DrawElement,
                onAddCallback = AddElement
            };
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox(
                "Ocean Generation Settings controls the days on which rarities unlock. This asset controls which floating items exist and their relative weights.",
                MessageType.Info);
            _list.DoLayoutList();
            serializedObject.ApplyModifiedProperties();

            var entries = ((FloatingItemPool)target).Items;
            var nullCount = entries.Count(entry => entry == null || entry.Item == null);
            var duplicateCount = entries.Where(entry => entry?.Item != null)
                .GroupBy(entry => entry.Item).Count(group => group.Count() > 1);
            if (nullCount > 0 || duplicateCount > 0)
                EditorGUILayout.HelpBox($"Empty entries: {nullCount}. Duplicate items: {duplicateCount}.", MessageType.Warning);
        }

        private void DrawElement(Rect rect, int index, bool active, bool focused)
        {
            EnsureStyles();
            var element = _items.GetArrayElementAtIndex(index);
            var itemProperty = element.FindPropertyRelative("Item");
            var weightProperty = element.FindPropertyRelative("Weight");
            var item = itemProperty.objectReferenceValue as ItemDefinition;
            rect.y += 3f;

            var iconRect = new Rect(rect.x, rect.y, 42f, 42f);
            EditorGUI.DrawRect(iconRect, new Color(0.08f, 0.08f, 0.08f, 0.55f));
            if (item != null && item.Icon != null) DrawSprite(iconRect, item.Icon);
            else EditorGUI.LabelField(iconRect, "—", EditorStyles.centeredGreyMiniLabel);

            var contentX = iconRect.xMax + 7f;
            var weightWidth = 92f;
            var contentWidth = Mathf.Max(80f, rect.xMax - contentX);
            var titleRect = new Rect(contentX, rect.y, contentWidth, 19f);
            var itemRect = new Rect(contentX, rect.y + 22f, Mathf.Max(45f, contentWidth - weightWidth - 5f), 19f);
            var weightRect = new Rect(itemRect.xMax + 5f, itemRect.y, weightWidth, 19f);

            if (item != null)
            {
                _nameStyle.normal.textColor = item.RarityColor;
                _rarityStyle.normal.textColor = item.RarityColor;
                EditorGUI.LabelField(titleRect, item.DisplayName, _nameStyle);
                var rarityWidth = Mathf.Min(92f, _rarityStyle.CalcSize(new GUIContent(item.Rarity.ToString())).x + 8f);
                var rarityRect = new Rect(titleRect.xMax - rarityWidth, titleRect.y, rarityWidth, titleRect.height);
                EditorGUI.DrawRect(rarityRect, new Color(0f, 0f, 0f, 0.4f));
                EditorGUI.LabelField(rarityRect, item.Rarity.ToString(), _rarityStyle);
            }
            else
            {
                EditorGUI.LabelField(titleRect, $"Empty item {index}", EditorStyles.boldLabel);
            }

            EditorGUI.PropertyField(itemRect, itemProperty, GUIContent.none);
            var weightLabelRect = new Rect(weightRect.x, weightRect.y, 42f, weightRect.height);
            var weightValueRect = new Rect(weightLabelRect.xMax, weightRect.y,
                Mathf.Max(30f, weightRect.xMax - weightLabelRect.xMax), weightRect.height);
            EditorGUI.LabelField(weightLabelRect, "Weight");
            EditorGUI.PropertyField(weightValueRect, weightProperty, GUIContent.none);
        }

        private void AddElement(ReorderableList list)
        {
            var index = _items.arraySize;
            _items.InsertArrayElementAtIndex(index);
            var element = _items.GetArrayElementAtIndex(index);
            element.FindPropertyRelative("Item").objectReferenceValue = null;
            element.FindPropertyRelative("Weight").floatValue = 1f;
            list.index = index;
        }

        private void EnsureStyles()
        {
            _nameStyle ??= new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                clipping = TextClipping.Clip,
                padding = new RectOffset(1, 96, 0, 0)
            };
            _rarityStyle ??= new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Clip
            };
        }

        private static void DrawSprite(Rect rect, Sprite sprite)
        {
            var texture = sprite.texture;
            if (texture == null) return;
            var source = sprite.rect;
            var uv = new Rect(source.x / texture.width, source.y / texture.height,
                source.width / texture.width, source.height / texture.height);
            var fitted = Fit(rect, source.size);
            GUI.DrawTextureWithTexCoords(fitted, texture, uv, true);
        }

        private static Rect Fit(Rect bounds, Vector2 size)
        {
            if (size.x <= 0f || size.y <= 0f) return bounds;
            var scale = Mathf.Min(bounds.width / size.x, bounds.height / size.y);
            var width = size.x * scale;
            var height = size.y * scale;
            return new Rect(bounds.x + (bounds.width - width) * 0.5f,
                bounds.y + (bounds.height - height) * 0.5f, width, height);
        }
    }
}
