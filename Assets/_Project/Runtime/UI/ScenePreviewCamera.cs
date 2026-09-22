using Unity.Netcode;
using UnityEngine;

namespace WaveByWave.UI
{
    [RequireComponent(typeof(Camera), typeof(AudioListener))]
    public sealed class ScenePreviewCamera : MonoBehaviour
    {
        private Camera _camera;
        private AudioListener _listener;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _listener = GetComponent<AudioListener>();
        }

        private void Update()
        {
            var manager = NetworkManager.Singleton;
            if (manager == null || manager.LocalClient?.PlayerObject == null)
                return;

            Disable();
        }

        public static void DisableAll()
        {
            foreach (var preview in FindObjectsByType<ScenePreviewCamera>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                preview.Disable();
        }

        private void Disable()
        {
            if (_camera != null)
                _camera.enabled = false;
            if (_listener != null)
                _listener.enabled = false;
        }
    }
}
