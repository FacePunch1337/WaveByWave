using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Netcode.Transports.Facepunch;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;
using WaveByWave.Core;
using WaveByWave.Items;
using WaveByWave.Player;
using UdpSocket = System.Net.Sockets.Socket;

namespace WaveByWave.Networking
{
    [DefaultExecutionOrder(-800)]
    public sealed class NetworkSessionCoordinator : MonoBehaviour, IDisposable
    {
        private const string LobbyNameKey = "wave_by_wave_name";
        private const string BuildKey = "wave_by_wave_build";
        private const string RelayPortKey = "wave_by_wave_relay_port";

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
        private bool _ownsSteamClient;
        private bool _disposed;
        private readonly List<TaskCompletionSource<bool>> _frameWaiters = new();

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
            if (networkManager != null)
                networkManager.OnClientConnectedCallback += OnClientConnected;

            if (!SteamClient.IsValid && steamAppId != 0)
            {
                try
                {
                    SteamClient.Init(steamAppId, false);
                    _ownsSteamClient = true;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Steam недоступен, будет использован локальный transport: {exception.Message}");
                }
            }
            if (SteamClient.IsValid)
            {
                try { SteamNetworkingUtils.InitRelayNetworkAccess(); }
                catch (Exception exception) { Debug.LogWarning($"Steam relay: {exception.Message}"); }
            }
        }

        private void Update()
        {
            if (!_disposed && SteamClient.IsValid)
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
            if (_starting || _disposed || !SteamAvailable || networkManager == null)
                return;

            _starting = true;
            SetStatus("Создание Steam-лобби…");

            try
            {
                // Do not start/stop Steam sockets inside Steam's callback dispatcher.
                await WaitForNextFrameAsync();
                await LeaveLobbyAndShutdownAsync();
                if (_disposed) return;
                if (!SteamClient.IsValid) throw new InvalidOperationException("Steam API недоступен.");
                var created = await SteamMatchmaking.CreateLobbyAsync(4);
                if (!created.HasValue)
                    throw new InvalidOperationException("Steam не вернул созданное лобби.");
                if (_disposed) { if (SteamClient.IsValid) created.Value.Leave(); return; }

                CurrentLobby = created.Value;
                CurrentLobby.SetPublic();
                CurrentLobby.SetJoinable(false);
                CurrentLobby.SetData(LobbyNameKey, $"{SteamClient.Name} — Wave by Wave");
                CurrentLobby.SetData(BuildKey, Application.version);

                await WaitForNextFrameAsync();
                if (_disposed) return;
                UseSteamTransport();
                steamTransport.virtualPort = 1 + (Guid.NewGuid().GetHashCode() & 0x7fff);
                if (!steamTransport.TryPrepareServer())
                    throw new InvalidOperationException($"Steam relay недоступен: {steamTransport.StartupError}");
                CurrentLobby.SetData(RelayPortKey, steamTransport.virtualPort.ToString());
                if (!networkManager.StartHost())
                    throw new InvalidOperationException("NGO не смог запустить host.");
                BindNetworkSceneCallbacks();

                CurrentLobby.SetGameServer(SteamClient.SteamId);
                CurrentLobby.SetJoinable(true);
                SetStatus($"Steam-лобби создано • {CurrentLobby.MemberCount}/4");
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Steam lobby failed, switching to local transport: {exception.Message}");
                if (!_disposed)
                {
                    try { await StartLocalHostCoreAsync(); }
                    catch (Exception fallbackException) { SetStatus($"Не удалось запустить сеть: {fallbackException.Message}"); }
                }
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

            ClearDynamicWorldItems();
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
                ResetVoyageStateAndRecreatePlayers();
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
            if (_starting || _disposed || networkManager == null)
                return;

            _starting = true;
            try
            {
                SetStatus("Остановка предыдущей сессии…");
                await LeaveLobbyAndShutdownAsync();
                if (_disposed) return;
                UseLocalTransport();
                var started = networkManager.StartClient();
                if (started) BindNetworkSceneCallbacks();
                var targetPort = GetLocalConnectionPort();
                SetStatus(started ? $"Подключение к {localAddress}:{targetPort}…" : "Локальный client не запустился.");
                if (started) await WaitForLocalConnectionAsync(targetPort);
            }
            catch (Exception exception) { SetStatus($"Ошибка локального подключения: {exception.Message}"); }
            finally { _starting = false; }
        }

        public async void StartLocalHost()
        {
            if (_starting || _disposed || networkManager == null)
                return;

            _starting = true;
            try { await StartLocalHostCoreAsync(); }
            catch (Exception exception) { SetStatus($"Локальный host не запустился: {exception.Message}"); }
            finally { _starting = false; }
        }

        private async Task StartLocalHostCoreAsync()
        {
            await LeaveLobbyAndShutdownAsync();
            if (_disposed) return;
            var hostPort = ValidateLocalHostPort();
            UseLocalTransport(hostPort);
            var started = networkManager != null && networkManager.StartHost();
            if (started)
                BindNetworkSceneCallbacks();
            SetStatus(started ? $"Локальный host • {localAddress}:{hostPort}" : "Локальный host не запустился.");
        }

        private ushort GetLocalConnectionPort() => GetLocalPortOverride() ??
            (localPort != 0 ? localPort : (ushort)7777);

        private ushort ValidateLocalHostPort()
        {
            var port = GetLocalConnectionPort();
            // Local Host and Local Connect must agree on the same configured endpoint.
            // Silently choosing another port creates an unreachable second session.
            using var socket = new UdpSocket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                return port;
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse ||
                exception.SocketErrorCode == SocketError.AccessDenied)
            {
                throw new InvalidOperationException($"UDP-порт {port} занят или недоступен. Если хост уже запущен, используйте Local Connect.");
            }
        }

