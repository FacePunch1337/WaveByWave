using System.IO;
using UnityEditor;
using UnityEngine;
using WaveByWave.Player;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    [InitializeOnLoad]
    internal static class LadderContentSetup
    {
        private const string PrefabPath = "Assets/_Project/Prefabs/Interactables/Ladder.prefab";

        static LadderContentSetup() => EditorApplication.delayCall += EnsurePrefab;

        [MenuItem("Tools/Wave by Wave/Create Test Ladder Prefab")]
        public static void EnsurePrefab()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode ||
                AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
                return;

            var folder = Path.GetDirectoryName(PrefabPath)?.Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets/_Project/Prefabs", "Interactables");

            var root = new GameObject("Ladder", typeof(BoxCollider), typeof(ClimbableLadder));
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "Visual";
            visual.transform.SetParent(root.transform, false);
            Object.DestroyImmediate(visual.GetComponent<Collider>());

            var ladder = root.GetComponent<ClimbableLadder>();
            var serialized = new SerializedObject(ladder);
            serialized.FindProperty("visual").objectReferenceValue = visual.transform;
            serialized.FindProperty("interactionVolume").objectReferenceValue = root.GetComponent<BoxCollider>();
            serialized.ApplyModifiedPropertiesWithoutUndo();
            visual.transform.localPosition = Vector3.up * 2f;
            visual.transform.localScale = new Vector3(0.8f, 4f, 0.12f);
            var volume = root.GetComponent<BoxCollider>();
            volume.isTrigger = true;
            volume.center = new Vector3(0f, 2f, -0.275f);
            volume.size = new Vector3(1.6f, 4.6f, 1.57f);

            try
            {
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
