using System;
using System.Collections;
using System.Threading.Tasks;
using Netcode.Transports.Facepunch;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;
using WaveByWave.Core;
using WaveByWave.Player;

namespace WaveByWave.Networking
{
    [DefaultExecutionOrder(-800)]
    public sealed class NetworkSessionCoordinator : MonoBehaviour, IDisposable
    {
        private const string LobbyNameKey = "wave_by_wave_name";
        private const string BuildKey = "wave_by_wave_build";

        public static NetworkSessionCoordinator Instance { get; private set; }
        public event Action StateChanged;

        public bool SteamAvailable { get; private set; }
        public bool IsLobbyOwner => CurrentLobby.Id != 0 && SteamAvailable && CurrentLobby.IsOwnedBy(SteamClient.SteamId);
        public Lobby CurrentLobby { get; private set; }
        public string Status { get; private set; } = "Инициализация сети…";

        [Header("Dependencies")]
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private FacepunchTransport steamTransport;
        [SerializeField] private UnityTransport localTransport;

        [Header("Development fallback")]
        [SerializeField] private uint steamAppId = 480;
        [SerializeField] private string localAddress = "127.0.0.1";
        [SerializeField] private ushort localPort = 7777;
        [SerializeField, Min(0.25f)] private float steamStartupTimeout = 2f;

        private bool _callbacksBound;
        private bool _networkSceneCallbacksBound;
        private bool _starting;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            networkManager ??= GetComponent<NetworkManager>();
            steamTransport ??= GetComponent<FacepunchTransport>();
            localTransport ??= GetComponent<UnityTransport>();

            if (!SteamClient.IsValid && steamAppId != 0)
            {
                try
                {
                    SteamClient.Init(steamAppId, false);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Steam недоступен, будет использован локальный transport: {exception.Message}");
                }
            }
        }

        private void Update()
        {
            if (SteamClient.IsValid)
                SteamClient.RunCallbacks();
        }

        private IEnumerator Start()
        {
            var deadline = Time.realtimeSinceStartup + steamStartupTimeout;
            while (!SteamClient.IsValid && Time.realtimeSinceStartup < deadline)
                yield return null;

            SteamAvailable = SteamClient.IsValid;
            if (SteamAvailable)
            {
                BindSteamCallbacks();
                _ = CreateSteamLobbyAndHostAsync();
            }
            else
            {
                StartLocalHost();
            }
        }

        private void BindSteamCallbacks()
        {
            if (_callbacksBound)
                return;

            SteamFriends.OnGameLobbyJoinRequested += OnGameLobbyJoinRequested;
            SteamMatchmaking.OnLobbyMemberJoined += OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberLeave += OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberDisconnected += OnLobbyMemberChanged;
            _callbacksBound = true;
        }

        private void UnbindSteamCallbacks()
        {
            if (!_callbacksBound)
                return;

            SteamFriends.OnGameLobbyJoinRequested -= OnGameLobbyJoinRequested;
            SteamMatchmaking.OnLobbyMemberJoined -= OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberLeave -= OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberDisconnected -= OnLobbyMemberChanged;
            _callbacksBound = false;
        }

        public async Task CreateSteamLobbyAndHostAsync()
        {
            if (_starting || !SteamAvailable || networkManager == null)
                return;

            _starting = true;
            SetStatus("Создание Steam-лобби…");
            await ShutdownNetworkAsync();

            try
            {
                var created = await SteamMatchmaking.CreateLobbyAsync(4);
                if (!created.HasValue)
                    throw new InvalidOperationException("Steam не вернул созданное лобби.");

                CurrentLobby = created.Value;
                CurrentLobby.SetPublic();
                CurrentLobby.SetJoinable(true);
                CurrentLobby.SetData(LobbyNameKey, $"{SteamClient.Name} — Wave by Wave");
                CurrentLobby.SetData(BuildKey, Application.version);

                UseSteamTransport();
                if (!networkManager.StartHost())
                    throw new InvalidOperationException("NGO не смог запустить host.");
                BindNetworkSceneCallbacks();

                CurrentLobby.SetGameServer(SteamClient.SteamId);
                SetStatus($"Steam-лобби создано • {CurrentLobby.MemberCount}/4");
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Steam lobby failed, switching to local transport: {exception.Message}");
                CurrentLobby = default;
                StartLocalHost();
            }
            finally
            {
                _starting = false;
            }
        }

