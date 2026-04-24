using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using XREngine.Networking;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Components;
using XREngine.Scene.Transforms;
using XREngine.Input;

namespace XREngine
{
    public class ServerNetworkingManager : BaseNetworkingManager
        {
            public override bool IsServer => true;
            public override bool IsClient => false;
            public override bool IsP2P => false;

            private readonly object _playerLock = new();
            private readonly Dictionary<int, NetworkPlayerConnection> _playersByIndex = new();
            private readonly Dictionary<string, NetworkPlayerConnection> _playersByClientId = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, NetworkPlayerConnection> _resumeByClientKey = new(StringComparer.OrdinalIgnoreCase);
            private readonly RealtimeReplicationCoordinator _replication = new();
            private readonly object _transformTargetLock = new();
            private readonly List<IPEndPoint> _transformTargets = new(16);
            private int _nextServerPlayerIndex = 1;

            public ServerNetworkingManager() : base(peerId: "server") { }

            public void Start(
                IPAddress udpMulticastGroupIP,
                int udpMulticastPort,
                int udpReceivePort)
            {
                Debug.Out($"Starting server at udp(receive/send:{udpReceivePort}; multicast fallback:{udpMulticastGroupIP}:{udpMulticastPort})");
                MulticastEndPoint = new IPEndPoint(udpMulticastGroupIP, udpMulticastPort);
                StartUdpReceiver(udpReceivePort);
            }

            /// <summary>
            /// Run on the server - receives from clients
            /// </summary>
            /// <param name="udpPort"></param>

            private void HandlePlayerLeave(PlayerLeaveNotice leave, IPEndPoint sender)
                => HandlePlayerLeave(leave);
            protected void StartUdpReceiver(int udpPort)
            {
                UdpClient listener = new();
                //listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
                UdpReceiver = listener;
                UdpMulticastSender = listener;
            }

            protected override async Task SendUDP()
            {
                _replication.AdvanceServerTick();
                PruneStalePlayers();
                //Send to clients
                await ConsumeAndSendUDPQueues(UdpMulticastSender);
            }

            protected override void CollectUdpSendTargets(List<IPEndPoint> targets)
            {
                lock (_playerLock)
                {
                    foreach (NetworkPlayerConnection connection in _playersByIndex.Values)
                    {
                        if (connection.LastEndpoint is not null)
                            targets.Add(connection.LastEndpoint);
                    }
                }
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
                        if (!StateChangePayloadSerializer.TryDeserialize<PlayerJoinRequest>(change.Data, out var join) || join is null || sender is null)
                            break;
                        HandlePlayerJoin(join, sender);
                        break;
                    case EStateChangeType.PlayerInputSnapshot:
                        if (!StateChangePayloadSerializer.TryDeserialize<PlayerInputSnapshot>(change.Data, out var snapshot) || snapshot is null)
                            break;
                        HandlePlayerInputSnapshot(snapshot);
                        break;
                    case EStateChangeType.PlayerTransformUpdate:
                        if (!StateChangePayloadSerializer.TryDeserialize<PlayerTransformUpdate>(change.Data, out var transformUpdate) || transformUpdate is null)
                            break;
                        HandlePlayerTransformUpdate(transformUpdate);
                        break;
                    case EStateChangeType.PlayerLeave:
                        if (!StateChangePayloadSerializer.TryDeserialize<PlayerLeaveNotice>(change.Data, out var leave) || leave is null || sender is null)
                            break;
                        HandlePlayerLeave(leave);
                        break;
                    case EStateChangeType.Heartbeat:
                        if (!StateChangePayloadSerializer.TryDeserialize<PlayerHeartbeat>(change.Data, out var hb) || hb is null || sender is null)
                            break;
                        HandleHeartbeat(hb, sender);
                        break;
                    case EStateChangeType.HumanoidPoseFrame:
                        if (!StateChangePayloadSerializer.TryDeserialize<HumanoidPoseFrame>(change.Data, out var pose) || pose is null)
                            break;
                        HandleHumanoidPoseFrame(pose);
                        break;
                    case EStateChangeType.AuthorityLeaseUpdate:
                    case EStateChangeType.ClockSync:
                    case EStateChangeType.ReplicationSnapshot:
                    case EStateChangeType.ReplicationDelta:
                        base.HandleStateChange(change, sender);
                        break;
                }
            }

