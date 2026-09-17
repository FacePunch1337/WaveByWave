using StylizedWater3;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.SceneManagement;
using WaveByWave.Networking;
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
            // AlignToWater samples the same wave phase that will be rendered for this
            // authoritative physics step. This runs before the ship's default-order
            // FixedUpdate methods.
            ApplyNetworkWaterTime(useFixedTime: true);
        }

        private void Update()
        {
            // A server-authoritative NetworkTransform is rendered from an interpolation
            // point in the past on clients. Drive SW3 from that exact point as well so
            // the visible wave crest stays underneath the replicated ship pose.
            ApplyNetworkWaterTime(useFixedTime: false);
        }

        private void ApplyNetworkWaterTime(bool useFixedTime)
        {
            if (_networkManager == null || !_networkManager.IsListening)
            {
                WaterObject.CustomTime = -1f;
                return;
            }

            NetworkTime waterTime;
            if (_networkManager.IsServer)
            {
                waterTime = _networkManager.ServerTime;
            }
            else
            {
                var interpolationTicks = Mathf.Max(
                    1,
                    _networkManager.NetworkTimeSystem.TickLatency +
                    NetworkTransform.InterpolationBufferTickOffset);
                waterTime = _networkManager.LocalTime.TimeTicksAgo(interpolationTicks);
            }

            var time = useFixedTime ? waterTime.FixedTime : waterTime.Time;
            // SW3 treats non-positive custom values as a request to fall back to local
            // Unity time. Keep the first network frames on the synchronized timeline.
            WaterObject.CustomTime = Mathf.Max(0.0001f, (float)time);
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
