using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UdpSocket = System.Net.Sockets.Socket;
using Steamworks;
using Steamworks.Data;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.NetCode;
using Unity.Networking.Transport;
using UnityEngine;
using WaveByWave.Items;

namespace WaveByWave.Networking
{
    /// <summary>
    /// Installs a SteamNetworkingSockets-backed Unity Transport interface before Netcode for
    /// Entities creates its client and server drivers. NGO keeps using FacepunchTransport on its
    /// own virtual port; Ghost traffic uses a second Steam relay port.
    /// </summary>
    [UnityEngine.Scripting.Preserve]
    public sealed class SteamNetcodeBootstrap : ClientServerBootstrap
    {
        public override bool Initialize(string defaultWorldName)
        {
            NetworkStreamReceiveSystem.DriverConstructor = new SteamNetworkDriverConstructor();
            return base.Initialize(defaultWorldName);
        }
    }

    internal sealed class SteamNetworkDriverConstructor : INetworkStreamDriverConstructor
    {
        public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            var networkInterface = new SteamNetworkInterface(false).WrapToUnmanaged();
            var driver = DefaultDriverBuilder.CreateClientNetworkDriver(
                networkInterface, DefaultDriverBuilder.GetNetworkClientSettings());
            driverStore.RegisterDriver(TransportType.Socket, driver);
            netDebug.DebugLog("[Steam NFE] Client driver created.");
        }