            private void HandlePlayerJoin(PlayerJoinRequest request, IPEndPoint sender)
            {
                if (string.IsNullOrWhiteSpace(request.ClientId))
                    request.ClientId = sender.ToString();

                NetworkPlayerConnection connection;
                bool isNewPlayer = false;
                ServerJoinAdmissionResult? joinAdmission = Engine.ServerJoinAdmissionResolver?.Invoke(request);
                if (joinAdmission is { FailureReason: not AdmissionFailureReason.None })
                {
                    SendJoinAdmissionFailure(request.ClientId, joinAdmission.FailureReason, joinAdmission.Message, sender);
                    return;
                }

                ServerSessionContext? resolvedSession = joinAdmission?.SessionContext ?? Engine.ServerSessionResolver?.Invoke(request);
                Guid? requestedSessionId = request.SessionId;
                XRWorldInstance? resolvedWorldInstance = resolvedSession?.WorldInstance ?? ResolvePrimaryWorldInstance();
                WorldAssetIdentity? serverWorldAsset = resolvedSession?.WorldAsset ?? CreateLocalWorldAsset(resolvedWorldInstance);
                AdmissionFailureReason validationFailure = RealtimeAdmissionValidator.ValidateBuildAndWorld(
                    request,
                    serverWorldAsset,
                    CurrentProtocolVersion,
                    out string validationMessage);
                if (validationFailure != AdmissionFailureReason.None)
                {
                    SendJoinAdmissionFailure(request.ClientId, validationFailure, validationMessage, sender);
                    return;
                }

                lock (_playerLock)
                {
                    if (!_playersByClientId.TryGetValue(request.ClientId, out connection!))
                    {
                        string resumeKey = CreateResumeKey(resolvedSession?.SessionId ?? requestedSessionId, request.ClientId);
                        if (!string.IsNullOrEmpty(resumeKey)
                            && _resumeByClientKey.TryGetValue(resumeKey, out NetworkPlayerConnection? resumable)
                            && resumable.ResumeUntilUtc > DateTime.UtcNow)
                        {
                            connection = resumable;
                            _resumeByClientKey.Remove(resumeKey);
                        }
                        else
                        {
                            connection = new NetworkPlayerConnection
                            {
                                ServerPlayerIndex = _nextServerPlayerIndex++,
                                ClientId = request.ClientId
                            };
                            isNewPlayer = true;
                        }

                        connection.IsResuming = !isNewPlayer;
                        if (connection.Budget is null)
                        {
                            double nowUtc = GetUtcSeconds();
                            connection.Budget = new NetworkBandwidthBudget(MultiplayerRuntimePolicy.DefaultReplicationBytesPerSecond, nowUtc);
                        }

                        _playersByClientId[request.ClientId] = connection;
                        _playersByIndex[connection.ServerPlayerIndex] = connection;
                    }

                    connection.SessionId = resolvedSession?.SessionId
                        ?? requestedSessionId
                        ?? (connection.SessionId != Guid.Empty ? connection.SessionId : Guid.NewGuid());
                    connection.WorldInstance = resolvedWorldInstance;
                    connection.WorldAsset = serverWorldAsset;
                    connection.LastEndpoint = sender;
                    if (connection.WorldInstance is null)
                    {
                        SendErrorToClient(request.ClientId, 500, "No World", "Server could not resolve a world instance for this connection.", null, fatal: true, target: sender);
                        return;
                    }

                    EnsureServerPawn(connection, request.DisplayName);
                    EnsureAuthorityLease(connection);

                    connection.JoinRequest = request;
                    connection.LastHeardUtc = DateTime.UtcNow;
                    connection.ResumeUntilUtc = null;
                }

                var assignment = new PlayerAssignment
                {
                    ServerPlayerIndex = connection.ServerPlayerIndex,
                    PlayerEntityId = connection.NetworkEntityId,
                    PawnId = connection.Pawn?.ID ?? Guid.Empty,
                    TransformId = connection.TransformId,
                    ClientId = connection.ClientId,
                    DisplayName = request.DisplayName,
                    World = BuildWorldDescriptor(connection.WorldInstance, connection.WorldAsset),
                    SessionId = connection.SessionId,
                    IsAuthoritative = true,
                    AuthorityLease = connection.AuthorityLease?.Clone(),
                    ServerTickId = _replication.CurrentServerTickId,
                    ServerTimeUtc = GetUtcSeconds()
                };

                BroadcastStateChange(EStateChangeType.PlayerAssignment, assignment, compress: true);
                if (assignment.AuthorityLease is not null)
                    BroadcastAuthorityLeaseUpdate(assignment.AuthorityLease);
                Debug.Networking(
                    "[Server] Accepted realtime join client={0}; playerIndex={1}; entity={2}; session={3}; world={4}",
                    connection.ClientId,
                    connection.ServerPlayerIndex,
                    connection.NetworkEntityId,
                    connection.SessionId,
                    RealtimeJoinHandoff.DescribeWorldAsset(connection.WorldAsset));
                Engine.ServerPlayerConnected?.Invoke(CreatePlayerEvent(connection));

                if (isNewPlayer)
                    BroadcastExistingTransforms();
            }

