using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WaveByWave.Networking;
using WaveByWave.Ships;

namespace WaveByWave.Editor
{
    public static class ShipSpawnPointMigration
    {
        private const string ShipPrefabPath = "Assets/_Project/Prefabs/Ship.prefab";
        private const string OceanScenePath = "Assets/_Project/Scenes/Ocean.unity";

        [MenuItem("Tools/Wave by Wave/Upgrade Ship Spawn Points")]
        public static void UpgradeIfRequired()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            var prefabRoot = PrefabUtility.LoadPrefabContents(ShipPrefabPath);
            if (prefabRoot == null)
                return;

            var prefabChanged = false;
            try
            {
                var ship = prefabRoot.GetComponent<NetworkShipController>();
                if (ship == null)
                    return;

                var serializedShip = new SerializedObject(ship);
                var pointsProperty = serializedShip.FindProperty("playerSpawnPoints");
                var hasValidPoint = pointsProperty != null && Enumerable.Range(0, pointsProperty.arraySize)
                    .Any(index => pointsProperty.GetArrayElementAtIndex(index).objectReferenceValue != null);

                if (!hasValidPoint)
                {
                    var container = prefabRoot.transform.Find("Player Spawn Points");
                    if (container == null)
                    {
                        container = new GameObject("Player Spawn Points").transform;
                        container.SetParent(prefabRoot.transform, false);
                    }
                    container.localPosition = new Vector3(0f, 2.1f, 1.5f);
                    container.localRotation = Quaternion.identity;

                    var offsets = new[]
                    {
                        new Vector3(-1.5f, 0f, -1.5f), new Vector3(1.5f, 0f, -1.5f),
                        new Vector3(-1.5f, 0f, 1.5f), new Vector3(1.5f, 0f, 1.5f)
                    };
                    pointsProperty.arraySize = offsets.Length;
                    for (var i = 0; i < offsets.Length; i++)
                    {
                        var point = container.Find($"Spawn {i + 1}");
                        if (point == null)
                        {
                            point = new GameObject($"Spawn {i + 1}").transform;
                            point.SetParent(container, false);
                        }
                        point.localPosition = offsets[i];
                        point.localRotation = Quaternion.identity;
                        pointsProperty.GetArrayElementAtIndex(i).objectReferenceValue = point;
                    }

                    serializedShip.ApplyModifiedPropertiesWithoutUndo();
                    prefabChanged = true;
                }

                if (prefabChanged)
                    PrefabUtility.SaveAsPrefabAsset(prefabRoot, ShipPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }

            RemoveLegacyOceanSpawnOverrides();
            if (prefabChanged)
                Debug.Log("[Wave by Wave] Ship prefab upgraded with configurable player spawn points.");
        }

        private static void RemoveLegacyOceanSpawnOverrides()
        {
            var scene = SceneManager.GetSceneByPath(OceanScenePath);
            var wasLoaded = scene.IsValid() && scene.isLoaded;
            if (!wasLoaded)
                scene = EditorSceneManager.OpenScene(OceanScenePath, OpenSceneMode.Additive);

            var changed = false;
            foreach (var root in scene.GetRootGameObjects())
            {
                var director = root.GetComponent<NetworkSpawnDirector>();
                if (director != null)
                {
                    var serializedDirector = new SerializedObject(director);
                    var points = serializedDirector.FindProperty("spawnPoints");
                    if (points != null && points.arraySize > 0)
                    {
                        points.arraySize = 0;
                        serializedDirector.ApplyModifiedPropertiesWithoutUndo();
                        changed = true;
                    }
                }

                var ship = root.GetComponent<NetworkShipController>();
                if (ship == null)
                    continue;

                foreach (Transform child in ship.transform)
                {
                    if (child.name == "Player Spawn Points" && PrefabUtility.IsAddedGameObjectOverride(child.gameObject))
                    {
                        Object.DestroyImmediate(child.gameObject);
                        changed = true;
                        break;
                    }
                }
            }

            if (changed)
                EditorSceneManager.SaveScene(scene);
            if (!wasLoaded)
                EditorSceneManager.CloseScene(scene, true);
        }
    }
}
