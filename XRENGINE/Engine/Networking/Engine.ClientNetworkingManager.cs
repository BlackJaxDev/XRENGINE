using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Net;
using System.Net.Sockets;
using XREngine.Components;
using XREngine.Networking;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine
{
    public static partial class Engine
    {
        public class ClientNetworkingManager : BaseNetworkingManager
        {
            public override bool IsServer => false;
            public override bool IsClient => true;
            public override bool IsP2P => false;

            private readonly string _clientId = Guid.NewGuid().ToString("N");
            private bool _joinRequested;
            private bool _tickRegistered;
            private double _lastInputSyncTime;
            private double _lastTransformSyncTime;
            private const double InputSyncIntervalSeconds = 1.0 / 60.0;
            private const double TransformSyncIntervalSeconds = 1.0 / 20.0;
            private readonly Dictionary<int, RemotePlayerAvatar> _remotePlayers = new();
            private readonly HashSet<int> _localServerIndices = new();

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

            protected override void HandleStateChange(StateChangeInfo change, IPEndPoint? sender)
            {
                switch (change.Type)
                {
                    case EStateChangeType.PlayerAssignment:
                        if (StateChangePayloadSerializer.TryDeserialize<PlayerAssignment>(change.Data, out var assignment))
                            HandlePlayerAssignment(assignment);
                        break;
                    case EStateChangeType.PlayerTransformUpdate:
                        if (StateChangePayloadSerializer.TryDeserialize<PlayerTransformUpdate>(change.Data, out var transformUpdate))
                            HandleRemoteTransform(transformUpdate);
                        break;
                }
            }

            private void EnsureClientTick()
            {
                if (_tickRegistered)
                    return;

                Time.Timer.UpdateFrame += TickClientNetwork;
                _tickRegistered = true;
            }

            private void TickClientNetwork()
            {
                if (!UDPServerConnectionEstablished)
                    return;

                double now = Engine.ElapsedTime;

                if (!_joinRequested)
                    SendJoinRequest();

                if (now - _lastInputSyncTime >= InputSyncIntervalSeconds)
                {
                    SendLocalInputSnapshots();
                    _lastInputSyncTime = now;
                }

                if (now - _lastTransformSyncTime >= TransformSyncIntervalSeconds)
                {
                    SendLocalTransformSnapshots();
                    _lastTransformSyncTime = now;
                }
            }

            private void SendJoinRequest()
            {
                PlayerJoinRequest request = new()
                {
                    ClientId = _clientId,
                    DisplayName = Environment.UserName,
                    BuildVersion = "dev",
                    WorldName = ResolvePrimaryWorldInstance()?.TargetWorld?.Name
                };

                BroadcastStateChange(EStateChangeType.PlayerJoin, request, compress: true);
                _joinRequested = true;
            }

            private void SendLocalInputSnapshots()
            {
                foreach (var player in State.LocalPlayers)
                {
                    if (player is null)
                        continue;

                    int serverIndex = player.PlayerInfo.ServerIndex;
                    if (serverIndex < 0)
                        continue;

                    if (player.ControlledPawn is not CharacterPawnComponent pawn)
                        continue;

                    var snapshot = new PlayerInputSnapshot
                    {
                        ServerPlayerIndex = serverIndex,
                        Input = pawn.CaptureNetworkInputState(),
                        TimestampUtc = GetUtcSeconds()
                    };

                    BroadcastStateChange(EStateChangeType.PlayerInputSnapshot, snapshot, compress: true);
                }
            }

            private void SendLocalTransformSnapshots()
            {
                foreach (var player in State.LocalPlayers)
                {
                    if (player is null)
                        continue;

                    int serverIndex = player.PlayerInfo.ServerIndex;
                    if (serverIndex < 0)
                        continue;

                    Transform? transform = player.ControlledPawn?.SceneNode?.Transform as Transform;
                    if (transform is null)
                        continue;

                    PlayerTransformUpdate update = new()
                    {
                        ServerPlayerIndex = serverIndex,
                        TransformId = transform.ID,
                        Translation = transform.Translation,
                        Rotation = transform.Rotation,
                        Velocity = Vector3.Zero
                    };

                    BroadcastStateChange(EStateChangeType.PlayerTransformUpdate, update, compress: false);
                }
            }

            private void HandlePlayerAssignment(PlayerAssignment assignment)
            {
                bool isLocal = string.Equals(assignment.ClientId, _clientId, StringComparison.OrdinalIgnoreCase);

                if (assignment.World is not null)
                    WarnWhenWorldDiffers(assignment.World);

                if (isLocal)
                {
                    AttachAssignmentToLocalPlayer(assignment);
                    return;
                }

                var avatar = GetOrCreateRemoteAvatar(assignment.ServerPlayerIndex);
                if (avatar is not null && !string.IsNullOrWhiteSpace(assignment.DisplayName))
                    avatar.Node.Name = assignment.DisplayName;
            }

            private void AttachAssignmentToLocalPlayer(PlayerAssignment assignment)
            {
                foreach (var player in State.LocalPlayers)
                {
                    if (player is null)
                        continue;

                    if (player.PlayerInfo.ServerIndex == assignment.ServerPlayerIndex || player.PlayerInfo.ServerIndex < 0)
                    {
                        player.PlayerInfo.ServerIndex = assignment.ServerPlayerIndex;
                        player.PlayerInfo.LocalIndex ??= player.LocalPlayerIndex;
                        _localServerIndices.Add(assignment.ServerPlayerIndex);
                        if (_remotePlayers.TryGetValue(assignment.ServerPlayerIndex, out var avatar))
                        {
                            avatar.Node.World?.RootNodes.Remove(avatar.Node);
                            _remotePlayers.Remove(assignment.ServerPlayerIndex);
                        }
                        return;
                    }
                }
            }

            private void HandleRemoteTransform(PlayerTransformUpdate update)
            {
                if (_localServerIndices.Contains(update.ServerPlayerIndex))
                    return;

                var avatar = GetOrCreateRemoteAvatar(update.ServerPlayerIndex);
                if (avatar?.Node.Transform is Transform transform)
                {
                    transform.TargetTranslation = update.Translation;
                    transform.TargetRotation = update.Rotation;
                }
            }

            private RemotePlayerAvatar? GetOrCreateRemoteAvatar(int serverPlayerIndex)
            {
                if (_remotePlayers.TryGetValue(serverPlayerIndex, out var avatar))
                    return avatar;

                XRWorldInstance? worldInstance = ResolvePrimaryWorldInstance();
                if (worldInstance is null)
                    return null;

                var node = new SceneNode(worldInstance, $"RemotePlayer_{serverPlayerIndex}");
                worldInstance.RootNodes.Add(node);
                avatar = new RemotePlayerAvatar(serverPlayerIndex, node);
                _remotePlayers[serverPlayerIndex] = avatar;
                return avatar;
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

            private sealed class RemotePlayerAvatar
            {
                public RemotePlayerAvatar(int serverPlayerIndex, SceneNode node)
                {
                    ServerPlayerIndex = serverPlayerIndex;
                    Node = node;
                }

                public int ServerPlayerIndex { get; }
                public SceneNode Node { get; }
            }

            ~ClientNetworkingManager()
            {
                if (_tickRegistered)
                    Time.Timer.UpdateFrame -= TickClientNetwork;
            }
        }
    }
}
