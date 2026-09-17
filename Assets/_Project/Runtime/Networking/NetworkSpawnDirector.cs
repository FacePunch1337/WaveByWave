using System.Collections;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using WaveByWave.Player;
using WaveByWave.Ships;

namespace WaveByWave.Networking
{
    public sealed class NetworkSpawnDirector : MonoBehaviour
    {
        [SerializeField] private Transform[] spawnPoints;

        private NetworkManager _manager;
        private Transform[] _validPoints;
        private NetworkShipController _shipSpawnSource;
        private int _sceneBuildIndex;

        private IEnumerator Start()
        {
            _sceneBuildIndex = gameObject.scene.buildIndex;
            _validPoints = spawnPoints != null && spawnPoints.Length > 0
                ? spawnPoints.Where(point => point != null).ToArray()
                : new[] { transform };
            _shipSpawnSource = FindObjectsByType<NetworkShipController>(FindObjectsSortMode.None)
                .FirstOrDefault(ship => ship.gameObject.scene == gameObject.scene);

            // In the initial Port scene the director starts before Steam/local hosting has
            // necessarily finished. Stay alive until NGO is listening instead of permanently
            // missing the first connection after one frame.
            while ((_manager = NetworkManager.Singleton) == null || !_manager.IsListening)
                yield return null;

            if (!_manager.IsServer)
                yield break;

            _manager.OnClientConnectedCallback += OnClientConnected;
            if (_manager.SceneManager != null)
            {
                _manager.SceneManager.OnLoadComplete += OnLoadComplete;
                _manager.SceneManager.OnSynchronizeComplete += OnSynchronizeComplete;
            }

            foreach (var client in _manager.ConnectedClientsList)
                StartCoroutine(PlaceClientWhenReady(client.ClientId));
        }

        private void OnClientConnected(ulong clientId) => StartCoroutine(PlaceClientWhenReady(clientId));

        private void OnLoadComplete(ulong clientId, string sceneName, LoadSceneMode loadSceneMode)
        {
            if (sceneName == gameObject.scene.name)
                StartCoroutine(PlaceClientWhenReady(clientId));
        }

        private void OnSynchronizeComplete(ulong clientId) => StartCoroutine(PlaceClientWhenReady(clientId));

        private IEnumerator PlaceClientWhenReady(ulong clientId)
        {
            for (var frame = 0; frame < 120; frame++)
            {
                if (_manager == null || !_manager.IsServer)
                    yield break;

                if (!_manager.ConnectedClients.TryGetValue(clientId, out var client) || client.PlayerObject == null ||
                    !client.PlayerObject.TryGetComponent<NetworkPlayerController>(out var player))
                {
                    yield return null;
                    continue;
                }

                var point = ResolveSpawnPoint(clientId, out var shipSpawnSource);
                var send = new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
                };
                var usePlatformSpace = shipSpawnSource != null && shipSpawnSource.IsSpawned;
                var platformReference = usePlatformSpace
                    ? new NetworkObjectReference(shipSpawnSource.NetworkObject)
                    : new NetworkObjectReference((NetworkObject)null);
                var platformLocalPosition = usePlatformSpace
                    ? shipSpawnSource.transform.InverseTransformPoint(point.position)
                    : Vector3.zero;
                var platformLocalRotation = usePlatformSpace
                    ? Quaternion.Inverse(shipSpawnSource.transform.rotation) * point.rotation
                    : Quaternion.identity;
                player.TeleportOwnerClientRpc(
                    _sceneBuildIndex,
                    point.position,
                    point.rotation,
                    usePlatformSpace,
                    platformReference,
                    platformLocalPosition,
                    platformLocalRotation,
                    send);
                yield break;
            }
        }

        private Transform ResolveSpawnPoint(ulong clientId, out NetworkShipController shipSpawnSource)
        {
            // A ship-owned point is preferred in gameplay scenes. Port scenes have no ship and
            // continue using the director's regular scene spawn points.
            if (_shipSpawnSource != null && _shipSpawnSource.TryGetPlayerSpawnPoint(clientId, out var shipPoint))
            {
                shipSpawnSource = _shipSpawnSource;
                return shipPoint;
            }

            shipSpawnSource = null;
            return _validPoints[(int)(clientId % (ulong)_validPoints.Length)];
        }

        private void OnDestroy()
        {
            if (_manager != null)
            {
                _manager.OnClientConnectedCallback -= OnClientConnected;
                if (_manager.SceneManager != null)
                {
                    _manager.SceneManager.OnLoadComplete -= OnLoadComplete;
                    _manager.SceneManager.OnSynchronizeComplete -= OnSynchronizeComplete;
                }
            }
        }
    }
}
