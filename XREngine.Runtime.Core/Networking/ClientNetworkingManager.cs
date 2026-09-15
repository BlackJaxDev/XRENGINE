using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Net;
using System.Net.Sockets;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Input;
using XREngine.Networking;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Timers;

namespace XREngine
{
    public partial class ClientNetworkingManager : BaseNetworkingManager
        {
            public override bool IsServer => false;
            public override bool IsClient => true;
            public override bool HasConnectedRemotePeer => _assignmentReceived;

            private readonly string _generatedClientId = Guid.NewGuid().ToString("N");
            private bool _joinRequested;
            private bool _tickRegistered;
            private volatile bool _assignmentReceived;
            private volatile int _primaryAssignedServerPlayerIndex = -1;
            private NetworkEntityId _primaryAssignedEntityId = NetworkEntityId.Empty;
            private long _lastInputSyncTicks;
            private long _lastTransformSyncTicks;
            private long _lastJoinRequestTicks;
            private long _lastHeartbeatTicks;
            private Guid _activeSessionId = Guid.Empty;
            private uint _inputSequence;
            private uint _poseFrameSequence;
            private long _clientTickId;
            private long _lastReceivedServerTickId;
            private uint _lastProcessedInputSequence;
            private double _clockOffsetSeconds;
            private const double InputSyncIntervalSeconds = 1.0 / 60.0;
            private const double TransformSyncIntervalSeconds = 1.0 / 20.0;
            private const double JoinRetrySeconds = 3.0;
            private const double HeartbeatIntervalSeconds = 3.0;
            private readonly Dictionary<int, RemotePlayerState> _remotePlayers = new();
            private readonly HashSet<int> _localServerIndices = new();
            private WorldAssetIdentity? _localWorldAsset;
            private string EffectiveClientId => string.IsNullOrWhiteSpace(StableClientId) ? _generatedClientId : StableClientId.Trim();

            public ClientNetworkingManager() : base(peerId: null)
            {
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    LastServerError = null;
                    SendPlayerLeaveForLocals("Client disposed");
                    DisposeManagedTransport();
                    _tlsTunnel?.Dispose();
                    _tlsTunnel = null;

                    if (_tickRegistered)
                    {
                        RuntimeTimingServices.Current.Update -= TickClientNetwork;
                        _tickRegistered = false;
                    }

                    DisposeReplicationSynchronization();
                }

                base.Dispose(disposing);
                if (disposing)
                    UdpSender = null;
            }

            /// <summary>
            /// The server IP to send to.
            /// </summary>
            public IPEndPoint? ServerIP { get; set; }
            /// <summary>
            /// Sends from client to server.
            /// </summary>
            public UdpClient? UdpSender { get; set; }
            public Guid? SessionId { get; set; }
            public string? SessionToken { get; set; }
            /// <summary>Stable client-instance identity supplied by a trusted launcher when available.</summary>
            public string? StableClientId { get; set; }
            /// <summary>Stable control-plane account identity carried through a managed handoff.</summary>
            public string? AccountId { get; set; }
            public Guid? WorkerGeneration { get; set; }
            public bool ResumeRequested { get; set; }
            public long CredentialEpoch { get; set; }
            public string? ReservationId { get; set; }
            /// <summary>Opaque player-specific admission grant. Never log this value.</summary>
            public string? AdmissionSecret { get; set; }
            public WorldAssetIdentity? LocalWorldAsset => _localWorldAsset ??= CreateLocalWorldAsset();
            public double EstimatedServerClockOffsetSeconds => _clockOffsetSeconds;
            public long LastReceivedServerTickId => _lastReceivedServerTickId;
            public uint LastProcessedInputSequence => _lastProcessedInputSequence;
            /// <summary>Last server-reported non-secret failure applicable to this client session.</summary>
            public ServerErrorMessage? LastServerError { get; private set; }
            public Guid? AssignedSessionId => _activeSessionId == Guid.Empty ? null : _activeSessionId;
            public int? PrimaryAssignedServerPlayerIndex => _primaryAssignedServerPlayerIndex < 0 ? null : _primaryAssignedServerPlayerIndex;
            /// <summary>Authoritative assigned pawn identity, retained even when this process has no UI local player.</summary>
            public NetworkEntityId? PrimaryAssignedEntityId => _primaryAssignedEntityId.IsEmpty ? null : _primaryAssignedEntityId;
            /// <summary>True only after a local assignment has supplied both a session and player identity.</summary>
            public bool HasValidLocalAssignment
                => _assignmentReceived && _activeSessionId != Guid.Empty && _primaryAssignedServerPlayerIndex >= 0;