            private void EnsureServerPawn(NetworkPlayerConnection connection, string? displayName)
            {
                if (connection.WorldInstance is null)
                    return;

                if (connection.Pawn is { IsDestroyed: false })
                {
                    connection.TransformId = connection.TransformId == Guid.Empty
                        ? connection.Pawn.SceneNode?.Transform?.ID ?? Guid.Empty
                        : connection.TransformId;
                    connection.NetworkEntityId = CreateEntityId(connection);
                    return;
                }

                var worldInstance = connection.WorldInstance;
                worldInstance.GameMode ??= new CustomGameMode { WorldInstance = worldInstance };

                connection.Pawn = worldInstance.GameMode.CreateDefaultPawn(ELocalPlayerIndex.One)
                    ?? CreateFallbackPawn(worldInstance, connection.ServerPlayerIndex, displayName);

                if (connection.Pawn is not null)
                {
                    connection.TransformId = connection.Pawn.SceneNode?.Transform?.ID ?? Guid.Empty;
                    connection.NetworkEntityId = CreateEntityId(connection);
                    var controller = Engine.State.InstantiateRemoteController(connection.ServerPlayerIndex);
                    if (controller is not null)
                    {
                        controller.ControlledPawnComponent = connection.Pawn;
                        connection.Pawn.Controller = controller;
                        if (!Engine.State.RemotePlayers.Contains(controller))
                            Engine.State.RemotePlayers.Add(controller);
                    }
                }
            }

            private static PawnComponent? CreateFallbackPawn(XRWorldInstance worldInstance, int serverPlayerIndex, string? displayName)
            {
                var nodeName = string.IsNullOrWhiteSpace(displayName) ? $"ServerPlayer_{serverPlayerIndex}" : displayName!;
                var node = new SceneNode(worldInstance, nodeName);
                return node.AddComponent<FlyingCameraPawnComponent>();
            }

