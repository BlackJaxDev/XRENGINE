using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using XREngine.Networking;
using XREngine.Input;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Components;
using XREngine.Scene.Transforms;

namespace XREngine
{
    public partial class ServerNetworkingManager : BaseNetworkingManager
        {
            public override bool IsServer => true;
            public override bool IsClient => false;
            public override bool HasConnectedRemotePeer
            {
                get
                {
                    lock (_playerLock)
                        return _playersByIndex.Count != 0;
                }
            }

            private readonly object _playerLock = new();
            private readonly Dictionary<int, NetworkPlayerConnection> _playersByIndex = new();
            private readonly Dictionary<string, NetworkPlayerConnection> _playersByClientId = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, NetworkPlayerConnection> _resumeByClientKey = new(StringComparer.OrdinalIgnoreCase);
            private readonly RealtimeReplicationCoordinator _replication = new();
            private readonly object _transformTargetLock = new();
            private readonly List<IPEndPoint> _transformTargets = new(16);
            private int _nextServerPlayerIndex = 1;
            private int _stalePruneQueued;
            private XREngine.Timers.IRuntimeTimingServices? _simulationTiming;

            public ServerNetworkingManager() : base(peerId: "server") { }

            /// <summary>Hard local admission ceiling. Managed workers set this from their launch contract.</summary>
            public int MaxPlayers { get; set; } = int.MaxValue;
            /// <summary>Enables the collisionless fallback when no game simulator is installed.</summary>
            public bool EnableKinematicCharacterLocomotion { get; set; }
            public long TickProgress => _replication.CurrentServerTickId;

            public IReadOnlyList<ServerSessionPlayerEvent> GetConnectedPlayers()
            {
                lock (_playerLock)
                    return _playersByIndex.Values.Select(CreatePlayerEvent).ToArray();
            }

            /// <summary>
            /// Extends only an existing managed resume hold to the authoritative credential deadline.
            /// It never recreates a pruned player, so a Resume credential cannot allocate a new identity.
            /// </summary>
            public bool TrySetReservationResumeDeadline(string reservationId, string clientId, Guid sessionId, Guid generation, DateTimeOffset expiresUtc)
            {
                if (string.IsNullOrWhiteSpace(reservationId) || string.IsNullOrWhiteSpace(clientId) || sessionId == Guid.Empty || generation == Guid.Empty || expiresUtc <= DateTimeOffset.UtcNow)
                    return false;

                lock (_playerLock)
                {
                    string key = CreateResumeKey(sessionId, clientId);
                    if (string.IsNullOrEmpty(key)
                        || !_resumeByClientKey.TryGetValue(key, out NetworkPlayerConnection? player)
                        || player.ResumeUntilUtc <= DateTime.UtcNow
                        || !string.Equals(player.ReservationId, reservationId, StringComparison.Ordinal)
                        || player.JoinRequest?.WorkerGeneration != generation)
                    {
                        return false;
                    }

                    player.ResumeUntilUtc = expiresUtc.UtcDateTime;
                    return true;
                }
            }

            public void Start(
                IPAddress udpMulticastGroupIP,
                int udpMulticastPort,
                int udpReceivePort,
                IPAddress? bindAddress = null)
            {
                Debug.Log(ELogCategory.Networking, $"Starting server at udp(receive/send:{udpReceivePort}; multicast fallback:{udpMulticastGroupIP}:{udpMulticastPort})");
                MulticastEndPoint = new IPEndPoint(udpMulticastGroupIP, udpMulticastPort);
                StartUdpReceiver(udpReceivePort, bindAddress ?? IPAddress.Any);
                _simulationTiming = XREngine.Timers.RuntimeTimingServices.Current;
                _simulationTiming.FixedUpdate += AdvanceSimulationTick;
            }

            /// <summary>
            /// Run on the server - receives from clients
            /// </summary>
            /// <param name="udpPort"></param>

