using StylizedWater3;
using StylizedWater3.UnderwaterRendering;
using UnityEngine;

namespace WaveByWave.Generation
{
    [DefaultExecutionOrder(10000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BoxCollider), typeof(UnderwaterArea))]
    public sealed class OceanUnderwaterCameraFollower : MonoBehaviour
    {
        [SerializeField] private UnderwaterArea underwaterArea;
        [SerializeField] private BoxCollider underwaterVolume;
        [SerializeField, Min(100f)] private float horizontalSize = 2000f;
        [SerializeField, Min(20f)] private float depth = 500f;

        private Camera _playerCamera;

        private void Awake() => ConfigureVolume();
        private void OnValidate() => ConfigureVolume();

        private void ConfigureVolume()
        {
            underwaterArea ??= GetComponent<UnderwaterArea>();
            underwaterVolume ??= GetComponent<BoxCollider>();
            if (underwaterVolume != null)
            {
                underwaterVolume.isTrigger = true;
                underwaterVolume.size = new Vector3(horizontalSize, depth, horizontalSize);
                underwaterVolume.center = new Vector3(0f, -depth * 0.5f + 3f, 0f);
            }
            if (underwaterArea != null)
            {
                underwaterArea.boxCollider = underwaterVolume;
                underwaterArea.waterLevelSource = UnderwaterArea.WaterLevelSource.Ocean;
                if (OceanFollowBehaviour.Instance != null)
                    underwaterArea.waterMaterial = OceanFollowBehaviour.Instance.material;
            }
        }

        private void LateUpdate()
        {
            if (_playerCamera == null || !_playerCamera.isActiveAndEnabled ||
                !_playerCamera.CompareTag("MainCamera"))
                _playerCamera = Camera.main;

            if (_playerCamera == null)
                return;

            if (OceanFollowBehaviour.Instance != null && underwaterArea != null)
                underwaterArea.waterMaterial = OceanFollowBehaviour.Instance.material;

            var position = transform.position;
            position.x = _playerCamera.transform.position.x;
            position.z = _playerCamera.transform.position.z;
            if (OceanFollowBehaviour.Instance != null)
                position.y = OceanFollowBehaviour.Instance.transform.position.y;
            transform.position = position;
        }
    }
}