            private void HandlePlayerInputSnapshot(PlayerInputSnapshot snapshot)
            {
                double nowUtc = GetUtcSeconds();
                lock (_playerLock)
                {
                    if (!_playersByIndex.TryGetValue(snapshot.ServerPlayerIndex, out var connection))
                        return;

                    if (connection.SessionId != Guid.Empty && snapshot.SessionId != Guid.Empty && snapshot.SessionId != connection.SessionId)
                        return;

                    snapshot.SessionId = connection.SessionId;
                    if (snapshot.EntityId.IsEmpty)
                        snapshot.EntityId = connection.NetworkEntityId;
                    if (!ValidateAuthority(connection, snapshot.EntityId, nowUtc, out NetworkAuthorityRevocationReason failureReason))
                    {
                        SendErrorToClient(connection.ClientId, 403, "Authority Rejected", $"Input rejected: {failureReason}.", connection.ServerPlayerIndex, fatal: false);
                        return;
                    }

                    connection.InputBufferDepth = _replication.BufferInput(snapshot, nowUtc);
                    connection.LastProcessedInputSequence = snapshot.InputSequence;
                    connection.LastInput = snapshot;
                    connection.LastHeardUtc = DateTime.UtcNow;
                }
            }

            private void HandlePlayerTransformUpdate(PlayerTransformUpdate transform)
            {
                NetworkPlayerConnection? connection;
                double nowUtc = GetUtcSeconds();
                lock (_playerLock)
                {
                    if (!_playersByIndex.TryGetValue(transform.ServerPlayerIndex, out connection))
                        return;

                    if (connection.SessionId != Guid.Empty && transform.SessionId != Guid.Empty && transform.SessionId != connection.SessionId)
                        return;

                    if (transform.EntityId.IsEmpty)
                        transform.EntityId = connection.NetworkEntityId;
                    if (!ValidateAuthority(connection, transform.EntityId, nowUtc, out NetworkAuthorityRevocationReason failureReason))
                    {
                        SendErrorToClient(connection.ClientId, 403, "Authority Rejected", $"Transform rejected: {failureReason}.", connection.ServerPlayerIndex, fatal: false);
                        return;
                    }

                    transform.SessionId = transform.SessionId == Guid.Empty ? connection.SessionId : transform.SessionId;
                    transform = _replication.StampAuthoritativeTransform(transform, nowUtc);
                    connection.LastTransform = transform;
                    connection.LastHeardUtc = DateTime.UtcNow;
                    connection.RelevanceCenter = transform.Translation;
                }

                if (connection is null)
                    return;

                if (connection.Pawn?.Controller is IPawnController remoteController)
                    remoteController.ApplyNetworkTransform(transform);

                SendAuthoritativeTransformUpdate(transform, connection, nowUtc);
            }

            private void HandleHeartbeat(PlayerHeartbeat hb, IPEndPoint sender)
            {
                ServerSessionPlayerEvent? playerEvent = null;
                ClockSyncMessage? clockSync = null;
                double receiveUtc = GetUtcSeconds();
                lock (_playerLock)
                {
                    if (_playersByIndex.TryGetValue(hb.ServerPlayerIndex, out var connection))
                    {
                        if (connection.SessionId != Guid.Empty && hb.SessionId.HasValue && hb.SessionId.Value != connection.SessionId)
                            return;

                        connection.LastHeardUtc = DateTime.UtcNow;
                        connection.LastEndpoint = sender;
                        playerEvent = CreatePlayerEvent(connection);
                        clockSync = CreateClockSync(connection, hb, receiveUtc);
                    }
                    else if (_playersByClientId.TryGetValue(hb.ClientId, out connection))
                    {
                        if (connection.SessionId != Guid.Empty && hb.SessionId.HasValue && hb.SessionId.Value != connection.SessionId)
                            return;

                        connection.LastHeardUtc = DateTime.UtcNow;
                        connection.LastEndpoint = sender;
                        playerEvent = CreatePlayerEvent(connection);
                        clockSync = CreateClockSync(connection, hb, receiveUtc);
                    }
                }

                if (playerEvent is not null)
                    Engine.ServerPlayerHeartbeatObserved?.Invoke(playerEvent);
                if (clockSync is not null)
                    SendClockSyncTo(sender, clockSync);
            }

