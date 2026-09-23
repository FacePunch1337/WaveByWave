using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WaveByWave.Generation;

namespace WaveByWave.Editor
{
    [CustomEditor(typeof(VoyageMapSettings))]
    public sealed class VoyageMapSettingsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            if (!EditorGUI.EndChangeCheck()) return;
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
    }

    // Preview materials are never written into a scene or carried into Play Mode.
    [InitializeOnLoad]
    internal static class VoyageDayNightPreviewGuard
    {
        static VoyageDayNightPreviewGuard()
        {
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnSceneSaving(Scene scene, string path)
        {
            foreach (var controller in Object.FindObjectsByType<VoyageDayNightController>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (controller.gameObject.scene == scene) controller.RestoreEditorPreview();
            EditorApplication.delayCall += EditorApplication.QueuePlayerLoopUpdate;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode) return;
            foreach (var controller in Object.FindObjectsByType<VoyageDayNightController>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                controller.RestoreEditorPreview();
        }
    }
}
