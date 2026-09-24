using StylizedWater3;
using StylizedWater3.UnderwaterRendering;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

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
        [SerializeField, Min(3f)] private float surfacePadding = 4f;
        [Header("Stylized Water 3 particles")]
        [SerializeField] private GameObject lightShaftsPrefab;
        [SerializeField] private GameObject planktonPrefab;
        [SerializeField] private GameObject bubblesPrefab;

        private Camera _playerCamera;
        private readonly List<ParticleSystem> _spawnedParticles = new();

        private void Awake()
        {
            ConfigureVolume();
            EnsureParticleEffects();
        }

        private void OnEnable()
        {
            ConfigureVolume();
            EnsureParticleEffects();
            RenderPipelineManager.beginCameraRendering += HandleBeginCameraRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= HandleBeginCameraRendering;
        }

        private void OnValidate() => ConfigureVolume();

        private void ConfigureVolume()
        {
            underwaterArea ??= GetComponent<UnderwaterArea>();
            underwaterVolume ??= GetComponent<BoxCollider>();
            if (underwaterVolume != null)
            {
                underwaterVolume.isTrigger = true;
                underwaterVolume.size = new Vector3(horizontalSize, depth, horizontalSize);
                underwaterVolume.center = new Vector3(0f, -depth * 0.5f + surfacePadding, 0f);
            }
            if (underwaterArea != null)
            {
                underwaterArea.boxCollider = underwaterVolume;
                underwaterArea.waterLevelSource = UnderwaterArea.WaterLevelSource.Ocean;
                BindOceanMaterial();
            }
        }

        private void LateUpdate()
        {
            if (_playerCamera == null || !_playerCamera.isActiveAndEnabled ||
                !_playerCamera.CompareTag("MainCamera"))
                _playerCamera = Camera.main;

            if (_playerCamera == null)
                return;

            FollowCamera(_playerCamera);
        }

        private void HandleBeginCameraRendering(ScriptableRenderContext _, Camera camera)
        {
            if (!Application.isPlaying || camera == null || camera.cameraType != CameraType.Game ||
                !camera.isActiveAndEnabled)
                return;

            // This callback runs before renderer features inspect UnderwaterArea. It therefore
            // remains correct when NGO activates the owner camera after the scene has loaded or
            // when a dedicated customization camera temporarily replaces the first-person view.
            _playerCamera = camera;
            FollowCamera(camera);
        }

        private void FollowCamera(Camera camera)
        {
            var compartment = WaveByWave.Ships.ShipFlooding.CompartmentAt(camera.transform.position);
            BindWaterMaterial(compartment);
            if (underwaterArea != null)
            {
                var insideWater = compartment != null && compartment.WaterLitres > 0.001f &&
                    camera.transform.position.y < compartment.WaterVolume.HeightAt(camera.transform.position, compartment.Fill);
                // A dry masked cabin may be below sea level. Ocean fog must not
                // make it look flooded before water has actually reached the camera.
                var active = compartment == null || insideWater;
                if (underwaterArea.enabled != active)
                {
                    underwaterArea.enabled = active;
                    if (!active) foreach (var particles in _spawnedParticles)
                        if (particles != null) particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
                underwaterArea.waterLevelSource = compartment != null ? UnderwaterArea.WaterLevelSource.FixedValue : UnderwaterArea.WaterLevelSource.Ocean;
            }

            var position = transform.position;
            position.x = camera.transform.position.x;
            position.z = camera.transform.position.z;
            if (OceanFollowBehaviour.Instance != null)
            {
                position.y = OceanFollowBehaviour.Instance.transform.position.y;
                if (underwaterArea != null)
                    underwaterArea.waterLevel = position.y;
            }
            if (compartment != null)
            {
                position.y = compartment.WaterVolume.HeightAt(camera.transform.position, compartment.Fill);
                if (underwaterArea != null) underwaterArea.waterLevel = position.y;
            }
            transform.position = position;
        }

        private void BindOceanMaterial()
        {
            if (underwaterArea == null || OceanFollowBehaviour.Instance == null ||
                OceanFollowBehaviour.Instance.material == null)
                return;

            var material = OceanFollowBehaviour.Instance.material;
            underwaterArea.waterMaterial = material;

            // Stylized Water 3 requires the water material to be double-sided for the
            // underwater mask and waterline to be visible from below the surface.
            if (Application.isPlaying && material.HasProperty("_Cull") &&
                material.GetInt("_Cull") != (int)CullMode.Off)
                material.SetInt("_Cull", (int)CullMode.Off);
        }

        private void BindWaterMaterial(WaveByWave.Ships.ShipFlooding compartment)
        {
            if (underwaterArea == null) return;
            var interior = compartment?.WaterVolume?.RuntimeWaterMaterial;
            if (interior != null) underwaterArea.waterMaterial = interior;
            else BindOceanMaterial();
        }

        private void EnsureParticleEffects()
        {
            if (!Application.isPlaying || underwaterArea == null || _spawnedParticles.Count > 0)
                return;

            underwaterArea.particleEffects ??= new List<UnderwaterArea.ParticleEffect>();
            AddParticleEffect(lightShaftsPrefab, true, 0f, 1f);
            AddParticleEffect(planktonPrefab, false, 6f, 50f);
            AddParticleEffect(bubblesPrefab, false, 0f, 2f);
        }

        private void AddParticleEffect(GameObject prefab, bool alignToSun, float minDepth, float maxDepth)
        {
            if (prefab == null) return;
            var instance = Instantiate(prefab, transform);
            instance.name = prefab.name;
            var particles = instance.GetComponentInChildren<ParticleSystem>(true);
            if (particles == null)
            {
                Destroy(instance);
                return;
            }
            _spawnedParticles.Add(particles);
            underwaterArea.particleEffects.Add(new UnderwaterArea.ParticleEffect(minDepth, maxDepth, alignToSun)
            {
                particleSystem = particles
            });
        }

    }
}