            private void HandleHumanoidPoseFrame(HumanoidPoseFrame frame)
            {
                NetworkPlayerConnection? connection;
                double nowUtc = GetUtcSeconds();
                lock (_playerLock)
                {
                    if (string.IsNullOrWhiteSpace(frame.SourceClientId)
                        || !_playersByClientId.TryGetValue(frame.SourceClientId, out connection))
                    {
                        return;
                    }

                    if (connection.SessionId != Guid.Empty && frame.SessionId != Guid.Empty && frame.SessionId != connection.SessionId)
                        return;

                    if (frame.EntityIds.Length == 0)
                        frame.EntityIds = [connection.NetworkEntityId];

                    foreach (NetworkEntityId entityId in frame.EntityIds)
                    {
                        if (!ValidateAuthority(connection, entityId, nowUtc, out _))
                            return;
                    }

                    frame.SessionId = connection.SessionId;
                    frame.ServerTickId = _replication.CurrentServerTickId == 0 ? _replication.AdvanceServerTick() : _replication.CurrentServerTickId;
                    frame.ServerTimestampUtc = nowUtc;
                    frame.AuthorityMode = NetworkAuthorityMode.ServerAuthoritative;
                }

                base.HandleStateChange(new StateChangeInfo(EStateChangeType.HumanoidPoseFrame, StateChangePayloadSerializer.Serialize(frame)), null);
                BroadcastHumanoidPoseFrame(frame, compress: false, resendOnFailedAck: false);
            }

            private void BroadcastExistingTransforms()
            {
                List<PlayerTransformUpdate> pending;
                lock (_playerLock)
                {
                    pending = _playersByIndex.Values
                        .Where(p => p.LastTransform is not null)
                        .Select(p =>
                        {
                            var clone = p.LastTransform!;
                            clone.SessionId = p.SessionId;
                            clone.EntityId = p.NetworkEntityId;
                            return clone;
                        })
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

                    if (connection.SessionId != Guid.Empty && leave.SessionId != Guid.Empty && connection.SessionId != leave.SessionId)
                        return;

                    _playersByIndex.Remove(leave.ServerPlayerIndex);
                    _playersByClientId.Remove(connection.ClientId);
                }

                RevokeAuthorityLease(connection, NetworkAuthorityRevocationReason.OwnerLeft, leave.Reason ?? "Client requested leave");
                BroadcastPlayerLeave(connection, leave.Reason ?? "Client requested leave");
                Engine.ServerPlayerDisconnected?.Invoke(CreatePlayerEvent(connection));
                SendErrorToClient(connection.ClientId, 499, "Client Closed", leave.Reason ?? "Client requested leave", connection.ServerPlayerIndex, fatal: false, target: connection.LastEndpoint);
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

                RevokeAuthorityLease(connection, NetworkAuthorityRevocationReason.OperatorRevoked, reason);
                BroadcastPlayerLeave(connection, reason);
                Engine.ServerPlayerDisconnected?.Invoke(CreatePlayerEvent(connection));
                SendErrorToClient(connection.ClientId, 403, "Kicked", reason, connection.ServerPlayerIndex, fatal: true, target: connection.LastEndpoint);
            }

            private WorldSyncDescriptor BuildWorldDescriptor(XRWorldInstance? worldInstance, WorldAssetIdentity? asset)
            {
                XRWorldInstance? targetInstance = worldInstance ?? ResolvePrimaryWorldInstance();
                if (targetInstance is null)
                    return new WorldSyncDescriptor { Asset = asset };

                XRWorld? world = targetInstance.TargetWorld;
                return new WorldSyncDescriptor
                {
                    WorldName = world?.Name,
                    GameModeType = targetInstance.GameMode?.GetType().FullName,
                    SceneNames = world?.Scenes.Select(s => s.Name ?? string.Empty).Where(static n => !string.IsNullOrWhiteSpace(n)).ToArray() ?? Array.Empty<string>(),
                    Asset = asset ?? CreateLocalWorldAsset(targetInstance)
                };
            }