            protected void StartUdpReceiver(int udpPort, IPAddress bindAddress)
            {
                UdpClient listener = new();
                UdpSocketOptions.DisableConnectionReset(listener, "server UDP receiver");
                //listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Client.Bind(new IPEndPoint(bindAddress, udpPort));
                UdpReceiver = listener;
                UdpMulticastSender = listener;
            }

            protected override async Task SendUDP()
            {
                QueueStalePlayerPrune();
                PumpReplicationTransfers();
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

            protected override bool IsAllowedInboundSender(IPEndPoint? sender, EBroadcastType type)
                // A fresh endpoint may submit only a state-change join frame. Every mutation is
                // subsequently bound to an admitted connection in the state handler below.
                => sender is not null && type == EBroadcastType.StateChange;

            protected override void HandleStateChange(StateChangeInfo change, IPEndPoint? sender)
            {
                if (sender is null)
                    return;

                // UDP receive runs independently of simulation. Every handler rechecks the
                // endpoint/association after this queue boundary before it mutates world state.
                RuntimeNetworkingHostServices.Current.EnqueueSimulation(() => HandleStateChangeOnSimulation(change, sender));
            }

            private void HandleStateChangeOnSimulation(StateChangeInfo change, IPEndPoint? sender)
            {
                if (TryHandleReplicationStateChange(change, sender))
                    return;

                switch (change.Type)
                {
                    case EStateChangeType.PlayerJoin:
                        if (!StateChangePayloadSerializer.TryDeserialize<PlayerJoinRequest>(change.Data, out var join) || join is null || sender is null)
                            break;
                        HandlePlayerJoin(join, sender);
                        break;
                    case EStateChangeType.PlayerInputSnapshot:
                        if (!StateChangePayloadSerializer.TryDeserialize<PlayerInputSnapshot>(change.Data, out var snapshot) || snapshot is null || sender is null)
                            break;
                        HandlePlayerInputSnapshot(snapshot, sender);
                        break;
                    case EStateChangeType.PlayerTransformUpdate:
                        if (!StateChangePayloadSerializer.TryDeserialize<PlayerTransformUpdate>(change.Data, out var transformUpdate) || transformUpdate is null || sender is null)
                            break;
                        HandlePlayerTransformUpdate(transformUpdate, sender);
                        break;
                    case EStateChangeType.PlayerLeave:
                        if (!StateChangePayloadSerializer.TryDeserialize<PlayerLeaveNotice>(change.Data, out var leave) || leave is null || sender is null)
                            break;
                        HandlePlayerLeave(leave, sender);
                        break;
                    case EStateChangeType.Heartbeat:
                        if (!StateChangePayloadSerializer.TryDeserialize<PlayerHeartbeat>(change.Data, out var hb) || hb is null || sender is null)
                            break;
                        HandleHeartbeat(hb, sender);
                        break;
                    case EStateChangeType.HumanoidPoseFrame:
                        if (!StateChangePayloadSerializer.TryDeserialize<HumanoidPoseFrame>(change.Data, out var pose) || pose is null || sender is null)
                            break;
                        HandleHumanoidPoseFrame(pose, sender);
                        break;
                }
            }

            private void HandlePlayerJoin(PlayerJoinRequest request, IPEndPoint sender, ServerJoinAdmissionResult? preauthenticatedAdmission = null)
            {
                if (string.IsNullOrWhiteSpace(request.ClientId))
                {
                    SendJoinAdmissionFailure(string.Empty, AdmissionFailureReason.InvalidRequest, "A stable client identity is required.", sender);
                    return;
                }

                // A packet that merely copies an active client ID must not reach the admission
                // resolver, where a managed credential could otherwise be consumed or refreshed.
                lock (_playerLock)
                    if (_playersByClientId.TryGetValue(request.ClientId, out NetworkPlayerConnection? existing)
                        && !IsAuthenticatedSender(existing, sender, request.SessionId))
                    {
                        SendJoinAdmissionFailure(request.ClientId, AdmissionFailureReason.Unauthorized, "Endpoint rebinding requires a new authenticated resume admission.", sender);
                        return;
                    }

                NetworkPlayerConnection connection;
                bool isNewPlayer = false;
                ServerJoinAdmissionResult? joinAdmission = preauthenticatedAdmission ?? RuntimeNetworkingHostServices.Current.ResolveServerJoinAdmission(request);
                if (joinAdmission is { FailureReason: not AdmissionFailureReason.None })
                {
                    SendJoinAdmissionFailure(request.ClientId, joinAdmission.FailureReason, joinAdmission.Message, sender);
                    return;
                }

                ServerSessionContext? resolvedSession = joinAdmission?.SessionContext ?? RuntimeNetworkingHostServices.Current.ResolveServerSession(request);
                Guid? requestedSessionId = request.SessionId;
                IRuntimeNetworkWorldContext? resolvedWorldInstance = resolvedSession?.WorldContext ?? ResolvePrimaryWorldInstance();
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
                    bool wasAlreadyActive = _playersByClientId.TryGetValue(request.ClientId, out connection!);
                    if (wasAlreadyActive && !IsAuthenticatedSender(connection, sender, request.SessionId))
                    {
                        SendJoinAdmissionFailure(request.ClientId, AdmissionFailureReason.Unauthorized, "Endpoint rebinding requires a new authenticated resume admission.", sender);
                        return;
                    }
                    if (!wasAlreadyActive)
                    {
                        string resumeKey = CreateResumeKey(resolvedSession?.SessionId ?? requestedSessionId, request.ClientId);
                        if (request.ResumeRequested
                            && !string.IsNullOrEmpty(resumeKey)
                            && _resumeByClientKey.TryGetValue(resumeKey, out NetworkPlayerConnection? resumable)
                            && resumable.ResumeUntilUtc > DateTime.UtcNow)
                        {
                            connection = resumable;
                            _resumeByClientKey.Remove(resumeKey);
                        }
                        else
                        {
                            if (request.ResumeRequested)
                            {
                                SendJoinAdmissionFailure(request.ClientId, AdmissionFailureReason.Unauthorized, "The managed resume hold is no longer available.", sender);
                                return;
                            }

                            if (_playersByIndex.Count + _resumeByClientKey.Count >= MaxPlayers)
                            {
                                SendJoinAdmissionFailure(request.ClientId, AdmissionFailureReason.SessionFull, "The managed worker is at its local player limit.", sender);
                                return;
                            }

                            connection = new NetworkPlayerConnection
                            {
                                ServerPlayerIndex = _nextServerPlayerIndex++,
                                ClientId = request.ClientId,
                                ConnectedUtc = DateTimeOffset.UtcNow,
                                ReplicationConnectionGeneration = Guid.NewGuid(),
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
                    if (!wasAlreadyActive)
                        connection.ReplicationConnectionGeneration = Guid.NewGuid();
                    connection.WorldContext = resolvedWorldInstance;
                    connection.WorldAsset = serverWorldAsset;
                    if (!string.IsNullOrWhiteSpace(connection.ReservationId)
                        && !string.Equals(connection.ReservationId, joinAdmission?.ReservationId ?? request.ReservationId, StringComparison.Ordinal))
                    {
                        SendJoinAdmissionFailure(request.ClientId, AdmissionFailureReason.Unauthorized, "The client is already bound to another admission reservation.", sender);
                        return;
                    }
                    if (wasAlreadyActive && connection.CredentialPurpose is int committedPurpose
                        && (committedPurpose != joinAdmission?.CredentialPurpose
                            || connection.CredentialEpoch != (joinAdmission?.CredentialEpoch ?? 0)))
                    {
                        SendJoinAdmissionFailure(request.ClientId, AdmissionFailureReason.Unauthorized, "The client retry does not match its committed managed admission credential.", sender);
                        return;
                    }
                    connection.AccountId = joinAdmission?.AccountId ?? request.AccountId;
                    connection.ReservationId = joinAdmission?.ReservationId ?? request.ReservationId;
                    connection.CredentialPurpose = joinAdmission?.CredentialPurpose;
                    connection.CredentialEpoch = joinAdmission?.CredentialEpoch ?? 0;
                    if (joinAdmission?.AcceptedUtc is DateTimeOffset acceptedUtc)
                        connection.ConnectedUtc = acceptedUtc;
                    connection.LastEndpoint = sender;
                    if (connection.WorldContext is null)
                    {
                        RemoveRejectedConnection(connection);
                        SendErrorToClient(request.ClientId, 500, "No World", "Server could not resolve a world instance for this connection.", null, fatal: true, target: sender);
                        return;
                    }

                    try
                    {
                        EnsureServerPawn(connection, request.DisplayName);
                    }
                    catch (Exception ex)
                    {
                        RemoveRejectedConnection(connection);
                        SendErrorToClient(request.ClientId, 500, "Pawn Creation Failed", ex.Message, null, fatal: true, target: sender);
                        return;
                    }
                    if (connection.Pawn is null || connection.TransformId == Guid.Empty || connection.NetworkEntityId.IsEmpty)
                    {
                        RemoveRejectedConnection(connection);
                        SendErrorToClient(request.ClientId, 500, "Pawn Creation Failed", "Server could not create the player pawn.", null, fatal: true, target: sender);
                        return;
                    }
                    EnsureAuthorityLease(connection);

                    connection.JoinRequest = request;
                    connection.LastHeardUtc = DateTime.UtcNow;
                    connection.ResumeUntilUtc = null;
                    if (connection.IsResuming && joinAdmission?.AcceptedUtc is null)
                        connection.ConnectedUtc = DateTimeOffset.UtcNow;
                }

                var assignment = new PlayerAssignment
                {
                    ServerPlayerIndex = connection.ServerPlayerIndex,
                    PlayerEntityId = connection.NetworkEntityId,
                    PawnId = connection.Pawn?.ID ?? Guid.Empty,
                    TransformId = connection.TransformId,
                    ClientId = connection.ClientId,
                    AccountId = connection.AccountId,
                    ReservationId = connection.ReservationId,
                    WorkerGeneration = connection.JoinRequest?.WorkerGeneration,
                    DisplayName = request.DisplayName,
                    World = BuildWorldDescriptor(connection.WorldContext, connection.WorldAsset),
                    SessionId = connection.SessionId,
                    IsAuthoritative = true,
                    AuthorityLease = connection.AuthorityLease?.Clone(),
                    ServerTickId = _replication.CurrentServerTickId,
                    ServerTimeUtc = GetUtcSeconds(),
                    ReplicationConnectionGeneration = connection.ReplicationConnectionGeneration,
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
                    RealtimeJoinHandoffContract.DescribeWorldAsset(connection.WorldAsset));
                RuntimeNetworkingHostServices.Current.NotifyServerPlayerConnected(CreatePlayerEvent(connection));
                BeginReplicationSynchronization(CreatePlayerEvent(connection));

                if (isNewPlayer)
                    BroadcastExistingTransforms();
            }

            private void RemoveRejectedConnection(NetworkPlayerConnection connection)
            {
                _playersByIndex.Remove(connection.ServerPlayerIndex);
                _playersByClientId.Remove(connection.ClientId);
                ReleasePlayerResources(connection);
                // Admission was accepted but world/pawn setup failed. Notify the managed host so its
                // accepted-proof cache cannot retain an abandoned handshake.
                RuntimeNetworkingHostServices.Current.NotifyServerPlayerDisconnected(CreatePlayerEvent(connection));
            }

            private void EnsureServerPawn(NetworkPlayerConnection connection, string? displayName)
            {
                if (connection.WorldContext is null)
                    return;

                if (connection.Pawn is { IsDestroyed: false })
                {
                    connection.TransformId = connection.TransformId == Guid.Empty
                        ? connection.Pawn.SceneNode?.Transform?.ID ?? Guid.Empty
                        : connection.TransformId;
                    connection.NetworkEntityId = CreateEntityId(connection);
                    return;
                }

                IRuntimeNetworkWorldContext worldInstance = connection.WorldContext;
                worldInstance.GameMode ??= new CustomGameMode { WorldInstance = worldInstance.WorldInstance };

                connection.Pawn = worldInstance.GameMode.CreateDefaultPawn(ELocalPlayerIndex.One) as PawnComponent
                    ?? worldInstance.CreateRemotePawn(connection.ServerPlayerIndex, displayName, serverOwned: true);

                if (connection.Pawn is not null)
                {
                    connection.TransformId = connection.Pawn.SceneNode?.Transform?.ID ?? Guid.Empty;
                    connection.NetworkEntityId = CreateEntityId(connection);
                    var controller = RuntimeNetworkingHostServices.Current.CreateRemotePlayer(connection.ServerPlayerIndex);
                    if (controller is null)
                    {
                        worldInstance.DestroyPawn(connection.Pawn);
                        connection.Pawn = null;
                        connection.TransformId = Guid.Empty;
                        connection.NetworkEntityId = default;
                        return;
                    }

                    controller.ControlledPawnComponent = connection.Pawn;
                    connection.Pawn.Controller = controller;
                    connection.RemoteController = controller;
                    RuntimeNetworkingHostServices.Current.AddRemotePlayer(controller);
                }
            }

            private static void ReleasePlayerResources(NetworkPlayerConnection connection)
            {
                if (connection.RemoteController is IPawnController controller)
                {
                    RuntimeNetworkingHostServices.Current.RemoveRemotePlayer(controller);
                    if (controller is XRObjectBase controllerObject)
                        controllerObject.Destroy();
                    connection.RemoteController = null;
                }

                if (connection.Pawn is not null)
                {
                    connection.WorldContext?.DestroyPawn(connection.Pawn);
                    connection.Pawn = null;
                }

                connection.TransformId = Guid.Empty;
                connection.NetworkEntityId = default;
            }


            private void HandlePlayerInputSnapshot(PlayerInputSnapshot snapshot, IPEndPoint sender)
            {
                double nowUtc = GetUtcSeconds();
                lock (_playerLock)
                {
                    if (!_playersByIndex.TryGetValue(snapshot.ServerPlayerIndex, out var connection))
                        return;

                    if (!IsAuthenticatedSender(connection, sender, snapshot.SessionId))
                        return;

                    snapshot.SessionId = connection.SessionId;
                    if (snapshot.EntityId.IsEmpty)
                        snapshot.EntityId = connection.NetworkEntityId;
                    if (!ValidateAuthority(connection, snapshot.EntityId, nowUtc, out NetworkAuthorityRevocationReason failureReason))
                    {
                        SendErrorToClient(connection.ClientId, 403, "Authority Rejected", $"Input rejected: {failureReason}.", connection.ServerPlayerIndex, fatal: false);
                        return;
                    }

                    if (!_replication.TryBufferInput(snapshot, nowUtc, connection.LastProcessedInputSequence, out int depth))
                        return;

                    connection.InputBufferDepth = depth;
                    connection.LastInput = snapshot;
                    connection.LastHeardUtc = DateTime.UtcNow;
                }
            }

            private void HandlePlayerTransformUpdate(PlayerTransformUpdate transform, IPEndPoint sender)
            {
                NetworkPlayerConnection? connection;
                double nowUtc = GetUtcSeconds();
                lock (_playerLock)
                {
                    if (!_playersByIndex.TryGetValue(transform.ServerPlayerIndex, out connection))
                        return;

                    if (!IsAuthenticatedSender(connection, sender, transform.SessionId))
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
                        if (!string.Equals(hb.ClientId, connection.ClientId, StringComparison.Ordinal)
                            || !IsAuthenticatedSender(connection, sender, hb.SessionId))
                            return;

                        connection.LastHeardUtc = DateTime.UtcNow;
                        playerEvent = CreatePlayerEvent(connection);
                        clockSync = CreateClockSync(connection, hb, receiveUtc);
                    }
                }

                if (playerEvent is not null)
                    RuntimeNetworkingHostServices.Current.NotifyServerPlayerHeartbeatObserved(playerEvent);
                if (clockSync is not null)
                    SendClockSyncTo(sender, clockSync);
            }

            private void HandleHumanoidPoseFrame(HumanoidPoseFrame frame, IPEndPoint sender)
            {
                if (!TryAcceptHumanoidPoseFrame(frame, sender, out HumanoidPoseFrame accepted))
                    return;

                base.HandleStateChange(new StateChangeInfo(EStateChangeType.HumanoidPoseFrame, StateChangePayloadSerializer.Serialize(accepted)), null);
                RecordReplicationPose(accepted);
                BroadcastHumanoidPoseFrame(accepted, compress: false, resendOnFailedAck: false);
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

            private void HandlePlayerLeave(PlayerLeaveNotice leave, IPEndPoint sender)
            {
                NetworkPlayerConnection? connection;
                lock (_playerLock)
                {
                    if (!_playersByIndex.TryGetValue(leave.ServerPlayerIndex, out connection))
                        return;

                    if (!string.Equals(leave.ClientId, connection.ClientId, StringComparison.Ordinal)
                        || !IsAuthenticatedSender(connection, sender, leave.SessionId))
                        return;

                    _playersByIndex.Remove(leave.ServerPlayerIndex);
                    _playersByClientId.Remove(connection.ClientId);
                    // A raw UDP leave is a transport departure. Preserve the server-owned identity so a
                    // managed Resume credential can restore it; terminal API deletes use KickReservation.
                    connection.ResumeUntilUtc = DateTime.UtcNow + MultiplayerRuntimePolicy.SessionResumeWindow;
                    string resumeKey = CreateResumeKey(connection.SessionId, connection.ClientId);
                    if (!string.IsNullOrEmpty(resumeKey))
                        _resumeByClientKey[resumeKey] = connection;
                }

                bool managedClosed = CloseManagedAssociation(connection.LastEndpoint);
                RevokeAuthorityLease(connection, NetworkAuthorityRevocationReason.OwnerLeft, leave.Reason ?? "Client requested leave");
                RemoveReplicationSynchronization(CreatePlayerEvent(connection));
                BroadcastPlayerLeave(connection, leave.Reason ?? "Client requested leave");
                RuntimeNetworkingHostServices.Current.NotifyServerPlayerDisconnected(CreatePlayerEvent(connection));
                if (!managedClosed)
                    SendErrorToClient(connection.ClientId, 499, "Client Closed", leave.Reason ?? "Client requested leave", connection.ServerPlayerIndex, fatal: false, target: connection.LastEndpoint);
            }

            private bool IsAuthenticatedSender(NetworkPlayerConnection connection, IPEndPoint sender, Guid? sessionId)
                => connection.LastEndpoint is not null
                    && connection.LastEndpoint.Equals(sender)
                    && connection.SessionId != Guid.Empty
                    && sessionId.HasValue
                    && sessionId.Value == connection.SessionId
                    && (!RequiresManagedUdpTransport || IsManagedAssociationForConnection(connection, sender));

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

                bool managedClosed = CloseManagedAssociation(connection.LastEndpoint);
                RevokeAuthorityLease(connection, NetworkAuthorityRevocationReason.OperatorRevoked, reason);
                RemoveReplicationSynchronization(CreatePlayerEvent(connection));
                ReleasePlayerResources(connection);
                BroadcastPlayerLeave(connection, reason);
                RuntimeNetworkingHostServices.Current.NotifyServerPlayerDisconnected(CreatePlayerEvent(connection));
                if (!managedClosed)
                    SendErrorToClient(connection.ClientId, 403, "Kicked", reason, connection.ServerPlayerIndex, fatal: true, target: connection.LastEndpoint);
            }

            /// <summary>Kicks an active player or destroys a resume-held player by its managed reservation.</summary>
            public void KickReservation(string reservationId, string reason = "Managed admission revoked")
            {
                if (string.IsNullOrWhiteSpace(reservationId))
                    return;

                NetworkPlayerConnection? active = null;
                NetworkPlayerConnection? held = null;
                lock (_playerLock)
                {
                    active = _playersByIndex.Values.FirstOrDefault(player => string.Equals(player.ReservationId, reservationId, StringComparison.Ordinal));
                    if (active is not null)
                    {
                        _playersByIndex.Remove(active.ServerPlayerIndex);
                        _playersByClientId.Remove(active.ClientId);
                    }
                    else
                    {
                        string? key = _resumeByClientKey.FirstOrDefault(pair => string.Equals(pair.Value.ReservationId, reservationId, StringComparison.Ordinal)).Key;
                        if (!string.IsNullOrEmpty(key))
                            _resumeByClientKey.Remove(key, out held);
                    }
                }

                NetworkPlayerConnection? connection = active ?? held;
                if (connection is null)
                    return;

                bool managedClosed = CloseManagedAssociation(connection.LastEndpoint);
                RevokeAuthorityLease(connection, NetworkAuthorityRevocationReason.OperatorRevoked, reason);
                RemoveReplicationSynchronization(CreatePlayerEvent(connection));
                ReleasePlayerResources(connection);
                if (active is not null)
                {
                    BroadcastPlayerLeave(connection, reason);
                    RuntimeNetworkingHostServices.Current.NotifyServerPlayerDisconnected(CreatePlayerEvent(connection));
                    if (!managedClosed)
                        SendErrorToClient(connection.ClientId, 403, "Kicked", reason, connection.ServerPlayerIndex, fatal: true, target: connection.LastEndpoint);
                }
            }

            private WorldSyncDescriptor BuildWorldDescriptor(IRuntimeNetworkWorldContext? worldInstance, WorldAssetIdentity? asset)
            {
                IRuntimeNetworkWorldContext? targetInstance = worldInstance ?? ResolvePrimaryWorldInstance();
                if (targetInstance is null)
                    return new WorldSyncDescriptor { Asset = asset };

                XRWorld? world = targetInstance.TargetWorld;
                return new WorldSyncDescriptor
                {
                    WorldName = world?.Name,
                    WorldBootstrapId = GameModeBootstrapRegistry.TryGetBootstrapId(targetInstance.GameMode, out string? bootstrapId)
                        ? bootstrapId
                        : null,
                    GameModeType = targetInstance.GameMode?.GetType().FullName,
                    SceneNames = world?.Scenes.Select(s => s.Name ?? string.Empty).Where(static n => !string.IsNullOrWhiteSpace(n)).ToArray() ?? Array.Empty<string>(),
                    Asset = asset ?? CreateLocalWorldAsset(targetInstance)
                };
            }

            private static IRuntimeNetworkWorldContext? ResolvePrimaryWorldInstance()
                => RuntimeNetworkingHostServices.Current.ResolvePrimaryWorld();

            private static WorldAssetIdentity? CreateLocalWorldAsset(IRuntimeNetworkWorldContext? worldInstance)
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
                    {
                        Interlocked.Add(ref _authoritativeTransformBytes, estimatedTransformBytes * _transformTargets.Count);
                        BroadcastStateChangeToTargets(_transformTargets, EStateChangeType.PlayerTransformUpdate, update, compress: false);
                    }

                    _transformTargets.Clear();
                }
            }

