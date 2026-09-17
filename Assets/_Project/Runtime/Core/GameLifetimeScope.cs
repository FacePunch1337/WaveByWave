using StylizedWater3;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using WaveByWave.Networking;
using WaveByWave.Player;
using WaveByWave.Simulation;

namespace WaveByWave.Core
{
    [DefaultExecutionOrder(-900)]
    public sealed class GameLifetimeScope : MonoBehaviour
    {
        public static GameLifetimeScope Instance { get; private set; }
        public IServiceResolver Services => _services;

        [SerializeField] private NetworkSessionCoordinator sessionCoordinator;

        private readonly ServiceRegistry _services = new();
        private NetworkManager _networkManager;
        private PlatformNetworkTransform _shipMotion;

        // Server physics has to sample the same fixed network time as buoyancy.
        // Rendering, however, must use one continuous value for the whole frame.
        // A remote client's interpolated NetworkTime can be corrected in discrete
        // tick-sized steps, so applying it directly to the water shader makes the
        // wave phase visibly jump.
        private bool _renderWaterTimeInitialized;
        private float _renderWaterTimeOffset;

        private const float RenderWaterTimeCorrectionSpeed = 18f;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            _networkManager = GetComponent<NetworkManager>();

            // Keeps old generated Port scenes valid after the bridge stopped being a NetworkBehaviour.
            if (GetComponent<HybridSimulationBridge>() == null)
                gameObject.AddComponent<HybridSimulationBridge>();

            sessionCoordinator ??= GetComponentInChildren<NetworkSessionCoordinator>(true);
            _services.Register(sessionCoordinator);
            InjectScene(SceneManager.GetActiveScene());
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void FixedUpdate()
        {
            if (_networkManager == null || !_networkManager.IsListening)
            {
                WaterObject.CustomTime = -1f;
                return;
            }

            // Only the authoritative simulation samples water in FixedUpdate.
            // Remote clients do not run buoyancy for the replicated ship, and
            // leaving their render value untouched here avoids a second time source
            // fighting the smoothed value written from Update.
            if (!_networkManager.IsServer)
                return;

            // AlignToWater samples the same wave phase that will be rendered for this
            // authoritative physics step. This runs before the ship's default-order
            // FixedUpdate methods.
            WaterObject.CustomTime = Mathf.Max(0.0001f, (float)_networkManager.ServerTime.FixedTime);
        }

        private void Update()
        {
            if (_networkManager == null || !_networkManager.IsListening)
            {
                _renderWaterTimeInitialized = false;
                WaterObject.CustomTime = -1f;
                return;
            }

            // The ship is rendered from buffered physics snapshots on clients.
            // Use its presentation time as the target, but advance
            // the shader on the local render clock and correct the network offset
            // gradually. This keeps the water phase continuous when network ticks are
            // received or the interpolation clock is corrected.
            var targetNetworkTime = GetNetworkWaterTime();
            var localRenderTime = Time.time;
            var targetOffset = (float)(targetNetworkTime - localRenderTime);

            if (!_renderWaterTimeInitialized)
            {
                _renderWaterTimeOffset = targetOffset;
                _renderWaterTimeInitialized = true;
            }
            else
            {
                var correction = 1f - Mathf.Exp(-RenderWaterTimeCorrectionSpeed * Mathf.Max(0f, Time.deltaTime));
                _renderWaterTimeOffset = Mathf.Lerp(_renderWaterTimeOffset, targetOffset, correction);
            }

            WaterObject.CustomTime = Mathf.Max(
                0.0001f,
                localRenderTime + _renderWaterTimeOffset);
        }

        private double GetNetworkWaterTime()
        {
            if (_networkManager.IsServer)
            {
                return _networkManager.ServerTime.Time;
            }

            if (_shipMotion != null && _shipMotion.IsSpawned)
                return _shipMotion.PresentationServerTime;

            return _networkManager.ServerTime.Time - PlatformNetworkTransform.DefaultInterpolationDelay;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => InjectScene(scene);

        private void InjectScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return;

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour is PlatformNetworkTransform shipMotion)
                        _shipMotion = shipMotion;
                    if (behaviour is IServiceConsumer consumer)
                        consumer.Inject(_services);
                }
            }
        }

        private void OnDestroy()
        {
            if (Instance != this)
                return;

            SceneManager.sceneLoaded -= OnSceneLoaded;
            WaterObject.CustomTime = -1f;
            _services.Dispose();
            Instance = null;
        }
    }
}