            private static XRWorldInstance? ResolvePrimaryWorldInstance()
            {
                foreach (var window in Engine.Windows)
                {
                    if (window?.TargetWorldInstance is not null)
                        return window.TargetWorldInstance;
                }

                return XRWorldInstance.WorldInstances.Values.FirstOrDefault();
            }

            private static WorldAssetIdentity? CreateLocalWorldAsset(XRWorldInstance? worldInstance)
                => worldInstance?.TargetWorld is null
                    ? null
                    : WorldAssetIdentityProvider.Create(worldInstance.TargetWorld, CurrentProtocolVersion);

            private void EnsureAuthorityLease(NetworkPlayerConnection connection)
            {
                if (connection.NetworkEntityId.IsEmpty || connection.SessionId == Guid.Empty)
                    return;

                double nowUtc = GetUtcSeconds();
                NetworkAuthorityLease? existing = _replication.GetLease(connection.NetworkEntityId);
                if (existing is not null
                    && existing.IsActive(nowUtc)
                    && existing.SessionId == connection.SessionId
                    && existing.OwnerServerPlayerIndex == connection.ServerPlayerIndex
                    && string.Equals(existing.OwnerClientId, connection.ClientId, StringComparison.OrdinalIgnoreCase))
                {
                    connection.AuthorityLease = existing;
                    return;
                }

                connection.AuthorityLease = _replication.GrantLease(
                    connection.NetworkEntityId,
                    connection.SessionId,
                    connection.ClientId,
                    connection.ServerPlayerIndex,
                    nowUtc);
            }

            private void RevokeAuthorityLease(NetworkPlayerConnection connection, NetworkAuthorityRevocationReason reason, string detail)
            {
                if (connection.NetworkEntityId.IsEmpty)
                    return;

                NetworkAuthorityLease? revoked = _replication.RevokeLease(connection.NetworkEntityId, reason, detail);
                if (revoked is null)
                    return;

                connection.AuthorityLease = revoked;
                BroadcastAuthorityLeaseUpdate(revoked);
            }

            private bool ValidateAuthority(
                NetworkPlayerConnection connection,
                NetworkEntityId entityId,
                double nowUtc,
                out NetworkAuthorityRevocationReason failureReason)
            {
                if (entityId.IsEmpty)
                    entityId = connection.NetworkEntityId;

                bool valid = _replication.TryValidateOwner(
                    entityId,
                    connection.ClientId,
                    connection.ServerPlayerIndex,
                    connection.SessionId,
                    nowUtc,
                    out NetworkAuthorityLease? lease,
                    out failureReason);

                if (lease is not null)
                    connection.AuthorityLease = lease;

                return valid;
            }

            private static NetworkEntityId CreateEntityId(NetworkPlayerConnection connection)
            {
                Guid source = connection.Pawn?.ID ?? Guid.Empty;
                if (source == Guid.Empty)
                    source = connection.TransformId;
                return NetworkEntityId.FromGuid(source);
            }

            private static string CreateResumeKey(Guid? sessionId, string? clientId)
            {
                if (sessionId is not Guid value || value == Guid.Empty || string.IsNullOrWhiteSpace(clientId))
                    return string.Empty;

                return $"{value:N}:{clientId.Trim()}";
            }

            private static double GetUtcSeconds()
                => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

            private ClockSyncMessage CreateClockSync(NetworkPlayerConnection connection, PlayerHeartbeat heartbeat, double receiveUtc)
                => new()
                {
                    SessionId = connection.SessionId,
                    ClientId = connection.ClientId,
                    ServerPlayerIndex = connection.ServerPlayerIndex,
                    ClientSendTimestampUtc = heartbeat.ClientSendTimestampUtc == 0.0d ? heartbeat.TimestampUtc : heartbeat.ClientSendTimestampUtc,
                    ServerReceiveTimestampUtc = receiveUtc,
                    ServerSendTimestampUtc = GetUtcSeconds(),
                    ServerTickId = _replication.CurrentServerTickId
                };

