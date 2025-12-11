using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using XREngine.Networking;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine
{
    public static partial class Engine
    {
        public class ServerNetworkingManager : BaseNetworkingManager
        {
            public override bool IsServer => true;
            public override bool IsClient => false;
            public override bool IsP2P => false;

            private readonly object _playerLock = new();
            private readonly Dictionary<int, NetworkPlayerConnection> _playersByIndex = new();
            private readonly Dictionary<string, NetworkPlayerConnection> _playersByClientId = new(StringComparer.OrdinalIgnoreCase);
            private int _nextServerPlayerIndex = 1;
            private static readonly TimeSpan PlayerTimeout = TimeSpan.FromSeconds(15);
            private static readonly TimeSpan HeartbeatGrace = TimeSpan.FromSeconds(5);

            public ServerNetworkingManager() : base(peerId: "server") { }

            public void Start(
                IPAddress udpMulticastGroupIP,
                int udpMulticastPort,
                int udpRecievePort)
            {
                Debug.Out($"Starting server at udp(multicast:{udpMulticastGroupIP}:{udpMulticastPort} / recieve:{udpRecievePort})");
                StartUdpMulticastSender(udpMulticastGroupIP, udpMulticastPort);
                StartUdpReceiver(udpRecievePort);
            }

            /// <summary>
            /// Run on the server - receives from clients
            /// </summary>
            /// <param name="udpPort"></param>
            protected void StartUdpReceiver(int udpPort)
            {
                UdpClient listener = new();
                //listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
                UdpReceiver = listener;
            }

            protected override async Task SendUDP()
            {
                PruneStalePlayers();
                //Send to clients
                await ConsumeAndSendUDPQueue(UdpMulticastSender, MulticastEndPoint);
            }

            protected override void HandleStateChange(StateChangeInfo change, IPEndPoint? sender)
            {
                if (change.Type is EStateChangeType.RemoteJobRequest or EStateChangeType.RemoteJobResponse)
                {
                    base.HandleStateChange(change, sender);
                    return;
                }

                switch (change.Type)
                {
                    case EStateChangeType.PlayerJoin:
                        if (StateChangePayloadSerializer.TryDeserialize<PlayerJoinRequest>(change.Data, out var join) && sender is not null)
                            HandlePlayerJoin(join, sender);
                        break;
                    case EStateChangeType.PlayerInputSnapshot:
                        if (StateChangePayloadSerializer.TryDeserialize<PlayerInputSnapshot>(change.Data, out var snapshot))
                            HandlePlayerInputSnapshot(snapshot);
                        break;
                    case EStateChangeType.PlayerTransformUpdate:
                        if (StateChangePayloadSerializer.TryDeserialize<PlayerTransformUpdate>(change.Data, out var transformUpdate))
                            HandlePlayerTransformUpdate(transformUpdate);
                        break;
                    case EStateChangeType.PlayerLeave:
                        if (StateChangePayloadSerializer.TryDeserialize<PlayerLeaveNotice>(change.Data, out var leave) && sender is not null)
                            HandlePlayerLeave(leave);
                        break;
                    case EStateChangeType.Heartbeat:
                        if (StateChangePayloadSerializer.TryDeserialize<PlayerHeartbeat>(change.Data, out var hb) && sender is not null)
                            HandleHeartbeat(hb, sender);
                        break;
                }
            }

            private void HandlePlayerJoin(PlayerJoinRequest request, IPEndPoint sender)
            {
                if (string.IsNullOrWhiteSpace(request.ClientId))
                    request.ClientId = sender.ToString();

                if (!string.Equals(request.BuildVersion, CurrentProtocolVersion, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Out($"[Server] Client {request.ClientId} version mismatch: client={request.BuildVersion}, server={CurrentProtocolVersion}");
                    SendErrorToClient(request.ClientId, 426, "Upgrade Required", $"Server version {CurrentProtocolVersion}, client version {request.BuildVersion}.", fatal: false);
                }

                NetworkPlayerConnection connection;
                bool isNewPlayer = false;

                lock (_playerLock)
                {
                    if (!_playersByClientId.TryGetValue(request.ClientId, out connection!))
                    {
                        connection = new NetworkPlayerConnection
                        {
                            ServerPlayerIndex = _nextServerPlayerIndex++,
                            ClientId = request.ClientId
                        };
                        _playersByClientId[request.ClientId] = connection;
                        _playersByIndex[connection.ServerPlayerIndex] = connection;
                        isNewPlayer = true;
                    }

                    connection.JoinRequest = request;
                    connection.LastEndpoint = sender;
                    connection.LastHeardUtc = DateTime.UtcNow;
                }

                var assignment = new PlayerAssignment
                {
                    ServerPlayerIndex = connection.ServerPlayerIndex,
                    PawnId = Guid.Empty,
                    TransformId = Guid.Empty,
                    ClientId = connection.ClientId,
                    DisplayName = request.DisplayName,
                    World = BuildWorldDescriptor()
                };

                BroadcastStateChange(EStateChangeType.PlayerAssignment, assignment, compress: true);

                if (isNewPlayer)
                    BroadcastExistingTransforms();
            }

            private void HandlePlayerInputSnapshot(PlayerInputSnapshot snapshot)
            {
                lock (_playerLock)
                {
                    if (!_playersByIndex.TryGetValue(snapshot.ServerPlayerIndex, out var connection))
                        return;

                    connection.LastInput = snapshot;
                    connection.LastHeardUtc = DateTime.UtcNow;
                }
            }

            private void HandlePlayerTransformUpdate(PlayerTransformUpdate transform)
            {
                lock (_playerLock)
                {
                    if (!_playersByIndex.TryGetValue(transform.ServerPlayerIndex, out var connection))
                        return;

                    connection.LastTransform = transform;
                    connection.LastHeardUtc = DateTime.UtcNow;
                }

                BroadcastStateChange(EStateChangeType.PlayerTransformUpdate, transform, compress: false);
            }

            private void HandleHeartbeat(PlayerHeartbeat hb, IPEndPoint sender)
            {
                lock (_playerLock)
                {
                    if (_playersByIndex.TryGetValue(hb.ServerPlayerIndex, out var connection))
                    {
                        connection.LastHeardUtc = DateTime.UtcNow;
                        connection.LastEndpoint = sender;
                        return;
                    }

                    if (_playersByClientId.TryGetValue(hb.ClientId, out connection))
                    {
                        connection.LastHeardUtc = DateTime.UtcNow;
                        connection.LastEndpoint = sender;
                    }
                }
            }

            private void BroadcastExistingTransforms()
            {
                List<PlayerTransformUpdate> pending;
                lock (_playerLock)
                {
                    pending = _playersByIndex.Values
                        .Where(p => p.LastTransform is not null)
                        .Select(p => p.LastTransform!)
                        .ToList();
                }

                foreach (var transform in pending)
                    BroadcastStateChange(EStateChangeType.PlayerTransformUpdate, transform, compress: false);
            }

            private void HandlePlayerLeave(PlayerLeaveNotice leave)
            {
                NetworkPlayerConnection? connection;
                lock (_playerLock)
                {
                    if (!_playersByIndex.TryGetValue(leave.ServerPlayerIndex, out connection))
                        return;

                    _playersByIndex.Remove(leave.ServerPlayerIndex);
                    _playersByClientId.Remove(connection.ClientId);
                }

                BroadcastPlayerLeave(connection, leave.Reason ?? "Client requested leave");
                SendErrorToClient(connection.ClientId, 499, "Client Closed", leave.Reason ?? "Client requested leave", connection.ServerPlayerIndex, fatal: false);
            }

            public IReadOnlyList<ServerConnectionInfo> GetConnectionsSnapshot()
            {
                lock (_playerLock)
                {
                    return _playersByIndex.Values
                        .Select(p => new ServerConnectionInfo(p.ServerPlayerIndex, p.ClientId, p.LastHeardUtc))
                        .ToList();
                }
            }

            public void KickClient(int serverPlayerIndex, string reason = "Kicked by operator")
            {
                NetworkPlayerConnection? connection;
                lock (_playerLock)
                {
                    if (!_playersByIndex.TryGetValue(serverPlayerIndex, out connection))
                        return;

                    _playersByIndex.Remove(serverPlayerIndex);
                    _playersByClientId.Remove(connection.ClientId);
                }

                BroadcastPlayerLeave(connection, reason);
                SendErrorToClient(connection.ClientId, 403, "Kicked", reason, connection.ServerPlayerIndex, fatal: true);
            }

            private WorldSyncDescriptor BuildWorldDescriptor()
            {
                XRWorldInstance? worldInstance = ResolvePrimaryWorldInstance();
                if (worldInstance is null)
                    return new WorldSyncDescriptor();

                XRWorld? world = worldInstance.TargetWorld;
                return new WorldSyncDescriptor
                {
                    WorldName = world?.Name,
                    GameModeType = worldInstance.GameMode?.GetType().FullName,
                    SceneNames = world?.Scenes.Select(s => s.Name ?? string.Empty).Where(static n => !string.IsNullOrWhiteSpace(n)).ToArray() ?? Array.Empty<string>()
                };
            }

            private static XRWorldInstance? ResolvePrimaryWorldInstance()
            {
                foreach (var window in Windows)
                {
                    if (window?.TargetWorldInstance is not null)
                        return window.TargetWorldInstance;
                }

                return XRWorldInstance.WorldInstances.Values.FirstOrDefault();
            }

            private sealed class NetworkPlayerConnection
            {
                public required int ServerPlayerIndex { get; init; }
                public required string ClientId { get; init; }
                public PlayerJoinRequest? JoinRequest { get; set; }
                public PlayerInputSnapshot? LastInput { get; set; }
                public PlayerTransformUpdate? LastTransform { get; set; }
                public IPEndPoint? LastEndpoint { get; set; }
                public DateTime LastHeardUtc { get; set; }
            }

            public readonly record struct ServerConnectionInfo(int ServerPlayerIndex, string ClientId, DateTime LastHeardUtc);

            private void PruneStalePlayers()
            {
                List<NetworkPlayerConnection> stale;
                lock (_playerLock)
                {
                    var now = DateTime.UtcNow;
                    stale = _playersByIndex.Values
                        .Where(p => now - p.LastHeardUtc > PlayerTimeout + HeartbeatGrace)
                        .ToList();

                    foreach (var player in stale)
                    {
                        _playersByIndex.Remove(player.ServerPlayerIndex);
                        _playersByClientId.Remove(player.ClientId);
                    }
                }

                foreach (var player in stale)
                {
                    Debug.Out($"[Server] Dropped stale player {player.ClientId} (index {player.ServerPlayerIndex}).");
                    BroadcastPlayerLeave(player, "Heartbeat timeout");
                    SendErrorToClient(player.ClientId, 408, "Request Timeout", "Heartbeat timed out.", player.ServerPlayerIndex, fatal: true);
                }
            }

            private void BroadcastPlayerLeave(NetworkPlayerConnection connection, string reason)
            {
                var leave = new PlayerLeaveNotice
                {
                    ServerPlayerIndex = connection.ServerPlayerIndex,
                    ClientId = connection.ClientId,
                    Reason = reason
                };

                BroadcastStateChange(EStateChangeType.PlayerLeave, leave, compress: false);
            }

            private void SendErrorToClient(string clientId, int statusCode, string title, string detail, int? serverPlayerIndex = null, bool fatal = false, string? requestId = null)
            {
                var error = new ServerErrorMessage
                {
                    StatusCode = statusCode,
                    Title = title,
                    Detail = detail,
                    ClientId = clientId,
                    ServerPlayerIndex = serverPlayerIndex,
                    RequestId = requestId,
                    Fatal = fatal
                };

                BroadcastServerError(error, compress: true, resendOnFailedAck: false);
            }
        }
    }
}