            private sealed class NetworkPlayerConnection
            {
                public required int ServerPlayerIndex { get; init; }
                public required string ClientId { get; init; }
                public string? AccountId { get; set; }
                public string? ReservationId { get; set; }
                public int? CredentialPurpose { get; set; }
                public long CredentialEpoch { get; set; }
                public DateTimeOffset ConnectedUtc { get; set; }
                public DateTimeOffset? SynchronizedUtc { get; set; }
                public Guid SessionId { get; set; }
                public Guid ReplicationConnectionGeneration { get; set; }
                public IRuntimeNetworkWorldContext? WorldContext { get; set; }
                public WorldAssetIdentity? WorldAsset { get; set; }
                public PawnComponent? Pawn { get; set; }
                public IPawnController? RemoteController { get; set; }
                public Guid TransformId { get; set; }
                public NetworkEntityId NetworkEntityId { get; set; }
                public NetworkAuthorityLease? AuthorityLease { get; set; }
                public PlayerJoinRequest? JoinRequest { get; set; }
                public PlayerInputSnapshot? LastInput { get; set; }
                public PlayerTransformUpdate? LastTransform { get; set; }
                public HumanoidPoseFrame? LastPose { get; set; }
                /// <summary>Bounded deltas following the cached pose baseline for late-join reconstruction.</summary>
                public List<HumanoidPoseFrame> PoseDeltas { get; } = [];
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
                Dictionary<NetworkPlayerConnection, IPEndPoint?> staleEndpoints = [];
                List<NetworkPlayerConnection> expiredResumeHolds = [];
                lock (_playerLock)
                {
                    var now = DateTime.UtcNow;
                    stale = _playersByIndex.Values
                        .Where(p => now - p.LastHeardUtc > MultiplayerRuntimePolicy.PlayerHeartbeatTimeout + MultiplayerRuntimePolicy.PlayerHeartbeatGracePeriod)
                        .ToList();

                    foreach (var player in stale)
                    {
                        staleEndpoints[player] = player.LastEndpoint;
                        _playersByIndex.Remove(player.ServerPlayerIndex);
                        _playersByClientId.Remove(player.ClientId);
                        player.LastEndpoint = null;
                        player.ResumeUntilUtc = now + MultiplayerRuntimePolicy.SessionResumeWindow;
                        string resumeKey = CreateResumeKey(player.SessionId, player.ClientId);
                        if (!string.IsNullOrEmpty(resumeKey))
                            _resumeByClientKey[resumeKey] = player;
                    }

                    foreach ((string key, NetworkPlayerConnection player) in _resumeByClientKey.ToArray())
                        if (player.ResumeUntilUtc <= now && _resumeByClientKey.Remove(key))
                            expiredResumeHolds.Add(player);
                }