            private void SendAuthoritativeTransformUpdate(PlayerTransformUpdate update, NetworkPlayerConnection owner, double nowUtc)
            {
                const int estimatedTransformBytes = 160;
                lock (_transformTargetLock)
                {
                    _transformTargets.Clear();
                    lock (_playerLock)
                    {
                        foreach (NetworkPlayerConnection recipient in _playersByIndex.Values)
                        {
                            if (recipient.SessionId != owner.SessionId)
                                continue;

                            if (recipient.LastEndpoint is null)
                                continue;

                            if (recipient.Budget is null)
                                recipient.Budget = new NetworkBandwidthBudget(MultiplayerRuntimePolicy.DefaultReplicationBytesPerSecond, nowUtc);

                            if (!recipient.IsRelevant(update.Translation))
                                continue;

                            if (!recipient.Budget.TryConsume(estimatedTransformBytes, nowUtc))
                                continue;

                            _transformTargets.Add(recipient.LastEndpoint);
                        }
                    }

                    if (_transformTargets.Count > 0)
                        BroadcastStateChangeToTargets(_transformTargets, EStateChangeType.PlayerTransformUpdate, update, compress: false);

                    _transformTargets.Clear();
                }
            }

            private sealed class NetworkPlayerConnection
            {
                public required int ServerPlayerIndex { get; init; }
                public required string ClientId { get; init; }
                public Guid SessionId { get; set; }
                public XRWorldInstance? WorldInstance { get; set; }
                public WorldAssetIdentity? WorldAsset { get; set; }
                public PawnComponent? Pawn { get; set; }
                public Guid TransformId { get; set; }
                public NetworkEntityId NetworkEntityId { get; set; }
                public NetworkAuthorityLease? AuthorityLease { get; set; }
                public PlayerJoinRequest? JoinRequest { get; set; }
                public PlayerInputSnapshot? LastInput { get; set; }
                public PlayerTransformUpdate? LastTransform { get; set; }
                public IPEndPoint? LastEndpoint { get; set; }
                public DateTime LastHeardUtc { get; set; }
                public DateTime? ResumeUntilUtc { get; set; }
                public bool IsResuming { get; set; }
                public int InputBufferDepth { get; set; }
                public uint LastProcessedInputSequence { get; set; }
                public Vector3 RelevanceCenter { get; set; }
                public float RelevanceRadius { get; set; } = MultiplayerRuntimePolicy.DefaultAreaOfInterestRadius;
                public NetworkBandwidthBudget? Budget { get; set; }

                public bool IsRelevant(Vector3 entityPosition)
                    => RelevanceRadius <= 0.0f
                        || Vector3.DistanceSquared(RelevanceCenter, entityPosition) <= RelevanceRadius * RelevanceRadius;
            }

            public readonly record struct ServerConnectionInfo(int ServerPlayerIndex, string ClientId, DateTime LastHeardUtc);

