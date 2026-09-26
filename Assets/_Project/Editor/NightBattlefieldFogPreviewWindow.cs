using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using WaveByWave.Generation;
using WaveByWave.Ships;

namespace WaveByWave.Editor
{
    [InitializeOnLoad]
    public sealed class NightBattlefieldFogPreviewWindow : EditorWindow
    {
        private const string SettingsPath = "Assets/_Project/Resources/NightBattlefieldSettings.asset";
        private const string WavesPath = "Assets/_Project/Resources/NightWaveSettings.asset";
        private static NightBattlefieldSettings _settings;
        private static NightWaveSettings _waves;
        private static double _nextRepaint;

        static NightBattlefieldFogPreviewWindow()
        {
            SceneView.duringSceneGui += DrawBattlefield;
            EditorApplication.update += RepaintAnimatedFog;
        }

        [MenuItem("Tools/Wave by Wave/Voyage/Night battlefield fog preview")]
        private static void Open() => GetWindow<NightBattlefieldFogPreviewWindow>("Night fog preview");

        private static NightBattlefieldSettings Settings => _settings ??=
            AssetDatabase.LoadAssetAtPath<NightBattlefieldSettings>(SettingsPath);
        private static NightWaveSettings Waves => _waves ??=
            AssetDatabase.LoadAssetAtPath<NightWaveSettings>(WavesPath);

        private void OnGUI()
        {
            var settings = Settings;
            var waves = Waves;
            if (settings == null || waves == null)
            {
                EditorGUILayout.HelpBox("NightBattlefieldSettings.asset or NightWaveSettings.asset was not found.", MessageType.Error);
                return;
            }
            EditorGUILayout.HelpBox("Preview renders in Scene View without Play Mode. It does not start a wave or damage the ship.",
                MessageType.Info);
            if (Application.isPlaying && NightBattlefieldController.TryGetBattlefield(out var liveCircle, out _))
            {
                var ship = FindFirstObjectByType<ShipCannonBattery>();
                var distance = ship == null ? 0f : Vector2.Distance(
                    new Vector2(ship.transform.position.x, ship.transform.position.z),
                    new Vector2(liveCircle.x, liveCircle.z));
                EditorGUILayout.HelpBox($"Live wave: fixed center ({liveCircle.x:0.0}, {liveCircle.z:0.0}), " +
                    $"radius {liveCircle.w:0.0} m, ship distance {distance:0.0} m. " +
                    (distance <= liveCircle.w ? "Inside" : "Outside"), MessageType.Info);
            }
            if (!HasFogFeature("Assets/Settings/PC_Renderer.asset") ||
                !HasFogFeature("Assets/Settings/Mobile_Renderer.asset"))
                EditorGUILayout.HelpBox("Night battlefield fog render feature is missing from a URP renderer.",
                    MessageType.Warning);
            var enabled = EditorGUILayout.Toggle("Show fog in Scene View", settings.PreviewFogInSceneView);
            if (enabled != settings.PreviewFogInSceneView)
            {
                Undo.RecordObject(settings, "Toggle night battlefield fog preview");
                if (enabled && !settings.PreviewFogInSceneView)
                {
                    var ship = FindFirstObjectByType<ShipCannonBattery>();
                    if (ship != null) settings.PreviewCenter = ship.transform.position;
                }
                settings.PreviewFogInSceneView = enabled;
                Changed(settings);
            }
            if (waves.Waves == null || waves.Waves.Length == 0)
            {
                EditorGUILayout.HelpBox("Add at least one night wave to preview its radius.", MessageType.Warning);
                return;
            }
            var names = new string[waves.Waves.Length];
            for (var i = 0; i < names.Length; i++)
                names[i] = $"{i + 1}: {waves.Waves[i]?.Name ?? "Wave"}";
            var index = Mathf.Clamp(settings.PreviewWaveIndex, 0, names.Length - 1);
            var selected = EditorGUILayout.Popup("Wave", index, names);
            if (selected != settings.PreviewWaveIndex)
            {
                Undo.RecordObject(settings, "Choose fog preview wave");
                settings.PreviewWaveIndex = selected;
                Changed(settings);
            }
            var wave = waves.Waves[selected];
            if (wave != null)
            {
                var radius = Mathf.Max(20f, EditorGUILayout.FloatField("Battlefield radius", wave.BattlefieldRadius));
                if (!Mathf.Approximately(radius, wave.BattlefieldRadius))
                {
                    Undo.RecordObject(waves, "Change battlefield radius");
                    wave.BattlefieldRadius = radius;
                    Changed(waves);
                }
            }
            var center = EditorGUILayout.Vector3Field("Preview center", settings.PreviewCenter);
            if (center != settings.PreviewCenter)
            {
                Undo.RecordObject(settings, "Move fog preview center");
                settings.PreviewCenter = center;
                Changed(settings);
            }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Center on selection") && Selection.activeTransform != null)
                SetCenter(settings, Selection.activeTransform.position);
            if (GUILayout.Button("Center on ship"))
            {
                var ship = FindFirstObjectByType<ShipCannonBattery>();
                if (ship != null) SetCenter(settings, ship.transform.position);
                else Debug.LogWarning("No ship with ShipCannonBattery in the open scene.");
            }
            EditorGUILayout.EndHorizontal();
            if (GUILayout.Button("View fog boundary") && wave != null && SceneView.lastActiveSceneView != null)
            {
                var pivot = settings.PreviewCenter + Vector3.right * (wave.BattlefieldRadius - 12f) + Vector3.up * 6f;
                SceneView.lastActiveSceneView.LookAt(pivot, Quaternion.LookRotation(Vector3.right, Vector3.up), 25f, false);
            }
            if (GUILayout.Button("Open all fog settings")) Selection.activeObject = settings;
        }