                foreach (var player in stale)
                {
                    Debug.Out($"[Server] Dropped stale player {player.ClientId} (index {player.ServerPlayerIndex}).");
                    IPEndPoint? previousEndpoint = staleEndpoints[player];
                    bool managedClosed = CloseManagedAssociation(previousEndpoint);
                    RevokeAuthorityLease(player, NetworkAuthorityRevocationReason.OwnerDisconnected, "Heartbeat timeout");
                    RemoveReplicationSynchronization(CreatePlayerEvent(player));
                    BroadcastPlayerLeave(player, "Heartbeat timeout");
                    RuntimeNetworkingHostServices.Current.NotifyServerPlayerDisconnected(CreatePlayerEvent(player));
                    if (!managedClosed)
                        SendErrorToClient(player.ClientId, 408, "Request Timeout", "Heartbeat timed out.", player.ServerPlayerIndex, fatal: true, target: previousEndpoint);
                }

                foreach (NetworkPlayerConnection player in expiredResumeHolds)
                    ReleasePlayerResources(player);
            }

            private void QueueStalePlayerPrune()
            {
                long now = Environment.TickCount64;
                if (now < Interlocked.Read(ref _nextStalePruneAt))
                    return;
                if (Interlocked.Exchange(ref _stalePruneQueued, 1) != 0)
                    return;
                Interlocked.Exchange(ref _nextStalePruneAt, now + 1000);

                RuntimeNetworkingHostServices.Current.EnqueueSimulation(() =>
                {
                    try { PruneStalePlayers(); }
                    finally { Volatile.Write(ref _stalePruneQueued, 0); }
                });
            }

            private long _nextStalePruneAt;

            private static ServerSessionPlayerEvent CreatePlayerEvent(NetworkPlayerConnection connection)
                => new(connection.SessionId, connection.ClientId, connection.ServerPlayerIndex, connection.TransformId, connection.AccountId, connection.ReservationId, connection.CredentialPurpose, connection.CredentialEpoch, connection.ConnectedUtc, connection.SynchronizedUtc);

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