            private void PruneStalePlayers()
            {
                List<NetworkPlayerConnection> stale;
                lock (_playerLock)
                {
                    var now = DateTime.UtcNow;
                    stale = _playersByIndex.Values
                        .Where(p => now - p.LastHeardUtc > MultiplayerRuntimePolicy.PlayerHeartbeatTimeout + MultiplayerRuntimePolicy.PlayerHeartbeatGracePeriod)
                        .ToList();

                    foreach (var player in stale)
                    {
                        _playersByIndex.Remove(player.ServerPlayerIndex);
                        _playersByClientId.Remove(player.ClientId);
                        player.ResumeUntilUtc = now + MultiplayerRuntimePolicy.SessionResumeWindow;
                        string resumeKey = CreateResumeKey(player.SessionId, player.ClientId);
                        if (!string.IsNullOrEmpty(resumeKey))
                            _resumeByClientKey[resumeKey] = player;
                    }

                    foreach (var resume in _resumeByClientKey.Where(static pair => pair.Value.ResumeUntilUtc <= DateTime.UtcNow).Select(static pair => pair.Key).ToList())
                        _resumeByClientKey.Remove(resume);
                }

                foreach (var player in stale)
                {
                    Debug.Out($"[Server] Dropped stale player {player.ClientId} (index {player.ServerPlayerIndex}).");
                    RevokeAuthorityLease(player, NetworkAuthorityRevocationReason.OwnerDisconnected, "Heartbeat timeout");
                    BroadcastPlayerLeave(player, "Heartbeat timeout");
                    Engine.ServerPlayerDisconnected?.Invoke(CreatePlayerEvent(player));
                    SendErrorToClient(player.ClientId, 408, "Request Timeout", "Heartbeat timed out.", player.ServerPlayerIndex, fatal: true, target: player.LastEndpoint);
                }
            }

            private static ServerSessionPlayerEvent CreatePlayerEvent(NetworkPlayerConnection connection)
                => new(connection.SessionId, connection.ClientId, connection.ServerPlayerIndex, connection.TransformId);

            private void SendJoinAdmissionFailure(string clientId, AdmissionFailureReason failureReason, string? message, IPEndPoint? target)
            {
                (int statusCode, string title, bool fatal) = failureReason switch
                {
                    AdmissionFailureReason.SessionNotFound => (404, "Session Not Found", true),
                    AdmissionFailureReason.SessionFull => (409, "Session Full", false),
                    AdmissionFailureReason.BuildVersionMismatch => (426, "Build Version Mismatch", false),
                    AdmissionFailureReason.WorldAssetMismatch => (412, "World Asset Mismatch", false),
                    AdmissionFailureReason.Unauthorized => (401, "Unauthorized", false),
                    _ => (400, "Join Rejected", false),
                };

                Debug.NetworkingWarning(
                    "[Server] Rejected realtime join client={0}; reason={1}; detail={2}",
                    clientId,
                    failureReason,
                    message ?? title);
                SendErrorToClient(clientId, statusCode, title, message ?? title, fatal: fatal, target: target);
            }

            private void BroadcastPlayerLeave(NetworkPlayerConnection connection, string reason)
            {
                var leave = new PlayerLeaveNotice
                {
                    ServerPlayerIndex = connection.ServerPlayerIndex,
                    ClientId = connection.ClientId,
                    Reason = reason,
                    SessionId = connection.SessionId
                };

                BroadcastStateChange(EStateChangeType.PlayerLeave, leave, compress: false);
            }

            private void SendErrorToClient(string clientId, int statusCode, string title, string detail, int? serverPlayerIndex = null, bool fatal = false, string? requestId = null, IPEndPoint? target = null)
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

                IPEndPoint? resolvedTarget = target ?? ResolveClientEndpoint(clientId, serverPlayerIndex);
                if (resolvedTarget is not null)
                    SendStateChangeTo(resolvedTarget, EStateChangeType.ServerError, error, compress: true, resendOnFailedAck: false);
                else
                    BroadcastServerError(error, compress: true, resendOnFailedAck: false);
            }

            private IPEndPoint? ResolveClientEndpoint(string? clientId, int? serverPlayerIndex)
            {
                lock (_playerLock)
                {
                    if (serverPlayerIndex is int idx
                        && _playersByIndex.TryGetValue(idx, out NetworkPlayerConnection? byIndex)
                        && byIndex.LastEndpoint is not null)
                    {
                        return byIndex.LastEndpoint;
                    }

                    if (!string.IsNullOrWhiteSpace(clientId)
                        && _playersByClientId.TryGetValue(clientId, out NetworkPlayerConnection? byClient)
                        && byClient.LastEndpoint is not null)
                    {
                        return byClient.LastEndpoint;
                    }
                }

                return null;
            }

            }

}
