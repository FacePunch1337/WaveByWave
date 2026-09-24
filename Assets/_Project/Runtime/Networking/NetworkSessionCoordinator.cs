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
using WaveByWave.Generation;
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
        private const string EntitiesRelayPortKey = "wave_by_wave_entities_relay_port";

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
        private bool _voyageInProgress;
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
            {
                networkManager.NetworkConfig.ConnectionApproval = true;
                networkManager.ConnectionApprovalCallback += ApproveConnection;
                networkManager.OnClientConnectedCallback += OnClientConnected;
                networkManager.OnClientDisconnectCallback += OnClientDisconnected;
            }

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

                var entitiesRelayPort = 0;
                string entitiesRelayError = null;
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    entitiesRelayPort = 1 + (Guid.NewGuid().GetHashCode() & 0x7fff);
                    if (entitiesRelayPort == steamTransport.virtualPort)
                        continue;
                    SteamNetcodeSession.Configure(entitiesRelayPort, SteamClient.SteamId);
                    if (SteamNetcodeSession.TryPrepareHost(out entitiesRelayError))
                        break;
                    entitiesRelayPort = 0;
                }
                if (entitiesRelayPort == 0)
                    throw new InvalidOperationException($"Steam relay для Netcode for Entities недоступен: {entitiesRelayError}");
                CurrentLobby.SetData(EntitiesRelayPortKey, entitiesRelayPort.ToString());
                SteamNetcodeSession.StartHostAndLocalClient();

                CurrentLobby.SetGameServer(SteamClient.SteamId);
                SetVoyageInProgress(SceneManager.GetActiveScene().name != GameScenes.Port);
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
            if (_voyageInProgress || SceneManager.GetActiveScene().name != GameScenes.Port)
            {
                SetStatus("Приглашать игроков можно только в порту, до начала плавания.");
                return;
            }
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

            if (_voyageInProgress || SceneManager.GetActiveScene().name != GameScenes.Port)
                return;
            foreach (var client in networkManager.ConnectedClientsList)
                if (client.PlayerObject == null)
                {
                    SetStatus("Дождитесь загрузки всех игроков перед отправлением.");
                    return;
                }

            // Close admission before scene loading starts, including direct transport joins.
            SetVoyageInProgress(true);
            ClearDynamicWorldItems();
            PreparePlayersForSceneTransition();
            OceanWorldDirector.BeginOceanLoading();
            var result = networkManager.SceneManager.LoadScene(GameScenes.Ocean, LoadSceneMode.Single);
            if (result != SceneEventProgressStatus.Started)
            {
                CancelLocalSceneTransition();
                OceanWorldDirector.CancelOceanLoading();
                SetVoyageInProgress(false);
            }
            SetStatus(result == SceneEventProgressStatus.Started
                ? "Отправляемся в море…"
                : $"Не удалось загрузить сцену: {result}");
        }

        public void ReturnToPort() => TryReturnToPort();

        public bool TryReturnToPort()
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
                return result == SceneEventProgressStatus.Started;
            }
            return false;
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
                var targetPort = GetLocalConnectionPort();
                if (started)
                {
                    BindNetworkSceneCallbacks();
                    SteamNetcodeSession.ConfigureLocal(GetLocalEntitiesPort(targetPort), localAddress);
                    SteamNetcodeSession.StartLocalClient();
                }
                SetStatus(started ? $"Подключение к {localAddress}:{targetPort}…" : "Локальный client не запустился.");
                if (started) await WaitForLocalConnectionAsync(targetPort);
            }
            catch (Exception exception)
            {
                try { await LeaveLobbyAndShutdownAsync(); }
                catch (Exception cleanupException) { Debug.LogWarning($"Остановка локальной сессии: {cleanupException.Message}"); }
                SetStatus($"Ошибка локального подключения: {exception.Message}");
            }
            finally { _starting = false; }
        }

        public async void StartLocalHost()
        {
            if (_starting || _disposed || networkManager == null)
                return;

            _starting = true;
            try { await StartLocalHostCoreAsync(); }
            catch (Exception exception)
            {
                try { await LeaveLobbyAndShutdownAsync(); }
                catch (Exception cleanupException) { Debug.LogWarning($"Остановка локального host: {cleanupException.Message}"); }
                SetStatus($"Локальный host не запустился: {exception.Message}");
            }
            finally { _starting = false; }
        }

        private async Task StartLocalHostCoreAsync()
        {
            await LeaveLobbyAndShutdownAsync();
            if (_disposed) return;
            var hostPort = ValidateLocalHostPort();
            UseLocalTransport(hostPort);
            var entitiesPort = GetLocalEntitiesPort(hostPort);
            SteamNetcodeSession.ConfigureLocal(entitiesPort, localAddress);
            if (!SteamNetcodeSession.TryPrepareLocalHost(out var entitiesError))
                throw new InvalidOperationException($"Локальный DOTS transport недоступен на UDP-порту {entitiesPort}: {entitiesError}");
            var started = networkManager != null && networkManager.StartHost();
            if (started)
            {
                BindNetworkSceneCallbacks();
                SteamNetcodeSession.StartLocalHostAndClient();
                SetVoyageInProgress(SceneManager.GetActiveScene().name != GameScenes.Port);
            }
            else
            {
                SteamNetcodeSession.Shutdown();
            }
            SetStatus(started ? $"Локальный host • {localAddress}:{hostPort}" : "Локальный host не запустился.");
        }

        private ushort GetLocalConnectionPort() => GetLocalPortOverride() ??
            (localPort != 0 ? localPort : (ushort)7777);

        private static ushort GetLocalEntitiesPort(ushort ngoPort) =>
            ngoPort < ushort.MaxValue ? (ushort)(ngoPort + 1) : (ushort)(ngoPort - 1);

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
            SteamNetcodeSession.Shutdown();
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

        private void ApproveConnection(NetworkManager.ConnectionApprovalRequest request,
            NetworkManager.ConnectionApprovalResponse response)
        {
            response.Approved = CanApproveConnection(_voyageInProgress,
                SceneManager.GetActiveScene().name, request.ClientNetworkId);
            response.CreatePlayerObject = response.Approved;
            response.Pending = false;
            response.Reason = response.Approved ? string.Empty :
                "Плавание уже началось. Подключиться можно после возвращения команды в порт.";
        }

        internal static bool CanApproveConnection(bool voyageInProgress, string activeScene, ulong clientId) =>
            clientId == NetworkManager.ServerClientId || (!voyageInProgress && activeScene == GameScenes.Port);

        private void SetVoyageInProgress(bool value)
        {
            _voyageInProgress = value;
            if (IsLobbyOwner) CurrentLobby.SetJoinable(!value);
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (_disposed || networkManager == null || networkManager.IsServer ||
                clientId != networkManager.LocalClientId) return;
            SteamNetcodeSession.Shutdown();
            var reason = networkManager.DisconnectReason;
            if (!string.IsNullOrWhiteSpace(reason)) SetStatus($"Подключение отклонено: {reason}");
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

                var entitiesRelayPortValue = lobby.GetData(EntitiesRelayPortKey);
                if (!int.TryParse(entitiesRelayPortValue, out var entitiesRelayPort) || entitiesRelayPort <= 0)
                    throw new InvalidOperationException("Steam-лобби не опубликовало порт Netcode for Entities.");
                SteamNetcodeSession.Configure(entitiesRelayPort, lobby.Owner.Id);
                SteamNetcodeSession.StartClient();

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
            SteamNetcodeSession.Shutdown();
            await WaitForNextFrameAsync();
            if (_disposed) return;
            if (CurrentLobby.Id != 0)
            {
                if (SteamClient.IsValid) CurrentLobby.Leave();
                CurrentLobby = default;
            }

            await ShutdownNetworkAsync();
            if (_disposed) return;
            _voyageInProgress = false;
            // NFE owns a separate server AND client driver. Let both process disconnect cleanup
            // before Configure replaces their stores and switches Steam/UDP endpoints.
            var entitiesDeadline = Time.realtimeSinceStartup + 5f;
            while (SteamNetcodeSession.HasPendingDisconnects && !_disposed &&
                   Time.realtimeSinceStartup < entitiesDeadline)
                await WaitForNextFrameAsync();
            if (_disposed) return;
            if (SteamNetcodeSession.HasPendingDisconnects)
                throw new InvalidOperationException("Не удалось завершить предыдущую NFE-сессию.");
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
            networkManager.SceneManager.OnLoadEventCompleted += OnNetworkSceneLoadCompleted;
            _networkSceneCallbacksBound = true;
        }

        private void UnbindNetworkSceneCallbacks()
        {
            if (!_networkSceneCallbacksBound || networkManager == null || networkManager.SceneManager == null)
                return;

            networkManager.SceneManager.OnLoad -= OnNetworkSceneLoadStarted;
            networkManager.SceneManager.OnLoadEventCompleted -= OnNetworkSceneLoadCompleted;
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

            if (sceneName == GameScenes.Ocean)
            {
                if (networkManager.IsServer) SetVoyageInProgress(true);
                OceanWorldDirector.BeginOceanLoading();
            }
            else if (sceneName == GameScenes.Port)
                OceanWorldDirector.CancelOceanLoading();

            var localPlayer = networkManager.SpawnManager.GetLocalPlayerObject();
            if (localPlayer != null && localPlayer.TryGetComponent(out NetworkPlayerController player))
                player.PrepareForSceneTransitionLocally();
        }

        private void OnNetworkSceneLoadCompleted(string sceneName, LoadSceneMode loadSceneMode,
            List<ulong> completed, List<ulong> timedOut)
        {
            // Reopen only after the crew has finished loading Port, never during the return transition.
            if (networkManager != null && networkManager.IsServer && sceneName == GameScenes.Port)
                SetVoyageInProgress(false);
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
            {
                networkManager.ConnectionApprovalCallback -= ApproveConnection;
                networkManager.OnClientConnectedCallback -= OnClientConnected;
                networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            }
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