        private async Task WaitForLocalConnectionAsync(ushort port)
        {
            var deadline = Time.realtimeSinceStartup + 8f;
            while (!_disposed && networkManager != null && networkManager.IsListening &&
                !networkManager.IsConnectedClient && !networkManager.IsApproved && Time.realtimeSinceStartup < deadline)
                await WaitForNextFrameAsync();
            if (_disposed || networkManager == null) return;
            if (networkManager.IsConnectedClient)
            {
                SetStatus($"Подключено к {localAddress}:{port}");
                return;
            }
            if (networkManager.IsApproved)
            {
                // Scene synchronization can take longer than the socket handshake.
                // Leave it to NGO's scene timeout once the host has accepted us.
                SetStatus("Хост принял подключение • синхронизация сцены…");
                return;
            }
            var reason = networkManager.DisconnectReason;
            await ShutdownNetworkAsync();
            if (!_disposed)
                SetStatus(string.IsNullOrWhiteSpace(reason)
                    ? $"Нет подключения к {localAddress}:{port}. Убедитесь, что на другом экземпляре включён Local Host."
                    : $"Подключение отклонено: {reason}");
        }

        private static ushort? GetLocalPortOverride()
        {
            // Use the same explicit override as NGO/UTP, including its status text.
            var arguments = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < arguments.Length; i++)
                if (arguments[i] == "-port" && ushort.TryParse(arguments[i + 1], out var port) && port > 0)
                    return port;
            return null;
        }

        private void OnClientConnected(ulong clientId)
        {
            if (_disposed || networkManager == null || networkManager.IsServer ||
                clientId != networkManager.LocalClientId) return;
            SetStatus(ReferenceEquals(networkManager.NetworkConfig.NetworkTransport, localTransport)
                ? $"Подключено к {localAddress}:{GetLocalConnectionPort()}" : "Подключено к Steam-хосту");
        }

        private async void OnGameLobbyJoinRequested(Lobby lobby, SteamId friendId)
        {
            if (_starting || _disposed)
                return;

            _starting = true;
            SetStatus("Входим в Steam-лобби друга…");

            try
            {
                // Leave Steam's callback dispatch before changing sockets/session.
                await WaitForNextFrameAsync();
                await LeaveLobbyAndShutdownAsync();
                if (!SteamAvailable || !SteamClient.IsValid)
                    throw new InvalidOperationException("Steam API не удалось перезапустить после остановки host.");

                var result = await lobby.Join();
                if (result != RoomEnter.Success)
                    throw new InvalidOperationException($"Steam Join вернул {result}.");

                CurrentLobby = lobby;
                await WaitForNextFrameAsync();
                if (_disposed) return;
                UseSteamTransport();
                steamTransport.targetSteamId = lobby.Owner.Id;
                var relayPort = lobby.GetData(RelayPortKey);
                steamTransport.virtualPort = int.TryParse(relayPort, out var parsedPort) && parsedPort >= 0 ? parsedPort : 0;
                if (!steamTransport.TryPrepareClient())
                    throw new InvalidOperationException($"Steam-подключение недоступно: {steamTransport.StartupError}");
                if (!networkManager.StartClient())
                    throw new InvalidOperationException("NGO не смог запустить client.");
                BindNetworkSceneCallbacks();

                SetStatus($"Подключение к {lobby.Owner.Name}…");
            }
            catch (Exception exception)
            {
                if (!_disposed)
                {
                    try { await LeaveLobbyAndShutdownAsync(); }
                    catch (Exception cleanupException) { Debug.LogWarning($"Остановка сессии: {cleanupException.Message}"); }
                    SetStatus($"Ошибка подключения: {exception.Message}");
                }
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

        private void UseLocalTransport(ushort? serverPort = null)
        {
            if (networkManager == null || localTransport == null)
                throw new InvalidOperationException("Локальный transport не настроен.");

            var targetPort = serverPort ?? GetLocalConnectionPort();
            localTransport.SetConnectionData(localAddress, targetPort, "0.0.0.0");
            networkManager.NetworkConfig.NetworkTransport = localTransport;
        }

        private async Task LeaveLobbyAndShutdownAsync()
        {
            await WaitForNextFrameAsync();
            if (_disposed) return;
            if (CurrentLobby.Id != 0)
            {
                if (SteamClient.IsValid) CurrentLobby.Leave();
                CurrentLobby = default;
            }

            await ShutdownNetworkAsync();
            if (_disposed) return;
            // Also close a socket prepared before NGO started. Shutdown is idempotent.
            steamTransport?.Shutdown();
            SteamAvailable = SteamClient.IsValid;
        }

        private async Task ShutdownNetworkAsync()
        {
            if (networkManager == null || (!networkManager.IsListening && !networkManager.ShutdownInProgress))
                return;

            UnbindNetworkSceneCallbacks();
            networkManager.Shutdown();
            var deadline = Time.realtimeSinceStartup + 3f;
            while (!_disposed && networkManager != null && networkManager.ShutdownInProgress && Time.realtimeSinceStartup < deadline)
                await WaitForNextFrameAsync();
            if (_disposed) return;
            if (networkManager != null && networkManager.ShutdownInProgress)
                throw new InvalidOperationException("Предыдущая сессия ещё останавливается; повторите запуск после остановки.");
        }

        private Task WaitForNextFrameAsync()
        {
            if (_disposed) return Task.CompletedTask;
            var completion = new TaskCompletionSource<bool>();
            _frameWaiters.Add(completion);
            StartCoroutine(CompleteOnNextFrame(completion));
            return completion.Task;
        }

        private IEnumerator CompleteOnNextFrame(TaskCompletionSource<bool> completion)
        {
            // Task.Yield only posts a continuation; it does not guarantee a Unity frame.
            // NGO needs subsequent player-loop updates to finish shutting down.
            yield return null;
            _frameWaiters.Remove(completion);
            completion.TrySetResult(true);
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

        private void ResetVoyageStateAndRecreatePlayers()
        {
            if (networkManager == null || !networkManager.IsServer)
                return;

            ClearDynamicWorldItems();

            var playerPrefab = networkManager.NetworkConfig.PlayerPrefab;
            var playerPrefabObject = playerPrefab != null ? playerPrefab.GetComponent<NetworkObject>() : null;
            if (playerPrefabObject == null)
            {
                Debug.LogError("PlayerPrefab is missing, voyage players cannot be recreated.");
                return;
            }

            var clientIds = new ulong[networkManager.ConnectedClientsList.Count];
            for (var i = 0; i < networkManager.ConnectedClientsList.Count; i++)
            {
                var client = networkManager.ConnectedClientsList[i];
                clientIds[i] = client.ClientId;
                if (client.PlayerObject != null && client.PlayerObject.IsSpawned)
                    client.PlayerObject.Despawn(true);
            }

            // A fresh prefab instance creates a new inventory NetworkList and invokes
            // the normal starting-item setup. Future meta currency can be copied here;
            // voyage inventory and progression intentionally are not carried to port.
            foreach (var clientId in clientIds)
            {
                var player = networkManager.SpawnManager.InstantiateAndSpawn(
                    playerPrefabObject, clientId, false, true);
                if (player == null)
                    Debug.LogError($"Не удалось пересоздать игрока {clientId} после завершения заплыва.");
            }
        }

        private void ClearDynamicWorldItems()
        {
            if (networkManager == null || !networkManager.IsServer || networkManager.SpawnManager == null)
                return;

            // Use NGO's authoritative spawned-object registry so items in the
            // DontDestroyOnLoad scene are included in the cleanup.
            var spawnedObjects = new List<NetworkObject>(networkManager.SpawnManager.SpawnedObjectsList);
            foreach (var networkObject in spawnedObjects)
            {
                if (networkObject == null || !networkObject.IsSpawned || networkObject.IsSceneObject == true)
                    continue;
                if (networkObject.TryGetComponent<WorldItem>(out _))
                    networkObject.Despawn(true);
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
            if (networkManager != null)
                networkManager.OnClientConnectedCallback -= OnClientConnected;
            UnbindNetworkSceneCallbacks();
            UnbindSteamCallbacks();
        }

        private void OnDestroy()
        {
            _disposed = true;
            var waiters = _frameWaiters.ToArray();
            _frameWaiters.Clear();
            foreach (var waiter in waiters) waiter.TrySetCanceled();
            Dispose();
            if (Instance == this)
            {
                Instance = null;
                if (CurrentLobby.Id != 0 && SteamClient.IsValid) CurrentLobby.Leave();
                steamTransport?.Shutdown();
                if (_ownsSteamClient && SteamClient.IsValid) SteamClient.Shutdown();
            }
        }
    }
}
