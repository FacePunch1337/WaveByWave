using System;
using UnityEditor;
using UnityEngine;
using WaveByWave.Ships;

namespace WaveByWave.Editor
{
    [CustomEditor(typeof(ShipHullHoles))]
    public sealed class ShipHullHolesEditor : UnityEditor.Editor
    {
        private Vector2 _dragStart;
        private bool _dragging;
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var hull = (ShipHullHoles)target;
            EditorGUILayout.HelpBox("Включи Show Allowed Regions: зелёным подсвечены разрешённые UV. Прямоугольники можно добавлять перетаскиванием на текстуре ниже. После изменения меша или областей обнови точки пробоин.", MessageType.Info);
            var rect = GUILayoutUtility.GetRect(200f, 230f);
            var material = hull.GetComponent<MeshRenderer>().sharedMaterial;
            var texture = material != null && material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : null;
            if (texture != null) EditorGUI.DrawPreviewTexture(rect, texture);
            else EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));
            foreach (var area in hull.AllowedUVRegions ?? Array.Empty<Rect>())
                EditorGUI.DrawRect(new Rect(rect.x + area.x * rect.width, rect.y + (1f - area.yMax) * rect.height,
                    area.width * rect.width, area.height * rect.height), hull.RegionColor);
            var e = Event.current;
            Vector2 UV(Vector2 point) => new(Mathf.Clamp01((point.x - rect.x) / rect.width), Mathf.Clamp01(1f - (point.y - rect.y) / rect.height));
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            { _dragging = true; _dragStart = UV(e.mousePosition); e.Use(); }
            if (_dragging && e.type == EventType.MouseDrag) { Repaint(); e.Use(); }
            if (_dragging && e.type == EventType.MouseUp)
            {
                _dragging = false; var end = UV(e.mousePosition);
                var area = Rect.MinMaxRect(Mathf.Min(_dragStart.x, end.x), Mathf.Min(_dragStart.y, end.y),
                    Mathf.Max(_dragStart.x, end.x), Mathf.Max(_dragStart.y, end.y));
                if (area.width > 0.001f && area.height > 0.001f && hull.AllowedUVRegions.Length < 8)
                {
                    Undo.RecordObject(hull, "Add hull UV region");
                    ArrayUtility.Add(ref hull.AllowedUVRegions, area); EditorUtility.SetDirty(hull);
                }
                e.Use();
            }
            EditorGUILayout.LabelField("Подготовленные точки", hull.Sites.Length.ToString());
            if (GUILayout.Button("Обновить точки пробоин"))
            { Undo.RecordObject(hull, "Rebuild hull breach sites"); ShipFloodingSetup.RebuildSites(hull); }
        }
    }

    [CustomEditor(typeof(ShipWaterVolume))]
    public sealed class ShipWaterVolumeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.HelpBox("Stylized Water Material — настоящий материал Stylized Water 3 с Wave Profile и настройками подводного эффекта. ShipWaterVolume создаёт поверхность и обрезает её по сечениям Ocean Cutout. Local Bounds задаёт уровни заполнения.", MessageType.Info);
            var current = (ShipWaterVolume)target;
            if (current.StylizedWaterMaterial == null || !current.StylizedWaterMaterial.HasProperty("_WaveProfile"))
                EditorGUILayout.HelpBox("Назначьте материал Stylized Water 3 в поле Stylized Water Material.", MessageType.Warning);
            if (!GUILayout.Button("Взять границы и контур из меша Water Cut")) return;
            var volume = (ShipWaterVolume)target;
            var filter = volume.OceanCutout != null ? volume.OceanCutout.GetComponent<MeshFilter>() : null;
            if (filter == null || filter.sharedMesh == null) return;
            Undo.RecordObject(volume, "Update ship water footprint");
            var vertices = filter.sharedMesh.vertices; var points = new Vector2[vertices.Length];
            var bounds = new Bounds();
            for (var i = 0; i < vertices.Length; i++)
            {
                var p = volume.transform.InverseTransformPoint(filter.transform.TransformPoint(vertices[i]));
                points[i] = new Vector2(p.x, p.z);
                if (i == 0) bounds = new Bounds(p, Vector3.zero); else bounds.Encapsulate(p);
            }
            volume.LocalBounds = bounds; volume.Footprint = ShipFloodingSetup.ConvexHull(points);
            EditorUtility.SetDirty(volume);
        }
    }
}
