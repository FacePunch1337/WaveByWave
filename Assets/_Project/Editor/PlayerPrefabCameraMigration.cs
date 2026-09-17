using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using WaveByWave.Player;

namespace WaveByWave.Editor
{
    /// <summary>
    /// Keeps the existing generated player prefab aligned with the current first-person layout
    /// without requiring the user to regenerate scenes or unrelated assets.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayerPrefabCameraMigration
    {
        private const string PlayerPrefabPath = "Assets/_Project/Prefabs/Player.prefab";

        static PlayerPrefabCameraMigration() => EditorApplication.delayCall += UpgradeIfRequired;

        [MenuItem("Tools/Wave by Wave/Upgrade Player Camera Prefab")]
        public static void UpgradeIfRequired()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            var prefabRoot = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
            if (prefabRoot == null)
                return;

            var changed = false;
            try
            {
                var controller = prefabRoot.GetComponent<NetworkPlayerController>();
                if (controller == null)
                    return;

                var holder = prefabRoot.transform.Find("Camera Holder") ?? prefabRoot.transform.Find("CameraTarget");
                if (holder == null)
                {
                    holder = new GameObject("Camera Holder").transform;
                    holder.SetParent(prefabRoot.transform, false);
                    holder.localPosition = new Vector3(0f, 1.65f, 0f);
                    changed = true;
                }

                if (holder.name != "Camera Holder")
                {
                    holder.name = "Camera Holder";
                    changed = true;
                }

                if (!holder.CompareTag("MainCamera"))
                {
                    holder.gameObject.tag = "MainCamera";
                    changed = true;
                }

                var view = holder.GetComponent<Camera>();
                if (view == null)
                {
                    view = holder.gameObject.AddComponent<Camera>();
                    changed = true;
                }
                view.fieldOfView = 75f;
                view.nearClipPlane = 0.03f;

                if (holder.GetComponent<AudioListener>() == null)
                {
                    holder.gameObject.AddComponent<AudioListener>();
                    changed = true;
                }
                if (holder.GetComponent<UniversalAdditionalCameraData>() == null)
                {
                    holder.gameObject.AddComponent<UniversalAdditionalCameraData>();
                    changed = true;
                }

                var firstPersonCamera = holder.GetComponent<FirstPersonCamera>();
                if (firstPersonCamera == null)
                {
                    firstPersonCamera = holder.gameObject.AddComponent<FirstPersonCamera>();
                    changed = true;
                }

                changed |= SetReference(controller, "cameraTarget", holder);
                changed |= SetReference(controller, "ownerCamera", firstPersonCamera);
                changed |= SetReference(controller, "firstPersonHiddenRoot", prefabRoot.transform.Find("Visual"));
                changed |= SetReference(firstPersonCamera, "eyeTarget", holder);
                changed |= SetReference(firstPersonCamera, "characterBody", prefabRoot.transform);

                var localBodyLayer = LayerMask.NameToLayer(NetworkPlayerController.LocalBodyLayerName);
                if (localBodyLayer >= 0)
                {
                    var ownerCullingMask = view.cullingMask & ~(1 << localBodyLayer);
                    if (view.cullingMask != ownerCullingMask)
                    {
                        view.cullingMask = ownerCullingMask;
                        changed = true;
                    }
                }

                if (holder.gameObject.activeSelf)
                {
                    holder.gameObject.SetActive(false);
                    changed = true;
                }

                if (changed)
                {
                    PrefabUtility.SaveAsPrefabAsset(prefabRoot, PlayerPrefabPath);
                    Debug.Log("[Wave by Wave] Player prefab upgraded with an embedded owner Camera Holder.");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        private static bool SetReference(Object target, string propertyName, Object value)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(propertyName);
            if (property == null || property.objectReferenceValue == value)
                return false;

            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }
    }
}