            public void Start(
                IPAddress udpMulticastGroupIP,
                int udpMulticastPort,
                IPAddress serverIP,
                int udpSendPort,
                int udpClientReceivePort)
            {
                Debug.Log(ELogCategory.Networking, $"Starting client with udp(receive:{udpClientReceivePort}) sending to server at ({serverIP}:{udpSendPort})");
                LastServerError = null;
                StartSelectedTransport(serverIP, udpSendPort, udpClientReceivePort);
                PauseUntilReplicationAssignment();
                EnsureClientTick();
                if (!StartManagedTransportHandshake())
                    SendJoinRequest();
            }

            protected void StartUdpSender(IPAddress serverIP, int udpMulticastServerPort, int udpClientReceivePort)
            {
                UdpClient udpClient = new(AddressFamily.InterNetwork)
                {
                    ExclusiveAddressUse = false,
                };
                UdpSocketOptions.DisableConnectionReset(udpClient, "client UDP sender/receiver");
                udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, udpClientReceivePort));
                UdpSender = udpClient;
                UdpReceiver = udpClient;
                ServerIP = new IPEndPoint(serverIP, udpMulticastServerPort);
                //UdpSender.Connect(ServerIP);
            }

            protected override async Task SendUDP()
            {
                //Send to server
                await ConsumeAndSendUDPQueue(UdpSender, ServerIP);
            }

            protected override void CollectUdpSendTargets(List<IPEndPoint> targets)
            {
                if (ServerIP is not null)
                    targets.Add(ServerIP);
            }

            protected override bool IsAllowedInboundSender(IPEndPoint? sender, EBroadcastType type)
                => sender is not null && ServerIP is not null && sender.Equals(ServerIP);

            public override void ConsumeQueues()
            {
                base.ConsumeQueues();

                // If the UDP connection drops, proactively send leave once
                if (!UDPServerConnectionEstablished && _assignmentReceived)
                {
                    SendPlayerLeaveForLocals("UDP disconnected");
                    _assignmentReceived = false;
                    ResetReplicationSynchronization();
                }
            }

            protected override void HandleStateChange(StateChangeInfo change, IPEndPoint? sender)
            {
                if (TryHandleReplicationStateChange(change, sender))
                    return;

                // Remote jobs are not part of the managed realtime authority surface. They can
                // load/process arbitrary application data and must use an explicitly provisioned
                // control transport instead of a gameplay UDP sender.
                if (change.Type is EStateChangeType.RemoteJobRequest or EStateChangeType.RemoteJobResponse)
                    return;

                if (change.Type == EStateChangeType.HumanoidPoseFrame)
                {
                    QueueReplicationPresentation(() => base.HandleStateChange(change, sender));
                    return;
                }

                switch (change.Type)
                {
                    case EStateChangeType.PlayerAssignment:
                        if (StateChangePayloadSerializer.TryDeserialize<PlayerAssignment>(change.Data, out var assignment) && assignment is not null)
                            RuntimeNetworkingHostServices.Current.EnqueueSimulation(() => HandlePlayerAssignment(assignment));
                        break;
                    case EStateChangeType.PlayerTransformUpdate:
                        if (StateChangePayloadSerializer.TryDeserialize<PlayerTransformUpdate>(change.Data, out var transformUpdate) && transformUpdate is not null)
                            QueueReplicationPresentation(() => HandleRemoteTransform(transformUpdate));
                        break;
                    case EStateChangeType.PlayerLeave:
                        if (StateChangePayloadSerializer.TryDeserialize<PlayerLeaveNotice>(change.Data, out var leave) && leave is not null)
                            RuntimeNetworkingHostServices.Current.EnqueueSimulation(() => HandlePlayerLeave(leave));
                        break;
                    case EStateChangeType.ServerError:
                        if (StateChangePayloadSerializer.TryDeserialize<ServerErrorMessage>(change.Data, out var error) && error is not null)
                            HandleServerError(error);
                        break;
                    case EStateChangeType.AuthorityLeaseUpdate:
                        if (StateChangePayloadSerializer.TryDeserialize<NetworkAuthorityLease>(change.Data, out var lease) && lease is not null)
                            QueueReplicationPresentation(() => HandleAuthorityLeaseUpdate(lease));
                        break;
                    case EStateChangeType.ClockSync:
                        if (StateChangePayloadSerializer.TryDeserialize<ClockSyncMessage>(change.Data, out var clock) && clock is not null)
                            HandleClockSync(clock);
                        break;
                    case EStateChangeType.ReplicationSnapshot:
                    case EStateChangeType.ReplicationDelta:
                        base.HandleStateChange(change, sender);
                        break;
                }
            }

            private void EnsureClientTick()
            {
                if (_tickRegistered)
                    return;

                RuntimeTimingServices.Current.Update += TickClientNetwork;
                _tickRegistered = true;
            }

            private void TickClientNetwork()
            {
                TickReplicationSynchronization();
                TickManagedTransportHandshake();
                if (!UDPServerConnectionEstablished)
                    return;

                if (IsManagedTransportRequested && !IsManagedTransportEstablished)
                    return;

                long nowTicks = CurrentEngineTicks();
                _clientTickId++;

                if (!_assignmentReceived && (!_joinRequested || HasElapsed(nowTicks, _lastJoinRequestTicks, JoinRetrySeconds)))
                    SendJoinRequest();

                if (IsGameplayReady && HasElapsed(nowTicks, _lastInputSyncTicks, InputSyncIntervalSeconds))
                {
                    SendLocalInputSnapshots();
                    _lastInputSyncTicks = nowTicks;
                }

                if (!IsManagedTransportRequested && IsGameplayReady && HasElapsed(nowTicks, _lastTransformSyncTicks, TransformSyncIntervalSeconds))
                {
                    SendLocalTransformSnapshots();
                    _lastTransformSyncTicks = nowTicks;
                }

                if (_assignmentReceived && HasElapsed(nowTicks, _lastHeartbeatTicks, HeartbeatIntervalSeconds))
                {
                    SendHeartbeat();
                    _lastHeartbeatTicks = nowTicks;
                }
            }

            private void SendJoinRequest()
            {
                if (IsManagedTransportRequested)
                {
                    // The handshake starts once with the connection and owns its retry state.
                    // Accept can precede simulation-thread assignment; never restart with the erased secret.
                    return;
                }
                long nowTicks = CurrentEngineTicks();
                _localWorldAsset ??= CreateLocalWorldAsset();

                PlayerJoinRequest request = new()
                {
                    ClientId = EffectiveClientId,
                    DisplayName = Environment.UserName,
                    BuildVersion = CurrentProtocolVersion,
                    WorldName = ResolvePrimaryWorldInstance()?.TargetWorld?.Name,
                    ClientWorldAsset = _localWorldAsset,
                    SessionId = SessionId ?? (_activeSessionId == Guid.Empty ? null : _activeSessionId),
                    SessionToken = SessionToken,
                    AccountId = AccountId,
                    ReservationId = ReservationId,
                    AdmissionSecret = AdmissionSecret,
                    WorkerGeneration = WorkerGeneration,
                    ResumeRequested = ResumeRequested,
                    CredentialEpoch = CredentialEpoch,
                };

                BroadcastStateChange(EStateChangeType.PlayerJoin, request, compress: true);
                _joinRequested = true;
                _lastJoinRequestTicks = nowTicks;
            }

            private void SendHeartbeat()
            {
                bool emittedAssignedPlayer = false;
                foreach (var player in RuntimeNetworkingHostServices.Current.LocalPlayers)
                {
                    if (player is null)
                        continue;

                    if (player.PlayerInfo is not { } playerInfo)
                        continue;

                    int serverIndex = playerInfo.ServerIndex;
                    if (serverIndex < 0)
                        continue;

                    var heartbeat = new PlayerHeartbeat
                    {
                        ServerPlayerIndex = serverIndex,
                        ClientId = EffectiveClientId,
                        TimestampUtc = GetUtcSeconds(),
                        ClientSendTimestampUtc = GetUtcSeconds(),
                        LastReceivedServerTickId = _lastReceivedServerTickId,
                        LastProcessedInputSequence = _lastProcessedInputSequence,
                        SessionId = _activeSessionId == Guid.Empty ? null : _activeSessionId
                    };

                    BroadcastStateChange(EStateChangeType.Heartbeat, heartbeat, compress: false);
                    emittedAssignedPlayer |= serverIndex == _primaryAssignedServerPlayerIndex;
                }

                if (!emittedAssignedPlayer && HasValidLocalAssignment)
                {
                    BroadcastStateChange(EStateChangeType.Heartbeat, new PlayerHeartbeat
                    {
                        ServerPlayerIndex = _primaryAssignedServerPlayerIndex,
                        ClientId = EffectiveClientId,
                        TimestampUtc = GetUtcSeconds(),
                        ClientSendTimestampUtc = GetUtcSeconds(),
                        LastReceivedServerTickId = _lastReceivedServerTickId,
                        LastProcessedInputSequence = _lastProcessedInputSequence,
                        SessionId = _activeSessionId,
                    }, compress: false);
                }
            }

            private void SendLocalInputSnapshots()
            {
                foreach (var player in RuntimeNetworkingHostServices.Current.LocalPlayers)
                {
                    if (player is null)
                        continue;

                    if (player.PlayerInfo is not { } playerInfo)
                        continue;

                    int serverIndex = playerInfo.ServerIndex;
                    if (serverIndex < 0)
                        continue;

                    if (player.ControlledPawnComponent is not PawnComponent pawn)
                        continue;

                    if (pawn.CaptureNetworkInputState() is not CharacterPawnInputSnapshot capturedInput)
                        continue;

                    var snapshot = new PlayerInputSnapshot
                    {
                        ServerPlayerIndex = serverIndex,
                        EntityId = playerInfo.NetworkEntityId,
                        Input = capturedInput,
                        TimestampUtc = GetUtcSeconds(),
                        ClientSendTimestampUtc = GetUtcSeconds(),
                        InputSequence = ++_inputSequence,
                        ClientTickId = _clientTickId,
                        SessionId = playerInfo.SessionId
                    };

                    RecordPredictedInput(snapshot);
                    BroadcastStateChange(EStateChangeType.PlayerInputSnapshot, snapshot, compress: true);
                }
            }

            private void SendLocalTransformSnapshots()
            {
                foreach (var player in RuntimeNetworkingHostServices.Current.LocalPlayers)
                {
                    if (player is null)
                        continue;

                    if (player.PlayerInfo is not { } playerInfo)
                        continue;

                    int serverIndex = playerInfo.ServerIndex;
                    if (serverIndex < 0)
                        continue;

                    Transform? transform = player.ControlledPawnComponent?.SceneNode?.Transform as Transform;
                    if (transform is null)
                        continue;

                    PlayerTransformUpdate update = new()
                    {
                        ServerPlayerIndex = serverIndex,
                        EntityId = playerInfo.NetworkEntityId,
                        TransformId = transform.ID,
                        Translation = transform.Translation,
                        Rotation = transform.Rotation,
                        Velocity = Vector3.Zero,
                        SessionId = playerInfo.SessionId,
                        ClientInputSequence = _inputSequence,
                        AuthorityMode = NetworkAuthorityMode.ClientPredicted
                    };

                    BroadcastStateChange(EStateChangeType.PlayerTransformUpdate, update, compress: false);
                }
            }

            private void SendPlayerLeaveForLocals(string reason)
            {
                bool emittedAssignedPlayer = false;
                foreach (var player in RuntimeNetworkingHostServices.Current.LocalPlayers)
                {
                    if (player is null)
                        continue;

                    if (player.PlayerInfo is not { } playerInfo)
                        continue;

                    int serverIndex = playerInfo.ServerIndex;
                    if (serverIndex < 0)
                        continue;

                    var leave = new PlayerLeaveNotice
                    {
                        ServerPlayerIndex = serverIndex,
                        ClientId = EffectiveClientId,
                        Reason = reason,
                        SessionId = playerInfo.SessionId
                    };

                    BroadcastStateChange(EStateChangeType.PlayerLeave, leave, compress: false);
                    emittedAssignedPlayer |= serverIndex == _primaryAssignedServerPlayerIndex;

                    playerInfo.ServerIndex = -1;
                    playerInfo.NetworkEntityId = NetworkEntityId.Empty;
                    playerInfo.AuthorityLease = null;
                    _localServerIndices.Remove(serverIndex);
                }

                if (!emittedAssignedPlayer && HasValidLocalAssignment)
                {
                    BroadcastStateChange(EStateChangeType.PlayerLeave, new PlayerLeaveNotice
                    {
                        ServerPlayerIndex = _primaryAssignedServerPlayerIndex,
                        ClientId = EffectiveClientId,
                        Reason = reason,
                        SessionId = _activeSessionId,
                    }, compress: false);
                }

                if (_primaryAssignedServerPlayerIndex >= 0)
                {
                    _localServerIndices.Remove(_primaryAssignedServerPlayerIndex);
                    _primaryAssignedServerPlayerIndex = -1;
                    _primaryAssignedEntityId = NetworkEntityId.Empty;
                    _assignmentReceived = false;
                    _activeSessionId = Guid.Empty;
                    ResetReplicationSynchronization();
                }
            }

            private void HandlePlayerAssignment(PlayerAssignment assignment)
            {
                if (IsReplicationDisposed)
                    return;

                bool isLocal = string.Equals(assignment.ClientId, EffectiveClientId, StringComparison.OrdinalIgnoreCase);

                if (!isLocal && _activeSessionId != Guid.Empty && assignment.SessionId != _activeSessionId)
                    return;

                if (isLocal)
                {
                    // Headless native clients intentionally have no local controller to attach. The
                    // network assignment is still authoritative and must retain its session identity.
                    _activeSessionId = assignment.SessionId;
                    AttachAssignmentToLocalPlayer(assignment);
                    _assignmentReceived = true;
                    _primaryAssignedServerPlayerIndex = assignment.ServerPlayerIndex;
                    _primaryAssignedEntityId = assignment.PlayerEntityId;
                    _localServerIndices.Add(assignment.ServerPlayerIndex);
                    BeginReplicationSynchronization(assignment);
                    if (assignment.World is not null)
                        RuntimeNetworkingHostServices.Current.EnqueueSimulation(() => ApplyWorldDescriptor(assignment.World));
                    Debug.Networking(
                        "[Client] Realtime assignment accepted playerIndex={0}; session={1}; world={2}",
                        assignment.ServerPlayerIndex,
                        assignment.SessionId,
                        RealtimeJoinHandoffContract.DescribeWorldAsset(assignment.World?.Asset));
                    return;
                }

                var remote = GetOrCreateRemotePlayer(assignment.ServerPlayerIndex, assignment.DisplayName);
                if (remote is not null && !string.IsNullOrWhiteSpace(assignment.DisplayName) && remote.Pawn.SceneNode is not null)
                    remote.Pawn.SceneNode.Name = assignment.DisplayName;
            }

            private void AttachAssignmentToLocalPlayer(PlayerAssignment assignment)
            {
                foreach (var player in RuntimeNetworkingHostServices.Current.LocalPlayers)
                {
                    if (player is null)
                        continue;

                    if (player.PlayerInfo is not { } playerInfo)
                        continue;

                    if (playerInfo.ServerIndex == assignment.ServerPlayerIndex || playerInfo.ServerIndex < 0)
                    {
                        playerInfo.ServerIndex = assignment.ServerPlayerIndex;
                        playerInfo.LocalIndex ??= player.LocalPlayerIndex;
                        playerInfo.SessionId = assignment.SessionId;
                        playerInfo.NetworkEntityId = assignment.PlayerEntityId;
                        playerInfo.AuthorityLease = assignment.AuthorityLease;
                        _activeSessionId = assignment.SessionId;
                        _lastReceivedServerTickId = Math.Max(_lastReceivedServerTickId, assignment.ServerTickId);
                        _localServerIndices.Add(assignment.ServerPlayerIndex);
                        RemoveRemotePlayer(assignment.ServerPlayerIndex);
                        return;
                    }
                }

                if (assignment.World is not null)
                    ApplyWorldDescriptor(assignment.World);

            }

            private void ApplyWorldDescriptor(WorldSyncDescriptor descriptor)
            {
                IRuntimeNetworkWorldContext? worldInstance = ResolvePrimaryWorldInstance();
                if (worldInstance is null)
                {
                    worldInstance = EnsureClientWorld(descriptor);
                    if (worldInstance is null)
                        return;
                }

                if (!IsGameplayReady && worldInstance.WorldInstance is RuntimeWorld runtimeWorld)
                    runtimeWorld.PausePlay();

                if (!string.IsNullOrWhiteSpace(descriptor.WorldName) && worldInstance.TargetWorld is not null)
                    worldInstance.TargetWorld.Name = descriptor.WorldName!;

                EnsureClientGameModeBootstrap(worldInstance, descriptor);

                // Scene list replication is advisory for now; loading scenes requires asset context.
                WarnWhenWorldDiffers(descriptor);
            }

            private static IRuntimeNetworkWorldContext? EnsureClientWorld(WorldSyncDescriptor descriptor)
                => RuntimeNetworkingHostServices.Current.EnsureClientWorld(descriptor);

            private static void EnsureClientGameModeBootstrap(IRuntimeNetworkWorldContext worldInstance, WorldSyncDescriptor descriptor)
            {
                if (string.IsNullOrWhiteSpace(descriptor.WorldBootstrapId))
                {
                    if (worldInstance.GameMode is null)
                        Debug.Out($"[Client] Server world descriptor did not include a bootstrap id; keeping the local default game mode. GameModeType hint: {descriptor.GameModeType ?? "<none>"}.");
                    return;
                }

                if (!GameModeBootstrapRegistry.TryResolveBootstrapId(descriptor.WorldBootstrapId, out string? bootstrapId) || string.IsNullOrWhiteSpace(bootstrapId))
                {
                    Debug.Out($"[Client] Unable to resolve game mode bootstrap id '{descriptor.WorldBootstrapId}'. GameModeType hint: {descriptor.GameModeType ?? "<none>"}.");
                    return;
                }

                if (GameModeBootstrapRegistry.TryGetBootstrapId(worldInstance.GameMode, out string? activeBootstrapId)
                    && string.Equals(activeBootstrapId, bootstrapId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                try
                {
                    if (!GameModeBootstrapRegistry.TryCreate(bootstrapId, out GameMode? gameMode) || gameMode is null)
                    {
                        Debug.Out($"[Client] Failed to create game mode for bootstrap id '{bootstrapId}'.");
                        return;
                    }

                    worldInstance.GameMode = gameMode;
                    gameMode.WorldInstance = worldInstance.WorldInstance;
                }
                catch (Exception ex)
                {
                    Debug.Out($"[Client] Failed to instantiate game mode for bootstrap id '{bootstrapId}': {ex.Message}");
                }
            }

            private void HandlePlayerLeave(PlayerLeaveNotice leave)
            {
                if (_activeSessionId != Guid.Empty && leave.SessionId != _activeSessionId)
                    return;

                // If this leave refers to a local player, clear the server index
                foreach (var player in RuntimeNetworkingHostServices.Current.LocalPlayers)
                {
                    if (player is null)
                        continue;

                    if (player.PlayerInfo is not { } playerInfo)
                        continue;

                    if (playerInfo.ServerIndex == leave.ServerPlayerIndex)
                    {
                        playerInfo.ServerIndex = -1;
                        playerInfo.NetworkEntityId = NetworkEntityId.Empty;
                        playerInfo.AuthorityLease = null;
                        _localServerIndices.Remove(leave.ServerPlayerIndex);
                    }
                }

                if (leave.ServerPlayerIndex == _primaryAssignedServerPlayerIndex)
                {
                    _localServerIndices.Remove(leave.ServerPlayerIndex);
                    _primaryAssignedServerPlayerIndex = -1;
                    _primaryAssignedEntityId = NetworkEntityId.Empty;
                    _assignmentReceived = false;
                    _activeSessionId = Guid.Empty;
                    ResetReplicationSynchronization();
                }

                // Remove remote avatar if present
                RemoveRemotePlayer(leave.ServerPlayerIndex);
            }

            private void HandleServerError(ServerErrorMessage error)
            {
                if (!string.IsNullOrWhiteSpace(error.ClientId) && !string.Equals(error.ClientId, EffectiveClientId, StringComparison.OrdinalIgnoreCase))
                    return;

                if (error.ServerPlayerIndex is int idx && !_localServerIndices.Contains(idx) && !string.IsNullOrWhiteSpace(error.ClientId))
                    return;

                string title = string.IsNullOrWhiteSpace(error.Title) ? "Server Error" : error.Title;
                LastServerError = new ServerErrorMessage
                {
                    StatusCode = error.StatusCode,
                    Title = title,
                    Detail = error.Detail,
                    ClientId = error.ClientId,
                    ServerPlayerIndex = error.ServerPlayerIndex,
                    RequestId = error.RequestId,
                    Fatal = error.Fatal,
                };
                Debug.Out($"[Client][Error {error.StatusCode}] {title}: {error.Detail}");

                if (error.Fatal)
                {
                    foreach (var player in RuntimeNetworkingHostServices.Current.LocalPlayers)
                    {
                        if (player is null)
                            continue;

                        if (player.PlayerInfo is { } playerInfo)
                        {
                            playerInfo.ServerIndex = -1;
                            playerInfo.NetworkEntityId = NetworkEntityId.Empty;
                            playerInfo.AuthorityLease = null;
                        }
                    }
                    _localServerIndices.Clear();
                    ClearRemotePlayers();
                    _assignmentReceived = false;
                    _primaryAssignedServerPlayerIndex = -1;
                    _primaryAssignedEntityId = NetworkEntityId.Empty;
                    _activeSessionId = Guid.Empty;
                    ResetReplicationSynchronization();
                }
            }

            private void HandleRemoteTransform(PlayerTransformUpdate update)
            {
                if (_activeSessionId != Guid.Empty && update.SessionId != _activeSessionId)
                    return;

                if (_localServerIndices.Contains(update.ServerPlayerIndex))
                {
                    ApplyServerCorrectionToLocal(update);
                    return;
                }

                var remote = GetOrCreateRemotePlayer(update.ServerPlayerIndex);
                remote?.Controller.ApplyNetworkTransform(update);
            }

            private void ApplyServerCorrectionToLocal(PlayerTransformUpdate update)
            {
                _lastReceivedServerTickId = Math.Max(_lastReceivedServerTickId, update.ServerTickId);
                _lastProcessedInputSequence = Math.Max(_lastProcessedInputSequence, update.LastProcessedInputSequence);
                if (!update.IsServerCorrection)
                    return;

                foreach (var player in RuntimeNetworkingHostServices.Current.LocalPlayers)
                {
                    if (player?.PlayerInfo is not { } playerInfo || playerInfo.ServerIndex != update.ServerPlayerIndex)
                        continue;

                    if (player.ControlledPawnComponent?.SceneNode?.Transform is Transform transform)
                        RecordPredictionCorrection(transform.Translation, update.Translation);
                    player.ApplyNetworkTransform(update);
                    ReplayPredictedInputs(player, update);
                    return;
                }
            }

            private void HandleAuthorityLeaseUpdate(NetworkAuthorityLease lease)
            {
                if (_activeSessionId != Guid.Empty && lease.SessionId != _activeSessionId)
                    return;

                foreach (var player in RuntimeNetworkingHostServices.Current.LocalPlayers)
                {
                    if (player?.PlayerInfo is not { } playerInfo)
                        continue;

                    if (playerInfo.ServerIndex != lease.OwnerServerPlayerIndex)
                        continue;

                    playerInfo.AuthorityLease = lease;
                    if (lease.IsRevoked)
                        playerInfo.NetworkEntityId = NetworkEntityId.Empty;
                    else
                        playerInfo.NetworkEntityId = lease.EntityId;
                }
            }

            private void HandleClockSync(ClockSyncMessage clock)
            {
                if (!string.Equals(clock.ClientId, EffectiveClientId, StringComparison.OrdinalIgnoreCase))
                    return;

                if (_activeSessionId != Guid.Empty && clock.SessionId != _activeSessionId)
                    return;

                double receiveUtc = GetUtcSeconds();
                double midpoint = (clock.ClientSendTimestampUtc + receiveUtc) * 0.5d;
                _clockOffsetSeconds = clock.ServerSendTimestampUtc - midpoint;
                _lastReceivedServerTickId = Math.Max(_lastReceivedServerTickId, clock.ServerTickId);
            }

            protected override void PrepareOutgoingHumanoidPoseFrame(HumanoidPoseFrame frame)
            {
                if (!IsGameplayReady)
                {
                    // Base exposes pose broadcast to renderer/XR callers. Keep the frame
                    // structurally invalid until synchronization permits gameplay traffic.
                    frame.SessionId = Guid.Empty;
                    frame.SourceClientId = string.Empty;
                    frame.EntityIds = [];
                    return;
                }
                if (frame.SessionId == Guid.Empty)
                    frame.SessionId = _activeSessionId;
                if (string.IsNullOrWhiteSpace(frame.SourceClientId))
                    frame.SourceClientId = EffectiveClientId;
                if (frame.FrameSequence == 0)
                    frame.FrameSequence = ++_poseFrameSequence;
                if (frame.EntityIds.Length == 0)
                    frame.EntityIds = GetLocalNetworkEntityIds();
                frame.AuthorityMode = NetworkAuthorityMode.ClientPredicted;
                frame.Channel = NetworkReplicationChannel.HumanoidPose;

                if (!IsManagedTransportRequested)
                    return;

                if (_primaryAssignedServerPlayerIndex is <= 0 or > ushort.MaxValue
                    || _primaryAssignedEntityId.IsEmpty
                    || frame.AvatarCount != 1
                    || frame.Payload.Length < 6
                    || frame.BaselineSequence == 0)
                {
                    RejectManagedPoseFrame(frame);
                    return;
                }

                // Managed pose traffic has one stable avatar, bound by the server-assigned
                // player index. The server re-parses the complete frame before accepting it.
                BinaryPrimitives.WriteUInt16LittleEndian(frame.Payload, (ushort)_primaryAssignedServerPlayerIndex);
                frame.EntityIds = [_primaryAssignedEntityId];
            }

            private void RejectManagedPoseFrame(HumanoidPoseFrame frame)
            {
                frame.SessionId = Guid.Empty;
                frame.SourceClientId = string.Empty;
                frame.EntityIds = [];
            }

            private NetworkEntityId[] GetLocalNetworkEntityIds()
            {
                List<NetworkEntityId> ids = [];
                foreach (var player in RuntimeNetworkingHostServices.Current.LocalPlayers)
                {
                    if (player?.PlayerInfo is { NetworkEntityId.IsEmpty: false } playerInfo)
                        ids.Add(playerInfo.NetworkEntityId);
                }

                if (ids.Count == 0 && !_primaryAssignedEntityId.IsEmpty)
                    ids.Add(_primaryAssignedEntityId);

                return [.. ids];
            }

            private RemotePlayerState? GetOrCreateRemotePlayer(int serverPlayerIndex, string? displayName = null, IRuntimeNetworkWorldContext? preferredWorld = null)
            {
                if (_localServerIndices.Contains(serverPlayerIndex))
                    return null;

                if (_remotePlayers.TryGetValue(serverPlayerIndex, out var existing))
                {
                    UpdateRemoteDisplayName(existing, displayName);
                    return existing;
                }

                IRuntimeNetworkWorldContext? worldInstance = preferredWorld ?? ResolvePrimaryWorldInstance() ?? EnsureClientWorld(new WorldSyncDescriptor());
                if (worldInstance is null)
                    return null;

                var pawn = CreateRemotePawn(worldInstance, serverPlayerIndex, displayName);
                if (pawn is null)
                    return null;

                var controller = RuntimeNetworkingHostServices.Current.CreateRemotePlayer(serverPlayerIndex);
                if (controller is null)
                    return null;

                controller.ControlledPawnComponent = pawn;

                RuntimeNetworkingHostServices.Current.AddRemotePlayer(controller);

                var remote = new RemotePlayerState(serverPlayerIndex, controller, pawn, worldInstance, _replicationWorldLease.Token);
                _remotePlayers[serverPlayerIndex] = remote;
                return remote;
            }

            private static PawnComponent? CreateRemotePawn(IRuntimeNetworkWorldContext worldInstance, int serverPlayerIndex, string? displayName)
                => worldInstance.CreateRemotePawn(serverPlayerIndex, displayName, serverOwned: false);

            private void RemoveRemotePlayer(int serverPlayerIndex)
            {
                if (!_remotePlayers.TryGetValue(serverPlayerIndex, out var remote))
                    return;

                DestroyRemotePlayer(remote);
                _remotePlayers.Remove(serverPlayerIndex);
            }

            private void ClearRemotePlayers(IRuntimeNetworkWorldContext? worldContext = null, bool clearAll = true, long? bindingGeneration = null)
            {
                foreach (var remote in _remotePlayers.Values.Where(remote => clearAll
                    || ReferenceEquals(remote.WorldContext, worldContext) && (!bindingGeneration.HasValue || remote.ReplicationBindingGeneration == bindingGeneration.Value)).ToArray())
                {
                    DestroyRemotePlayer(remote);
                    _remotePlayers.Remove(remote.ServerPlayerIndex);
                }

                if (clearAll)
                    _remotePlayers.Clear();
            }

            private static void DestroyRemotePlayer(RemotePlayerState remote)
            {
                remote.WorldContext.DestroyPawn(remote.Pawn);
                RuntimeNetworkingHostServices.Current.RemoveRemotePlayer(remote.Controller);
                if (remote.Controller is XRObjectBase controllerObj)
                    controllerObj.Destroy();
            }

            private static void UpdateRemoteDisplayName(RemotePlayerState remote, string? displayName)
            {
                if (string.IsNullOrWhiteSpace(displayName))
                    return;

                if (remote.Pawn.SceneNode is { } node)
                    node.Name = displayName;
            }

            private static IRuntimeNetworkWorldContext? ResolvePrimaryWorldInstance()
                => RuntimeNetworkingHostServices.Current.ResolvePrimaryWorld();

            private static WorldAssetIdentity? CreateLocalWorldAsset()
            {
                IRuntimeNetworkWorldContext? worldInstance = ResolvePrimaryWorldInstance();
                return worldInstance?.TargetWorld is null
                    ? null
                    : WorldAssetIdentityProvider.Create(worldInstance.TargetWorld, CurrentProtocolVersion);
            }

            private static double GetUtcSeconds()
                => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

            private static void WarnWhenWorldDiffers(WorldSyncDescriptor descriptor)
            {
                IRuntimeNetworkWorldContext? worldInstance = ResolvePrimaryWorldInstance();
                XRWorld? world = worldInstance?.TargetWorld;
                if (world is null || string.IsNullOrWhiteSpace(descriptor.WorldName))
                    return;

                if (!string.Equals(world.Name, descriptor.WorldName, StringComparison.OrdinalIgnoreCase))
                    Debug.Out($"[Client] Connected to server world '{descriptor.WorldName}', but local world is '{world.Name}'.");

                WorldAssetIdentity? localAsset = CreateLocalWorldAsset();
                if (descriptor.Asset is not null && localAsset is not null && !localAsset.IsSameAssetAs(descriptor.Asset))
                    Debug.Out("[Client] Server world asset identity differs from the local world asset identity.");
            }

            private sealed class RemotePlayerState
            {
                public RemotePlayerState(int serverPlayerIndex, IPawnController controller, PawnComponent pawn, IRuntimeNetworkWorldContext worldContext, long replicationBindingGeneration)
                {
                    ServerPlayerIndex = serverPlayerIndex;
                    Controller = controller;
                    Pawn = pawn;
                    WorldContext = worldContext;
                    ReplicationBindingGeneration = replicationBindingGeneration;
                }

                public int ServerPlayerIndex { get; }
                public IPawnController Controller { get; }
                public PawnComponent Pawn { get; }
                public IRuntimeNetworkWorldContext WorldContext { get; }
                public long ReplicationBindingGeneration { get; }
            }

            ~ClientNetworkingManager()
            {
                if (_tickRegistered)
                    RuntimeTimingServices.Current.Update -= TickClientNetwork;
            }

        }

}
