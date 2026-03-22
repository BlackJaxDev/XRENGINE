using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics.CodeAnalysis;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Input;
using XREngine.Networking;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine
{
    public class ClientNetworkingManager : BaseNetworkingManager
        {
            public override bool IsServer => false;
            public override bool IsClient => true;
            public override bool IsP2P => false;

            private readonly string _clientId = Guid.NewGuid().ToString("N");
            private bool _joinRequested;
            private bool _tickRegistered;
            private bool _assignmentReceived;
            private long _lastInputSyncTicks;
            private long _lastTransformSyncTicks;
            private long _lastJoinRequestTicks;
            private long _lastHeartbeatTicks;
            private Guid _activeInstanceId = Guid.Empty;
            private const double InputSyncIntervalSeconds = 1.0 / 60.0;
            private const double TransformSyncIntervalSeconds = 1.0 / 20.0;
            private const double JoinRetrySeconds = 3.0;
            private const double HeartbeatIntervalSeconds = 3.0;
            private readonly Dictionary<int, RemotePlayerState> _remotePlayers = new();
            private readonly HashSet<int> _localServerIndices = new();

            public ClientNetworkingManager() : base(peerId: null)
            {
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    SendPlayerLeaveForLocals("Client disposed");

                    if (_tickRegistered)
                    {
                        Engine.Time.Timer.UpdateFrame -= TickClientNetwork;
                        _tickRegistered = false;
                    }

                    ClearRemotePlayers();
                }

                base.Dispose(disposing);
            }

            /// <summary>
            /// The server IP to send to.
            /// </summary>
            public IPEndPoint? ServerIP { get; set; }
            /// <summary>
            /// Sends from client to server.
            /// </summary>
            public UdpClient? UdpSender { get; set; }

            public void Start(
                IPAddress udpMulticastGroupIP,
                int udpMulticastPort,
                IPAddress serverIP,
                int udpSendPort)
            {
                Debug.Out($"Starting client with udp(multicast: {udpMulticastGroupIP}:{udpMulticastPort}) sending to server at ({serverIP}:{udpSendPort})");
                StartUdpMulticastReceiver(serverIP, udpMulticastGroupIP, udpMulticastPort);
                StartUdpSender(serverIP, udpMulticastPort);
                EnsureClientTick();
                SendJoinRequest();
            }

            protected void StartUdpSender(IPAddress serverIP, int udpMulticastServerPort)
            {
                UdpSender = new UdpClient();
                ServerIP = new IPEndPoint(serverIP, udpMulticastServerPort);
                //UdpSender.Connect(ServerIP);
            }

            protected override async Task SendUDP()
            {
                //Send to server
                await ConsumeAndSendUDPQueue(UdpSender, ServerIP);
            }

            public override void ConsumeQueues()
            {
                base.ConsumeQueues();

                // If the UDP connection drops, proactively send leave once
                if (!UDPServerConnectionEstablished && _assignmentReceived)
                {
                    SendPlayerLeaveForLocals("UDP disconnected");
                    _assignmentReceived = false;
                }
            }

            protected override void HandleStateChange(StateChangeInfo change, IPEndPoint? sender)
            {
                if (change.Type is EStateChangeType.RemoteJobRequest or EStateChangeType.RemoteJobResponse or EStateChangeType.HumanoidPoseFrame)
                {
                    base.HandleStateChange(change, sender);
                    return;
                }

                switch (change.Type)
                {
                    case EStateChangeType.PlayerAssignment:
                        if (StateChangePayloadSerializer.TryDeserialize<PlayerAssignment>(change.Data, out var assignment) && assignment is not null)
                        {
#pragma warning disable IL2026
                            HandlePlayerAssignment(assignment);
#pragma warning restore IL2026
                        }
                        break;
                    case EStateChangeType.PlayerTransformUpdate:
                        if (StateChangePayloadSerializer.TryDeserialize<PlayerTransformUpdate>(change.Data, out var transformUpdate) && transformUpdate is not null)
                            HandleRemoteTransform(transformUpdate);
                        break;
                    case EStateChangeType.PlayerLeave:
                        if (StateChangePayloadSerializer.TryDeserialize<PlayerLeaveNotice>(change.Data, out var leave) && leave is not null)
                            HandlePlayerLeave(leave);
                        break;
                    case EStateChangeType.ServerError:
                        if (StateChangePayloadSerializer.TryDeserialize<ServerErrorMessage>(change.Data, out var error) && error is not null)
                            HandleServerError(error);
                        break;
                }
            }

            private void EnsureClientTick()
            {
                if (_tickRegistered)
                    return;

                Engine.Time.Timer.UpdateFrame += TickClientNetwork;
                _tickRegistered = true;
            }

            private void TickClientNetwork()
            {
                if (!UDPServerConnectionEstablished)
                    return;

                long nowTicks = CurrentEngineTicks();

                if (!_assignmentReceived && (!_joinRequested || HasElapsed(nowTicks, _lastJoinRequestTicks, JoinRetrySeconds)))
                    SendJoinRequest();

                if (HasElapsed(nowTicks, _lastInputSyncTicks, InputSyncIntervalSeconds))
                {
                    SendLocalInputSnapshots();
                    _lastInputSyncTicks = nowTicks;
                }

                if (HasElapsed(nowTicks, _lastTransformSyncTicks, TransformSyncIntervalSeconds))
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
                PlayerJoinRequest request = new()
                {
                    ClientId = _clientId,
                    DisplayName = Environment.UserName,
                    BuildVersion = CurrentProtocolVersion,
                    WorldName = ResolvePrimaryWorldInstance()?.TargetWorld?.Name,
                    InstanceId = _activeInstanceId == Guid.Empty ? null : _activeInstanceId
                };

                BroadcastStateChange(EStateChangeType.PlayerJoin, request, compress: true);
                _joinRequested = true;
                _lastJoinRequestTicks = CurrentEngineTicks();
            }

            private void SendHeartbeat()
            {
                foreach (var player in Engine.State.LocalPlayers)
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
                        ClientId = _clientId,
                        TimestampUtc = GetUtcSeconds(),
                        InstanceId = _activeInstanceId == Guid.Empty ? null : _activeInstanceId
                    };

                    BroadcastStateChange(EStateChangeType.Heartbeat, heartbeat, compress: false);
                }
            }

            private void SendLocalInputSnapshots()
            {
                foreach (var player in Engine.State.LocalPlayers)
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

                    var snapshot = new PlayerInputSnapshot
                    {
                        ServerPlayerIndex = serverIndex,
                        Input = pawn.CaptureNetworkInputState(),
                        TimestampUtc = GetUtcSeconds(),
                        InstanceId = playerInfo.InstanceId
                    };

                    BroadcastStateChange(EStateChangeType.PlayerInputSnapshot, snapshot, compress: true);
                }
            }

            private void SendLocalTransformSnapshots()
            {
                foreach (var player in Engine.State.LocalPlayers)
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
                        TransformId = transform.ID,
                        Translation = transform.Translation,
                        Rotation = transform.Rotation,
                        Velocity = Vector3.Zero,
                        InstanceId = playerInfo.InstanceId
                    };

                    BroadcastStateChange(EStateChangeType.PlayerTransformUpdate, update, compress: false);
                }
            }

            private void SendPlayerLeaveForLocals(string reason)
            {
                foreach (var player in Engine.State.LocalPlayers)
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
                        ClientId = _clientId,
                        Reason = reason,
                        InstanceId = playerInfo.InstanceId
                    };

                    BroadcastStateChange(EStateChangeType.PlayerLeave, leave, compress: false);

                    playerInfo.ServerIndex = -1;
                    _localServerIndices.Remove(serverIndex);
                }
            }

            [RequiresUnreferencedCode("World/GameMode reflection for networking sync")]
            private void HandlePlayerAssignment(PlayerAssignment assignment)
            {
                bool isLocal = string.Equals(assignment.ClientId, _clientId, StringComparison.OrdinalIgnoreCase);

                if (assignment.World is not null)
                    ApplyWorldDescriptor(assignment.World);

                if (!isLocal && _activeInstanceId != Guid.Empty && assignment.InstanceId != _activeInstanceId)
                    return;

                if (isLocal)
                {
                    AttachAssignmentToLocalPlayer(assignment);
                    _assignmentReceived = true;
                    return;
                }

                var remote = GetOrCreateRemotePlayer(assignment.ServerPlayerIndex, assignment.DisplayName);
                if (remote is not null && !string.IsNullOrWhiteSpace(assignment.DisplayName) && remote.Pawn.SceneNode is not null)
                    remote.Pawn.SceneNode.Name = assignment.DisplayName;
            }

            private void AttachAssignmentToLocalPlayer(PlayerAssignment assignment)
            {
                foreach (var player in Engine.State.LocalPlayers)
                {
                    if (player is null)
                        continue;

                    if (player.PlayerInfo is not { } playerInfo)
                        continue;

                    if (playerInfo.ServerIndex == assignment.ServerPlayerIndex || playerInfo.ServerIndex < 0)
                    {
                        playerInfo.ServerIndex = assignment.ServerPlayerIndex;
                        playerInfo.LocalIndex ??= player.LocalPlayerIndex;
                        playerInfo.InstanceId = assignment.InstanceId;
                        _activeInstanceId = assignment.InstanceId;
                        _localServerIndices.Add(assignment.ServerPlayerIndex);
                        RemoveRemotePlayer(assignment.ServerPlayerIndex);
                        return;
                    }
                }
            }

            [RequiresUnreferencedCode("World/GameMode reflection for networking sync")]
            private void ApplyWorldDescriptor(WorldSyncDescriptor descriptor)
            {
                XRWorldInstance? worldInstance = ResolvePrimaryWorldInstance();
                if (worldInstance is null)
                {
                    worldInstance = EnsureClientWorld(descriptor);
                    if (worldInstance is null)
                        return;
                }

                if (!string.IsNullOrWhiteSpace(descriptor.WorldName) && worldInstance.TargetWorld is not null)
                    worldInstance.TargetWorld.Name = descriptor.WorldName!;

                if (!string.IsNullOrWhiteSpace(descriptor.GameModeType))
                    EnsureClientGameMode(worldInstance, descriptor.GameModeType!);

                // Scene list replication is advisory for now; loading scenes requires asset context.
                WarnWhenWorldDiffers(descriptor);
            }

            private static XRWorldInstance? EnsureClientWorld(WorldSyncDescriptor descriptor)
            {
                XRWorld world = new()
                {
                    Name = string.IsNullOrWhiteSpace(descriptor.WorldName) ? "RemoteWorld" : descriptor.WorldName!
                };

                var instance = XRWorldInstance.GetOrInitWorld(world);

                foreach (var window in Engine.Windows)
                {
                    if (window is null)
                        continue;

                    window.TargetWorldInstance ??= instance;
                    break;
                }

                return instance;
            }

            [RequiresUnreferencedCode("Game mode reflection for networking sync")]
            private static void EnsureClientGameMode(XRWorldInstance worldInstance, string gameModeTypeName)
            {
                var gmType = ResolveTypeIgnoreCase(gameModeTypeName);
                if (gmType is null || !typeof(GameMode).IsAssignableFrom(gmType))
                {
                    Debug.Out($"[Client] Unable to resolve game mode type '{gameModeTypeName}'.");
                    return;
                }

                if (worldInstance.GameMode is not null && worldInstance.GameMode.GetType() == gmType)
                    return;

                try
                {
                    var gameMode = (GameMode?)Activator.CreateInstance(gmType);
                    if (gameMode is null)
                        return;

                    worldInstance.GameMode = gameMode;
                    gameMode.WorldInstance = worldInstance;
                }
                catch (Exception ex)
                {
                    Debug.Out($"[Client] Failed to instantiate game mode '{gameModeTypeName}': {ex.Message}");
                }
            }

            [RequiresUnreferencedCode("Type resolution via reflection for networking sync")]
            private static Type? ResolveTypeIgnoreCase(string typeName)
            {
                Type? resolved = AotRuntimeMetadataStore.ResolveTypeIgnoreCase(typeName);
                if (resolved is not null)
                    return resolved;

                if (XRRuntimeEnvironment.IsAotRuntimeBuild)
                    return null;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? match = null;
                    try
                    {
                        match = asm.GetTypes().FirstOrDefault(t => string.Equals(t.FullName, typeName, StringComparison.OrdinalIgnoreCase));
                    }
                    catch
                    {
                        // ignore reflection type load issues
                    }

                    if (match is not null)
                        return match;
                }

                return null;
            }

            private void HandlePlayerLeave(PlayerLeaveNotice leave)
            {
                if (_activeInstanceId != Guid.Empty && leave.InstanceId != _activeInstanceId)
                    return;

                // If this leave refers to a local player, clear the server index
                foreach (var player in Engine.State.LocalPlayers)
                {
                    if (player is null)
                        continue;

                    if (player.PlayerInfo is not { } playerInfo)
                        continue;

                    if (playerInfo.ServerIndex == leave.ServerPlayerIndex)
                    {
                        playerInfo.ServerIndex = -1;
                        _localServerIndices.Remove(leave.ServerPlayerIndex);
                    }
                }

                // Remove remote avatar if present
                RemoveRemotePlayer(leave.ServerPlayerIndex);
            }

            private void HandleServerError(ServerErrorMessage error)
            {
                if (!string.IsNullOrWhiteSpace(error.ClientId) && !string.Equals(error.ClientId, _clientId, StringComparison.OrdinalIgnoreCase))
                    return;

                if (error.ServerPlayerIndex is int idx && !_localServerIndices.Contains(idx) && !string.IsNullOrWhiteSpace(error.ClientId))
                    return;

                string title = string.IsNullOrWhiteSpace(error.Title) ? "Server Error" : error.Title;
                Debug.Out($"[Client][Error {error.StatusCode}] {title}: {error.Detail}");

                if (error.Fatal)
                {
                    foreach (var player in Engine.State.LocalPlayers)
                    {
                        if (player is null)
                            continue;

                        if (player.PlayerInfo is { } playerInfo)
                            playerInfo.ServerIndex = -1;
                    }
                    _localServerIndices.Clear();
                    ClearRemotePlayers();
                    _assignmentReceived = false;
                    _activeInstanceId = Guid.Empty;
                }
            }

            private void HandleRemoteTransform(PlayerTransformUpdate update)
            {
                if (_activeInstanceId != Guid.Empty && update.InstanceId != _activeInstanceId)
                    return;

                if (_localServerIndices.Contains(update.ServerPlayerIndex))
                    return;

                var remote = GetOrCreateRemotePlayer(update.ServerPlayerIndex);
                remote?.Controller.ApplyNetworkTransform(update);
            }

            private RemotePlayerState? GetOrCreateRemotePlayer(int serverPlayerIndex, string? displayName = null)
            {
                if (_localServerIndices.Contains(serverPlayerIndex))
                    return null;

                if (_remotePlayers.TryGetValue(serverPlayerIndex, out var existing))
                {
                    UpdateRemoteDisplayName(existing, displayName);
                    return existing;
                }

                XRWorldInstance? worldInstance = ResolvePrimaryWorldInstance() ?? EnsureClientWorld(new WorldSyncDescriptor());
                if (worldInstance is null)
                    return null;

                var pawn = CreateRemotePawn(worldInstance, serverPlayerIndex, displayName);
                if (pawn is null)
                    return null;

                var controller = Engine.State.InstantiateRemoteController(serverPlayerIndex);
                if (controller is null)
                    return null;

                controller.ControlledPawnComponent = pawn;

                if (!Engine.State.RemotePlayers.Contains(controller))
                    Engine.State.RemotePlayers.Add(controller);

                var remote = new RemotePlayerState(serverPlayerIndex, controller, pawn);
                _remotePlayers[serverPlayerIndex] = remote;
                return remote;
            }

            private static PawnComponent? CreateRemotePawn(XRWorldInstance worldInstance, int serverPlayerIndex, string? displayName)
            {
                var pawnType = worldInstance.GameMode?.PlayerPawnClass ?? typeof(FlyingCameraPawnComponent);
                if (pawnType is null)
                    return null;

                var nodeName = string.IsNullOrWhiteSpace(displayName) ? $"RemotePlayer_{serverPlayerIndex}" : displayName!;
                var node = new SceneNode(worldInstance, nodeName);

                if (node.AddComponent(pawnType) is not PawnComponent pawn)
                {
                    node.Destroy();
                    return null;
                }

                worldInstance.RootNodes.Add(node);
                return pawn;
            }

            private void RemoveRemotePlayer(int serverPlayerIndex)
            {
                if (!_remotePlayers.TryGetValue(serverPlayerIndex, out var remote))
                    return;

                DestroyRemotePlayer(remote);
                _remotePlayers.Remove(serverPlayerIndex);
            }

            private void ClearRemotePlayers()
            {
                foreach (var remote in _remotePlayers.Values)
                    DestroyRemotePlayer(remote);

                _remotePlayers.Clear();
            }

            private static void DestroyRemotePlayer(RemotePlayerState remote)
            {
                if (remote.Pawn.SceneNode is { } node)
                {
                    if (node.World is XRWorldInstance nodeWorld)
                        nodeWorld.RootNodes.Remove(node);

                    node.Destroy();
                }

                Engine.State.RemotePlayers.Remove(remote.Controller);
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

            private static XRWorldInstance? ResolvePrimaryWorldInstance()
            {
                foreach (var window in Engine.Windows)
                {
                    if (window?.TargetWorldInstance is not null)
                        return window.TargetWorldInstance;
                }

                return XRWorldInstance.WorldInstances.Values.FirstOrDefault();
            }

            private static double GetUtcSeconds()
                => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

            private static void WarnWhenWorldDiffers(WorldSyncDescriptor descriptor)
            {
                XRWorldInstance? worldInstance = ResolvePrimaryWorldInstance();
                XRWorld? world = worldInstance?.TargetWorld;
                if (world is null || string.IsNullOrWhiteSpace(descriptor.WorldName))
                    return;

                if (!string.Equals(world.Name, descriptor.WorldName, StringComparison.OrdinalIgnoreCase))
                    Debug.Out($"[Client] Connected to server world '{descriptor.WorldName}', but local world is '{world.Name}'.");
            }

            private sealed class RemotePlayerState
            {
                public RemotePlayerState(int serverPlayerIndex, IPawnController controller, PawnComponent pawn)
                {
                    ServerPlayerIndex = serverPlayerIndex;
                    Controller = controller;
                    Pawn = pawn;
                }

                public int ServerPlayerIndex { get; }
                public IPawnController Controller { get; }
                public PawnComponent Pawn { get; }
            }

            ~ClientNetworkingManager()
            {
                if (_tickRegistered)
                    Engine.Time.Timer.UpdateFrame -= TickClientNetwork;
            }

        }

}