        private static void SetCenter(NightBattlefieldSettings settings, Vector3 center)
        {
            Undo.RecordObject(settings, "Move fog preview center");
            settings.PreviewCenter = center;
            Changed(settings);
        }

        private static bool HasFogFeature(string rendererPath)
        {
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (renderer == null) return false;
            foreach (var feature in renderer.rendererFeatures)
                if (feature is NightBattlefieldFogFeature && feature.isActive) return true;
            return false;
        }

        private static void Changed(Object settings)
        {
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            SceneView.RepaintAll();
        }

        private static void RepaintAnimatedFog()
        {
            var settings = Settings;
            if (Application.isPlaying || settings == null || !settings.PreviewFogInSceneView ||
                EditorApplication.timeSinceStartup < _nextRepaint) return;
            _nextRepaint = EditorApplication.timeSinceStartup + 1.0 / 15.0;
            SceneView.RepaintAll();
        }

        private static void DrawBattlefield(SceneView sceneView)
        {
            Vector3 center;
            float radius;
            if (Application.isPlaying)
            {
                if (!NightBattlefieldController.TryGetBattlefield(out var liveCircle, out _)) return;
                center = new Vector3(liveCircle.x, liveCircle.y, liveCircle.z);
                radius = liveCircle.w;
            }
            else
            {
                var settings = Settings;
                if (settings == null || !settings.PreviewFogInSceneView ||
                    Waves == null || Waves.Waves == null || Waves.Waves.Length == 0) return;
                var wave = Waves.Waves[Mathf.Clamp(settings.PreviewWaveIndex, 0, Waves.Waves.Length - 1)];
                if (wave == null) return;
                center = settings.PreviewCenter;
                radius = wave.BattlefieldRadius;
            }
            var previous = Handles.color;
            Handles.color = new Color(0.25f, 0.95f, 0.92f, 0.85f);
            Handles.DrawWireDisc(center, Vector3.up, radius, 2f);
            Handles.Label(center + Vector3.up * 2f,
                $"Fixed battlefield center · radius {radius:0} m");
            Handles.color = previous;
        }
    }
}