        public void OpenInviteOverlay()
        {
            if (!SteamAvailable || CurrentLobby.Id == 0)
            {
                SetStatus("Steam-лобби недоступно — запущен локальный режим.");
                return;
            }

            SteamFriends.OpenGameInviteOverlay(CurrentLobby.Id);
        }

        public void StartVoyage()
        {
            if (networkManager == null || !networkManager.IsServer)
            {
                SetStatus("Только хост может выбрать карту и начать плавание.");
                return;
            }

            PreparePlayersForSceneTransition();
            var result = networkManager.SceneManager.LoadScene(GameScenes.Ocean, LoadSceneMode.Single);
            if (result != SceneEventProgressStatus.Started)
                CancelLocalSceneTransition();
            SetStatus(result == SceneEventProgressStatus.Started
                ? "Отправляемся в море…"
                : $"Не удалось загрузить сцену: {result}");
        }

        public void ReturnToPort()
        {
            if (networkManager != null && networkManager.IsServer)
            {
                PreparePlayersForSceneTransition();
                var result = networkManager.SceneManager.LoadScene(GameScenes.Port, LoadSceneMode.Single);
                if (result != SceneEventProgressStatus.Started)
                {
                    CancelLocalSceneTransition();
                    SetStatus($"Не удалось вернуться в порт: {result}");
                }
            }
        }

        public async void StartLocalClient()
        {
            if (_starting)
                return;

            _starting = true;
            await LeaveLobbyAndShutdownAsync();
            UseLocalTransport();
            var started = networkManager.StartClient();
            if (started)
                BindNetworkSceneCallbacks();
            SetStatus(started ? $"Подключение к {localAddress}:{localPort}…" : "Локальный client не запустился.");
            _starting = false;
        }

        public async void StartLocalHost()
        {
            if (_starting && networkManager != null && networkManager.IsListening)
                return;

            await LeaveLobbyAndShutdownAsync();
            UseLocalTransport();
            var started = networkManager != null && networkManager.StartHost();
            if (started)
                BindNetworkSceneCallbacks();
            SetStatus(started ? $"Локальный host • {localAddress}:{localPort}" : "Локальный host не запустился.");
        }

        private async void OnGameLobbyJoinRequested(Lobby lobby, SteamId friendId)
        {
            if (_starting)
                return;

            _starting = true;
            SetStatus("Входим в Steam-лобби друга…");

            try
            {
                // Leave Steam's callback dispatch before shutting down the current host.
                // FacepunchTransport 2.0.0 shuts down the global Steam client together
                // with its socket, so joining in the same callback otherwise hits a null
                // SteamMatchmaking interface inside Lobby.Join().
                await Task.Yield();
                await LeaveLobbyAndShutdownAsync();
                if (!SteamAvailable || !SteamClient.IsValid)
                    throw new InvalidOperationException("Steam API не удалось перезапустить после остановки host.");

                var result = await lobby.Join();
                if (result != RoomEnter.Success)
                    throw new InvalidOperationException($"Steam Join вернул {result}.");

                CurrentLobby = lobby;
                UseSteamTransport();
                steamTransport.targetSteamId = lobby.Owner.Id;
                if (!networkManager.StartClient())
                    throw new InvalidOperationException("NGO не смог запустить client.");
                BindNetworkSceneCallbacks();

                SetStatus($"Подключение к {lobby.Owner.Name}…");
            }
            catch (Exception exception)
            {
                Debug.LogError(exception);
                SetStatus($"Ошибка подключения: {exception.Message}");
            }
            finally
            {
                _starting = false;
            }
        }

        private void OnLobbyMemberChanged(Lobby lobby, Friend friend)
        {
            if (CurrentLobby.Id != lobby.Id)
                return;

            SetStatus($"Steam-лобби • {lobby.MemberCount}/4");
        }

        private void UseSteamTransport()
        {
            if (networkManager == null || steamTransport == null)
                throw new InvalidOperationException("FacepunchTransport не настроен.");

            networkManager.NetworkConfig.NetworkTransport = steamTransport;
        }

        private void UseLocalTransport()
        {
            if (networkManager == null || localTransport == null)
                return;

            localTransport.SetConnectionData(localAddress, localPort);
            networkManager.NetworkConfig.NetworkTransport = localTransport;
        }