        public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            var networkInterface = new SteamNetworkInterface(true).WrapToUnmanaged();
            var driver = DefaultDriverBuilder.CreateServerNetworkDriver(
                networkInterface, DefaultDriverBuilder.GetNetworkServerSettings());
            driverStore.RegisterDriver(TransportType.Socket, driver);
            netDebug.DebugLog("[Steam NFE] Server driver created.");
        }
    }

    /// <summary>
    /// A connected NFE peer must be marked in-game before Ghost snapshots are exchanged. The NGO
    /// lobby is already the authority/authentication boundary, so both ECS worlds can enter the
    /// snapshot stream as soon as UTP assigns a NetworkId.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct SteamNetworkAutoInGameSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (_, entity) in SystemAPI.Query<RefRO<NetworkId>>()
                         .WithAll<NetworkStreamConnection>()
                         .WithNone<NetworkStreamInGame>()
                         .WithEntityAccess())
                commands.AddComponent<NetworkStreamInGame>(entity);
            commands.Playback(state.EntityManager);
            commands.Dispose();
        }
    }

    /// <summary>Session-level control used by the NGO lobby coordinator.</summary>
    public static class SteamNetcodeSession
    {
        private enum SessionTransport
        {
            None,
            Steam,
            LocalUdp
        }

        private static readonly List<SteamDatagramBridge> Bridges = new();
        private static int _virtualPort;
        private static ulong _hostSteamId;
        private static string _localAddress = "127.0.0.1";
        private static SessionTransport _transport;
        private static readonly NetworkEndpoint LocalHostClientEndpoint =
            NetworkEndpoint.Parse("fdff::1", 1, NetworkFamily.Ipv6);

        internal static void Register(SteamDatagramBridge bridge)
        {
            Bridges.Add(bridge);
            if (_transport == SessionTransport.LocalUdp)
                bridge.ConfigureLocal(_virtualPort, _localAddress);
            else
                bridge.ConfigureSteam(_virtualPort, _hostSteamId);
        }

        internal static void Unregister(SteamDatagramBridge bridge) => Bridges.Remove(bridge);

        public static void Configure(int virtualPort, ulong hostSteamId)
        {
            _transport = SessionTransport.Steam;
            _virtualPort = virtualPort;
            _hostSteamId = hostSteamId;
            foreach (var bridge in Bridges)
                bridge.ConfigureSteam(virtualPort, hostSteamId);
        }

        public static void ConfigureLocal(int port, string address)
        {
            _transport = SessionTransport.LocalUdp;
            _virtualPort = port;
            _hostSteamId = 0;
            _localAddress = string.IsNullOrWhiteSpace(address) ? "127.0.0.1" : address;
            foreach (var bridge in Bridges)
                bridge.ConfigureLocal(port, _localAddress);
        }

        public static void StartHostAndLocalClient()
        {
            if (!SteamClient.IsValid)
                throw new InvalidOperationException("Steam API is unavailable for Netcode for Entities.");

            ConfigureWorld(ClientServerBootstrap.ServerWorld, true);
            ConfigureWorld(ClientServerBootstrap.ClientWorld, false);
        }

        public static bool TryPrepareHost(out string error)
        {
            return TryPrepareServer(out error);
        }

        public static bool TryPrepareLocalHost(out string error) => TryPrepareServer(out error);

        private static bool TryPrepareServer(out string error)
        {
            var foundServer = false;
            foreach (var bridge in Bridges)
            {
                if (!bridge.IsServer)
                    continue;
                foundServer = true;
                if (!bridge.StartServer())
                {
                    error = bridge.LastError;
                    return false;
                }
            }

            error = foundServer ? null : "Netcode for Entities server world is unavailable.";
            return foundServer;
        }

        public static void StartClient()
        {
            if (!SteamClient.IsValid)
                throw new InvalidOperationException("Steam API is unavailable for Netcode for Entities.");

            ConfigureWorld(ClientServerBootstrap.ClientWorld, false);
        }

        public static void StartLocalHostAndClient()
        {
            if (_transport != SessionTransport.LocalUdp)
                throw new InvalidOperationException("Local Netcode for Entities transport is not configured.");

            ConfigureWorld(ClientServerBootstrap.ServerWorld, true);
            ConfigureWorld(ClientServerBootstrap.ClientWorld, false);
        }

        public static void StartLocalClient()
        {
            if (_transport != SessionTransport.LocalUdp)
                throw new InvalidOperationException("Local Netcode for Entities transport is not configured.");

            ConfigureWorld(ClientServerBootstrap.ClientWorld, false);
        }

        public static void Shutdown()
        {
            LootStressTest.ClearLocal();
            DisconnectWorld(ClientServerBootstrap.ClientWorld);
            foreach (var bridge in Bridges)
                bridge.Close();
            _virtualPort = 0;
            _hostSteamId = 0;
            _transport = SessionTransport.None;
        }

        private static void ConfigureWorld(World world, bool listen)
        {
            if (world == null || !world.IsCreated)
            {
                Debug.LogWarning($"[Steam NFE] {(listen ? "Server" : "Client")} world is unavailable.");
                return;
            }

            var manager = world.EntityManager;
            if (listen)
            {
                var request = manager.CreateEntity();
                manager.AddComponentData(request, new NetworkStreamRequestListen
                {
                    Endpoint = NetworkEndpoint.AnyIpv4.WithPort(ToPort(_virtualPort))
                });
            }
            else
            {
                var request = manager.CreateEntity();
                manager.AddComponentData(request, new NetworkStreamRequestConnect
                {
                    Endpoint = _transport == SessionTransport.LocalUdp
                        ? ResolveEndpoint(_localAddress, ToPort(_virtualPort))
                        : NetworkEndpoint.LoopbackIpv4.WithPort(ToPort(_virtualPort))
                });
            }
        }

        private static NetworkEndpoint ResolveEndpoint(string address, ushort port)
        {
            if (NetworkEndpoint.TryParse(address, port, out var endpoint))
                return endpoint;

            foreach (var candidate in Dns.GetHostAddresses(address))
                if (candidate.AddressFamily == AddressFamily.InterNetwork &&
                    NetworkEndpoint.TryParse(candidate.ToString(), port, out endpoint))
                    return endpoint;

            throw new InvalidOperationException($"Не удалось определить локальный адрес NFE: {address}");
        }

        private static void DisconnectWorld(World world)
        {
            if (world == null || !world.IsCreated)
                return;

            var manager = world.EntityManager;
            using var query = manager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
            using var connections = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            foreach (var connection in connections)
                if (!manager.HasComponent<NetworkStreamRequestDisconnect>(connection))
                    manager.AddComponentData(connection, new NetworkStreamRequestDisconnect());
        }

        private static ushort ToPort(int value) => (ushort)Math.Clamp(value, 1, ushort.MaxValue);

        internal static bool IsLocalHost => SteamClient.IsValid && _hostSteamId == SteamClient.SteamId;

        internal static bool IsLocalUdp => _transport == SessionTransport.LocalUdp;

        internal static bool RouteLocalClientToServer(byte[] payload)
        {
            if (!IsLocalHost)
                return false;
            var server = Bridges.Find(bridge => bridge.IsServer);
            if (server == null)
                return false;
            server.Enqueue(payload, LocalHostClientEndpoint);
            return true;
        }

        internal static bool RouteServerToLocalClient(NetworkEndpoint destination, byte[] payload,
            NetworkEndpoint serverEndpoint)
        {
            if (!IsLocalHost || destination != LocalHostClientEndpoint)
                return false;
            var client = Bridges.Find(bridge => !bridge.IsServer);
            if (client == null)
                return false;
            client.Enqueue(payload, serverEndpoint);
            return true;
        }
    }

    internal struct SteamNetworkInterface : INetworkInterface
    {
        private SteamDatagramBridge _bridge;
        private NetworkEndpoint _localEndpoint;

        internal SteamNetworkInterface(bool server)
        {
            _bridge = new SteamDatagramBridge(server);
            _localEndpoint = default;
        }

        public NetworkEndpoint LocalEndpoint => _localEndpoint;

        public int Initialize(ref NetworkSettings settings, ref int packetPadding) => 0;

        public void Dispose()
        {
            _bridge?.Dispose();
            _bridge = null;
        }

        public JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dependency)
        {
            dependency.Complete();
            _bridge?.Pump();
            _bridge?.DrainInto(ref arguments);
            return default;
        }

        public JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dependency)
        {
            dependency.Complete();
            _bridge?.Flush(ref arguments);
            return default;
        }

        public int Bind(NetworkEndpoint endpoint)
        {
            _localEndpoint = endpoint;
            return 0;
        }

        public int Listen() => _bridge != null && _bridge.StartServer() ? 0 : -1;
    }

    internal sealed class SteamDatagramBridge : IDisposable, IConnectionManager, ISocketManager
    {
        private readonly struct ReceivedPacket
        {
            public readonly byte[] Payload;
            public readonly NetworkEndpoint Source;

            public ReceivedPacket(byte[] payload, NetworkEndpoint source)
            {
                Payload = payload;
                Source = source;
            }
        }

        private readonly bool _server;
        private readonly Queue<ReceivedPacket> _received = new();
        private readonly Dictionary<NetworkEndpoint, Connection> _serverConnections = new();
        private readonly byte[] _udpReceiveBuffer = new byte[ushort.MaxValue];
        private ConnectionManager _client;
        private SocketManager _listener;
        private UdpSocket _udpSocket;
        private IPEndPoint _udpServerEndpoint;
        private NetworkEndpoint _serverEndpoint;
        private int _virtualPort;
        private ulong _hostSteamId;
        private bool _localUdp;
        private bool _disposed;

        internal bool IsServer => _server;
        internal string LastError { get; private set; }

        internal SteamDatagramBridge(bool server)
        {
            _server = server;
            SteamNetcodeSession.Register(this);
        }

        internal void ConfigureSteam(int virtualPort, ulong hostSteamId)
        {
            if (!_localUdp && _virtualPort == virtualPort && _hostSteamId == hostSteamId)
                return;

            Close();
            _localUdp = false;
            _virtualPort = virtualPort;
            _hostSteamId = hostSteamId;
            _serverEndpoint = NetworkEndpoint.LoopbackIpv4.WithPort((ushort)Math.Clamp(virtualPort, 1, ushort.MaxValue));
        }

        internal void ConfigureLocal(int port, string address)
        {
            Close();
            _localUdp = true;
            _virtualPort = port;
            _hostSteamId = 0;
            _udpServerEndpoint = new IPEndPoint(ResolveIpv4(address), Math.Clamp(port, 1, ushort.MaxValue));
            _serverEndpoint = ToNetworkEndpoint(_udpServerEndpoint);
        }

        internal bool StartServer()
        {
            LastError = null;
            if (!_server)
                return true;
            if (_listener != null || _udpSocket != null)
                return true;
            if (_virtualPort <= 0)
                return false;

            try
            {
                if (SteamNetcodeSession.IsLocalUdp)
                {
                    _udpSocket = new UdpSocket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                    {
                        Blocking = false,
                        ExclusiveAddressUse = true
                    };
                    _udpSocket.Bind(new IPEndPoint(IPAddress.Any, _virtualPort));
                    Debug.Log($"[Local NFE] Listening on UDP port {_virtualPort}.");
                    return true;
                }

                if (!SteamClient.IsValid)
                    return false;
                SteamNetworkingUtils.InitRelayNetworkAccess();
                _listener = SteamNetworkingSockets.CreateRelaySocket<SocketManager>(_virtualPort);
                _listener.Interface = this;
                Debug.Log($"[Steam NFE] Listening on relay virtual port {_virtualPort}.");
                return true;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                Debug.LogError($"[Steam NFE] Could not listen on relay port {_virtualPort}: {exception.Message}");
                return false;
            }
        }

        private bool EnsureClient()
        {
            if (_server || _client != null || _udpSocket != null)
                return true;
            if (_virtualPort <= 0)
                return false;

            try
            {
                if (SteamNetcodeSession.IsLocalUdp)
                {
                    _udpSocket = new UdpSocket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                    {
                        Blocking = false
                    };
                    _udpSocket.Bind(new IPEndPoint(IPAddress.Any, 0));
                    return true;
                }

                if (!SteamClient.IsValid || _hostSteamId == 0)
                    return false;
                SteamNetworkingUtils.InitRelayNetworkAccess();
                _client = SteamNetworkingSockets.ConnectRelay<ConnectionManager>(_hostSteamId, _virtualPort);
                _client.Interface = this;
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Steam NFE] Could not connect to {_hostSteamId}:{_virtualPort}: {exception.Message}");
                return false;
            }
        }

        internal void Pump()
        {
            if (_disposed)
                return;
            if (SteamNetcodeSession.IsLocalUdp)
            {
                PumpUdp();
                return;
            }
            if (!SteamClient.IsValid)
                return;
            _client?.Receive();
            _listener?.Receive();
        }

        internal unsafe void DrainInto(ref ReceiveJobArguments arguments)
        {
            while (_received.Count > 0)
            {
                if (!arguments.ReceiveQueue.EnqueuePacket(out var packet))
                {
                    arguments.ReceiveResult.ErrorCode = -10; // UTP NetworkReceiveQueueFull
                    return;
                }

                var received = _received.Dequeue();
                if (received.Payload.Length > packet.BytesAvailableAtEnd)
                {
                    packet.Drop();
                    continue;
                }

                fixed (byte* payload = received.Payload)
                    packet.AppendToPayload(payload, received.Payload.Length);
                packet.EndpointRef = received.Source;
            }
        }

        internal unsafe void Flush(ref SendJobArguments arguments)
        {
            if (SteamNetcodeSession.IsLocalUdp)
            {
                FlushUdp(ref arguments);
                return;
            }

            if (!_server && !SteamNetcodeSession.IsLocalHost && !EnsureClient())
                return;

            var count = arguments.SendQueue.Count;
            for (var index = 0; index < count; index++)
            {
                var packet = arguments.SendQueue[index];
                if (packet.Length <= 0)
                    continue;

                var payload = new byte[packet.Length];
                fixed (byte* destination = payload)
                    UnsafeUtility.MemCpy(destination, (byte*)packet.GetUnsafePayloadPtr() + packet.Offset, packet.Length);

                if (_server)
                {
                    var routedLocally = SteamNetcodeSession.RouteServerToLocalClient(
                        packet.EndpointRef, payload, _serverEndpoint);
                    if (!routedLocally && _serverConnections.TryGetValue(packet.EndpointRef, out var connection))
                        connection.SendMessage(payload, SendType.Unreliable);
                }
                else
                {
                    if (!SteamNetcodeSession.RouteLocalClientToServer(payload))
                    {
                        // The local-host bridge can disappear while worlds are reconnecting or
                        // shutting down. Never dereference a missing remote client as a fallback;
                        // recreate it when possible and otherwise drop this unreliable packet.
                        if (!EnsureClient() || _client == null)
                        {
                            packet.Drop();
                            continue;
                        }
                        _client.Connection.SendMessage(payload, SendType.Unreliable);
                    }
                }

                packet.Drop();
            }
        }

        internal void Close()
        {
            try { _client?.Close(); }
            catch (Exception exception) { Debug.LogWarning($"[Steam NFE] Client close: {exception.Message}"); }
            try { _listener?.Close(); }
            catch (Exception exception) { Debug.LogWarning($"[Steam NFE] Listener close: {exception.Message}"); }
            _client = null;
            _listener = null;
            try { _udpSocket?.Close(); }
            catch (Exception exception) { Debug.LogWarning($"[Local NFE] UDP close: {exception.Message}"); }
            _udpSocket = null;
            _serverConnections.Clear();
            _received.Clear();
        }

        private void PumpUdp()
        {
            if (_udpSocket == null)
                return;

            while (_udpSocket.Poll(0, SelectMode.SelectRead))
            {
                EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    var length = _udpSocket.ReceiveFrom(_udpReceiveBuffer, 0, _udpReceiveBuffer.Length,
                        SocketFlags.None, ref source);
                    if (length <= 0 || source is not IPEndPoint ipSource)
                        continue;
                    var payload = new byte[length];
                    Buffer.BlockCopy(_udpReceiveBuffer, 0, payload, 0, length);
                    _received.Enqueue(new ReceivedPacket(payload, ToNetworkEndpoint(ipSource)));
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock)
                {
                    break;
                }
            }
        }

        private unsafe void FlushUdp(ref SendJobArguments arguments)
        {
            if (!EnsureClient() || _udpSocket == null)
                return;

            var count = arguments.SendQueue.Count;
            for (var index = 0; index < count; index++)
            {
                var packet = arguments.SendQueue[index];
                if (packet.Length <= 0)
                    continue;

                var payload = new byte[packet.Length];
                fixed (byte* destination = payload)
                    UnsafeUtility.MemCpy(destination, (byte*)packet.GetUnsafePayloadPtr() + packet.Offset, packet.Length);

                try
                {
                    var destination = _server ? ToIpEndpoint(packet.EndpointRef) : _udpServerEndpoint;
                    if (destination != null)
                        _udpSocket.SendTo(payload, destination);
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock ||
                    exception.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
                {
                    // This is an unreliable datagram transport; a saturated packet may be dropped.
                }
                packet.Drop();
            }
        }

        private static IPAddress ResolveIpv4(string address)
        {
            if (IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
                return parsed;
            foreach (var candidate in Dns.GetHostAddresses(address))
                if (candidate.AddressFamily == AddressFamily.InterNetwork)
                    return candidate;
            throw new InvalidOperationException($"Не удалось определить IPv4-адрес: {address}");
        }

        private static NetworkEndpoint ToNetworkEndpoint(IPEndPoint endpoint) =>
            NetworkEndpoint.Parse(endpoint.Address.ToString(), (ushort)endpoint.Port);

        private static IPEndPoint ToIpEndpoint(NetworkEndpoint endpoint)
        {
            using var bytes = endpoint.GetRawAddressBytes();
            return new IPEndPoint(new IPAddress(bytes.ToArray()), endpoint.Port);
        }

        internal void Enqueue(byte[] payload, NetworkEndpoint source) =>
            _received.Enqueue(new ReceivedPacket(payload, source));

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Close();
            SteamNetcodeSession.Unregister(this);
        }

        private static NetworkEndpoint EndpointFor(Connection connection)
        {
            var id = unchecked((ulong)connection.Id);
            return NetworkEndpoint.Parse($"fd00::{id:x}", 1, NetworkFamily.Ipv6);
        }

        private static unsafe byte[] CopyPayload(IntPtr data, int size)
        {
            var payload = new byte[size];
            fixed (byte* destination = payload)
                UnsafeUtility.MemCpy(destination, (void*)data, size);
            return payload;
        }

        void IConnectionManager.OnConnecting(ConnectionInfo info) { }

        void IConnectionManager.OnConnected(ConnectionInfo info) =>
            Debug.Log($"[Steam NFE] Connected to Steam host {info.Identity.SteamId}.");

        void IConnectionManager.OnDisconnected(ConnectionInfo info) =>
            Debug.Log($"[Steam NFE] Disconnected from Steam host {info.Identity.SteamId}.");

        void IConnectionManager.OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel) =>
            _received.Enqueue(new ReceivedPacket(CopyPayload(data, size), _serverEndpoint));

        void ISocketManager.OnConnecting(Connection connection, ConnectionInfo info) => connection.Accept();

        void ISocketManager.OnConnected(Connection connection, ConnectionInfo info)
        {
            _serverConnections[EndpointFor(connection)] = connection;
            Debug.Log($"[Steam NFE] Accepted Steam user {info.Identity.SteamId}.");
        }

        void ISocketManager.OnDisconnected(Connection connection, ConnectionInfo info) =>
            _serverConnections.Remove(EndpointFor(connection));

        void ISocketManager.OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size,
            long messageNum, long recvTime, int channel)
        {
            var endpoint = EndpointFor(connection);
            _serverConnections[endpoint] = connection;
            _received.Enqueue(new ReceivedPacket(CopyPayload(data, size), endpoint));
        }
    }
}