        private async Task LeaveLobbyAndShutdownAsync()
        {
            if (CurrentLobby.Id != 0)
            {
                CurrentLobby.Leave();
                CurrentLobby = default;
            }

            var restoreSteam =
                SteamClient.IsValid &&
                networkManager != null &&
                steamTransport != null &&
                ReferenceEquals(networkManager.NetworkConfig.NetworkTransport, steamTransport) &&
                (networkManager.IsListening || networkManager.ShutdownInProgress);

            if (restoreSteam)
                UnbindSteamCallbacks();

            await ShutdownNetworkAsync();

            if (restoreSteam)
                RestoreSteamAfterTransportShutdown();
        }

        private void RestoreSteamAfterTransportShutdown()
        {
            try
            {
                if (!SteamClient.IsValid)
                    SteamClient.Init(steamAppId, false);

                SteamAvailable = SteamClient.IsValid;
                if (!SteamAvailable)
                    return;

                // The transport keeps its own initialized flag after Shutdown(), so it
                // will not request relay access again when NGO starts it a second time.
                SteamNetworkingUtils.InitRelayNetworkAccess();
                BindSteamCallbacks();
            }
            catch (Exception exception)
            {
                SteamAvailable = false;
                Debug.LogError($"Не удалось восстановить Steam после остановки transport: {exception}");
            }
        }

        private async Task ShutdownNetworkAsync()
        {
            if (networkManager == null || (!networkManager.IsListening && !networkManager.ShutdownInProgress))
                return;

            UnbindNetworkSceneCallbacks();
            networkManager.Shutdown();
            var deadline = Time.realtimeSinceStartup + 3f;
            while (networkManager != null && networkManager.ShutdownInProgress && Time.realtimeSinceStartup < deadline)
                await Task.Yield();
        }

        private void BindNetworkSceneCallbacks()
        {
            if (_networkSceneCallbacksBound || networkManager == null || networkManager.SceneManager == null)
                return;

            networkManager.SceneManager.OnLoad += OnNetworkSceneLoadStarted;
            _networkSceneCallbacksBound = true;
        }

        private void UnbindNetworkSceneCallbacks()
        {
            if (!_networkSceneCallbacksBound || networkManager == null || networkManager.SceneManager == null)
                return;

            networkManager.SceneManager.OnLoad -= OnNetworkSceneLoadStarted;
            _networkSceneCallbacksBound = false;
        }

        private void OnNetworkSceneLoadStarted(
            ulong clientId,
            string sceneName,
            LoadSceneMode loadSceneMode,
            AsyncOperation asyncOperation)
        {
            if (networkManager == null || clientId != networkManager.LocalClientId)
                return;

            var localPlayer = networkManager.SpawnManager.GetLocalPlayerObject();
            if (localPlayer != null && localPlayer.TryGetComponent(out NetworkPlayerController player))
                player.PrepareForSceneTransitionLocally();
        }

        private void PreparePlayersForSceneTransition()
        {
            if (networkManager == null || !networkManager.IsServer)
                return;

            // Dynamic player objects must be roots before a Single-mode scene unload. Otherwise
            // NGO treats them as children of the scene-owned ship and they do not migrate to DDOL.
            foreach (var client in networkManager.ConnectedClientsList)
            {
                var playerObject = client.PlayerObject;
                if (playerObject == null ||
                    !playerObject.TryGetComponent(out NetworkPlayerController player))
                    continue;

                if (player.IsOwner)
                    player.PrepareForSceneTransitionLocally();
                player.RemoveNetworkParentOnServer();
            }
        }

        private void CancelLocalSceneTransition()
        {
            if (networkManager == null)
                return;

            var localPlayer = networkManager.SpawnManager.GetLocalPlayerObject();
            if (localPlayer != null && localPlayer.TryGetComponent(out NetworkPlayerController player))
                player.CancelSceneTransitionLocally();
        }

        private void SetStatus(string value)
        {
            Status = value;
            StateChanged?.Invoke();
            Debug.Log($"[Session] {value}");
        }

        public void Dispose()
        {
            UnbindNetworkSceneCallbacks();
            UnbindSteamCallbacks();
        }

        private void OnDestroy()
        {
            Dispose();
            if (Instance == this)
                Instance = null;
        }
    }
}
