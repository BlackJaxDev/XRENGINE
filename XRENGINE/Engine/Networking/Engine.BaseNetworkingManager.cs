using MemoryPack;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Transforms.Rotations;
using XREngine.Scene.Transforms;
using XREngine.Networking;

namespace XREngine
{
    public static partial class Engine
    {
        public enum EStateChangeType : byte
        {
            /// <summary>
            /// Invalid state change type.
            /// </summary>
            Invalid = 0,
            /// <summary>
            /// Sent by the server to all clients when the world changes.
            /// Sent by a client to the server to request to change the world.
            /// </summary>
            WorldChange,
            /// <summary>
            /// Sent by the server to all clients when the game mode changes.
            /// Sent by a client to the server to request to change the game mode.
            /// </summary>
            GameModeChange,
            /// <summary>
            /// Sent by the server to all clients when a pawn changes possession.
            /// Sent by a client to the server to request to change possession of a pawn.
            /// </summary>
            PawnPossessionChange,
            /// <summary>
            /// Sent by the server to all clients when a world object is created.
            /// Sent by a client to the server to request to create a world object.
            /// </summary>
            WorldObjectCreated,
            /// <summary>
            /// Sent by the server to all clients when a world object is destroyed.
            /// Sent by a client to the server to request to destroy a world object.
            /// </summary>
            WorldObjectDestroyed,
            /// <summary>
            /// Sent by the server to all clients when a scene node is created.
            /// Sent by a client to the server to request to create a scene node.
            /// </summary>
            SceneNodeCreated,
            /// <summary>
            /// Sent by the server to all clients when a scene node is destroyed.
            /// Sent by a client to the server to request to destroy a scene node.
            /// </summary>
            SceneNodeDestroyed,
            /// <summary>
            /// Sent by the server to all clients when a component is created.
            /// Sent by a client to the server to request to create a component.
            /// </summary>
            ComponentCreated,
            /// <summary>
            /// Sent by the server to all clients when a component is destroyed.
            /// Sent by a client to the server to request to destroy a component.
            /// </summary>
            ComponentDestroyed,
            /// <summary>
            /// Sent by a client to the server to request to join the game.
            /// Sent by the server to all clients when a player joins.
            /// </summary>
            PlayerJoin,
            /// <summary>
            /// Sent by the server to confirm the authoritative player slot for a joining client.
            /// </summary>
            PlayerAssignment,
            /// <summary>
            /// Sent by a client to the server to request to join the game.
            /// Sent by the server to all clients when a player leaves.
            /// </summary>
            PlayerLeave,
            /// <summary>
            /// Heartbeat to keep a connection alive and update liveness state.
            /// </summary>
            Heartbeat,
            /// <summary>
            /// Sent by a client to the server with the latest local input values.
            /// </summary>
            PlayerInputSnapshot,
            /// <summary>
            /// Sent by the server to all clients to update the transform of a player.
            /// </summary>
            PlayerTransformUpdate,
            /// <summary>
            /// Sent by a client to the server to receive updates for player (usually if they're close enough to be relevant).
            /// </summary>
            RequestPlayerUpdates,
            /// <summary>
            /// Sent by a client to the server to stop receiving updates for a player.
            /// </summary>
            UnrequestPlayerUpdates,
            /// <summary>
            /// Sent by a client to request remote job execution (e.g., remote asset load).
            /// </summary>
            RemoteJobRequest,
            /// <summary>
            /// Sent by the remote host in response to a remote job request.
            /// </summary>
            RemoteJobResponse,
            /// <summary>
            /// Server-to-client error/status message (HTTP-like codes).
            /// </summary>
            ServerError,
            /// <summary>
            /// High-density VR humanoid pose packet (baseline or delta).
            /// </summary>
            HumanoidPoseFrame,
        }

        [MemoryPackable]
        public sealed partial class StateChangeInfo
        {
            public StateChangeInfo() { }
            [MemoryPackConstructor]
            public StateChangeInfo(EStateChangeType type, string data)
            {
                Type = type;
                Data = data;
            }

            public EStateChangeType Type { get; set; }
            public string Data { get; set; } = string.Empty;
        }

        public abstract class BaseNetworkingManager : XRBase, IDisposable
        {
            protected ConcurrentQueue<(ushort sequenceNum, byte[])> UdpSendQueue { get; } = new();

            public abstract bool IsServer { get; }
            public abstract bool IsClient { get; }
            public abstract bool IsP2P { get; }

            public bool UDPServerConnectionEstablished
                => UdpReceiver?.Client.Connected ?? false;
            public string LocalPeerId { get; }
            protected static string CurrentProtocolVersion { get; } = typeof(Engine).Assembly.GetName().Version?.ToString() ?? "dev";
            public event Func<RemoteJobRequest, Task<RemoteJobResponse?>>? RemoteJobRequestReceived;
            public event Action<RemoteJobResponse>? RemoteJobResponseReceived;
            public event Action<ServerErrorMessage>? ServerErrorReceived;
            public event Action<HumanoidPoseFrame>? HumanoidPoseFrameReceived;

            private readonly CancellationTokenSource _consumeCts = new();
            private Task _consumeTask = Task.CompletedTask;
            private bool _disposed;

            protected BaseNetworkingManager(string? peerId = null)
            {
                LocalPeerId = string.IsNullOrWhiteSpace(peerId) ? Guid.NewGuid().ToString("N") : peerId;
                Time.Timer.UpdateFrame += OnUpdateFrame;
            }
            ~BaseNetworkingManager()
                => Dispose(false);

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            protected virtual void Dispose(bool disposing)
            {
                if (_disposed)
                    return;

                _disposed = true;
                if (disposing)
                {
                    _consumeCts.Cancel();
                    Time.Timer.UpdateFrame -= OnUpdateFrame;

                    _consumeCts.Dispose();

                    try
                    {
                        _consumeTask.Wait(TimeSpan.FromMilliseconds(50));
                    }
                    catch
                    {
                        // ignore wait failures
                    }

                    DisposeSockets();
                }
                else
                {
                    Time.Timer.UpdateFrame -= OnUpdateFrame;
                    DisposeSockets();
                }
            }
            
            /// <summary>
            /// Sends from server to all connected clients, or from client to all other p2p clients.
            /// </summary>
            public UdpClient? UdpMulticastSender { get; set; }
            /// <summary>
            /// Receives from server or from other p2p clients.
            /// </summary>
            public UdpClient? UdpReceiver { get; set; }
            public IPEndPoint? MulticastEndPoint { get; set; }

            protected virtual void DisposeSockets()
            {
                try
                {
                    UdpReceiver?.Close();
                    UdpReceiver?.Dispose();
                }
                catch { }

                try
                {
                    UdpMulticastSender?.Close();
                    UdpMulticastSender?.Dispose();
                }
                catch { }

                UdpReceiver = null;
                UdpMulticastSender = null;
            }

            public static bool IsConnected()
                => NetworkInterface.GetIsNetworkAvailable();

            public static string[] GetAllLocalIPv4(NetworkInterfaceType type)
            {
                List<string> ipAddrList = [];
                foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (item.NetworkInterfaceType != type || item.OperationalStatus != OperationalStatus.Up)
                        continue;
                    
                    foreach (UnicastIPAddressInformation ip in item.GetIPProperties().UnicastAddresses)
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                            ipAddrList.Add(ip.Address.ToString());
                }
                return [.. ipAddrList];
            }

            protected abstract Task SendUDP();
            protected virtual async Task ReadUDP()
            {
                var receiver = UdpReceiver;
                bool anyAcked = false;
                while ((receiver?.Available ?? 0) > 0)
                {
                    UdpReceiveResult result = await receiver!.ReceiveAsync();
                    ReadReceivedData(result.Buffer, result.Buffer.Length, _decompBuffer, ref anyAcked, result.RemoteEndPoint);
                }
                //TODO: verify this is correct and not ruining the average
                if (!anyAcked)
                    UpdateRTT(0.0f);
            }
            private void OnUpdateFrame()
            {
                if (_disposed || _consumeCts.IsCancellationRequested)
                    return;

                if (!_consumeTask.IsCompleted)
                    return;

                _consumeTask = ConsumeQueuesAsync();
            }

            public virtual void ConsumeQueues() => OnUpdateFrame();

            private async Task ConsumeQueuesAsync()
            {
                try
                {
                    await ReadUDP().ConfigureAwait(false);
                    await SendUDP().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    //ignore cancellation
                }
                catch (Exception ex)
                {
                    Debug.Out($"[Net] ConsumeQueues exception: {ex}");
                }
            }

            /// <summary>
            /// Run on server or p2p client - sends to all clients
            /// </summary>
            /// <param name="udpMulticastIP"></param>
            /// <param name="udpMulticastPort"></param>
            protected void StartUdpMulticastSender(IPAddress udpMulticastIP, int udpMulticastPort)
            {
                UdpClient udpClient = new() { /*ExclusiveAddressUse = false*/ };
                UdpMulticastSender = udpClient;
                MulticastEndPoint = new IPEndPoint(udpMulticastIP, udpMulticastPort);
                //UdpMulticastSender.Connect(MulticastEndPoint);
            }

            /// <summary>
            /// Run on client or p2p client - receives from server
            /// </summary>
            /// <param name="udpMulticastServerIP"></param>
            /// <param name="upMulticastServerPort"></param>
            protected void StartUdpMulticastReceiver(IPAddress serverIP, IPAddress udpMulticastServerIP, int upMulticastServerPort)
            {
                UdpClient udpClient = new(upMulticastServerPort) { /*ExclusiveAddressUse = false,*/ MulticastLoopback = false };
                //udpClient.Connect(serverIP, upMulticastServerPort);
                //udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udpClient.JoinMulticastGroup(udpMulticastServerIP);
                UdpReceiver = udpClient;
            }

            private readonly ConcurrentDictionary<ushort, byte[]> _mustAck = new();

            private void EnqueueBroadcast(ushort sequenceNum, byte[] bytes, bool resendOnFailedAck)
            {
                UdpSendQueue.Enqueue((sequenceNum, bytes));
                if (resendOnFailedAck)
                    _mustAck[sequenceNum] = bytes;
            }
            
            private float _maxRoundTripSec = 1.0f;
            public float MaxRoundTripSec
            {
                get => _maxRoundTripSec;
                set => SetField(ref _maxRoundTripSec, value);
            }

            private readonly ConcurrentDictionary<ushort, float> _rttBuffer = [];

            private int? _maxSendablePacketsPerSecond = null;
            public int? MaxSendablePacketsPerSecond
            {
                get => _maxSendablePacketsPerSecond;
                set => SetField(ref _maxSendablePacketsPerSecond, value);
            }

            // Token bucket fields for rate limiting
            private float _packetTokens = 0;
            private float _lastTokenUpdate = Engine.ElapsedTime;

            private void UpdatePacketTokens()
            {
                var perSec = MaxSendablePacketsPerSecond;
                if (perSec is null)
                    return;

                var now = Engine.ElapsedTime;
                float elapsedSeconds = now - _lastTokenUpdate;
                _packetTokens += elapsedSeconds * perSec.Value;
                if (_packetTokens > perSec.Value)
                    _packetTokens = perSec.Value;
                _lastTokenUpdate = now;
            }

            // Add the following field and property near _totalBytesSent:
            private readonly ConcurrentQueue<(float timestamp, int bytes)> _bytesSentLog = new();

            public bool HasSentBytesInTheLastSecond => !_bytesSentLog.IsEmpty;

            public float PacketsPerSecond => _bytesSentLog.Count;

            /// <summary>
            /// Gets the total number of bytes sent during the last 1 second.
            /// </summary>
            public int BytesSentLastSecond
            {
                get
                {
                    float now = Engine.ElapsedTime;
                    TrimBytesSentLog(now);
                    int sum = 0;
                    foreach (var (timestamp, bytes) in _bytesSentLog)
                        sum += bytes;
                    return sum;
                    
                }
            }
            public float KBytesSentLastSecond => BytesSentLastSecond / 1024.0f;
            public float MBytesSentLastSecond => KBytesSentLastSecond / 1024.0f;

            private void TrimBytesSentLog(float now)
            {
                while (_bytesSentLog.TryPeek(out (float timestamp, int bytes) entry) && entry.timestamp < now - 1.0f)
                    _bytesSentLog.TryDequeue(out _);
            }

            public string DataPerSecondString
            {
                get
                {
                    float bytes = BytesSentLastSecond;
                    if (bytes < 1024)
                        return $"{bytes}b/s";
                    float kbytes = bytes / 1024.0f;
                    if (kbytes < 1024)
                        return $"{MathF.Round(kbytes)}Kb/s";
                    float mbytes = kbytes / 1024.0f;
                    return $"{MathF.Round(mbytes)}Mb/s";
                }
            }

            protected async Task ConsumeAndSendUDPQueue(UdpClient? client, IPEndPoint? endPoint)
            {
                ClearOldRTTs();

                //Send queue MUST be consumed so it doesn't grow infinitely
                if (UdpSendQueue.IsEmpty)
                    return;

                float now = Engine.ElapsedTime;
                TrimBytesSentLog(now);

                int packetsAllowed = int.MaxValue;
                if (MaxSendablePacketsPerSecond is not null)
                {
                    UpdatePacketTokens();
                    packetsAllowed = (int)Math.Floor(_packetTokens);
                }

                int packetsSent = 0;
                while (packetsSent < packetsAllowed && UdpSendQueue.TryDequeue(out (ushort sequenceNum, byte[] bytes) data))
                {
                    if (!_rttBuffer.ContainsKey(data.sequenceNum))
                        _rttBuffer[data.sequenceNum] = Engine.ElapsedTime;

                    if (client is null)
                        continue;

                    await client.SendAsync(data.bytes, data.bytes.Length, endPoint);
                    float timestamp = Engine.ElapsedTime;
                    _bytesSentLog.Enqueue((timestamp, data.bytes.Length));
                    TrimBytesSentLog(timestamp);
                    packetsSent++;
                }
                _packetTokens -= packetsSent;
                if (_packetTokens < 0)
                    _packetTokens = 0;
            }

            private void ClearOldRTTs()
            {
                if (_rttBuffer.IsEmpty)
                    return;
                                
                float oldest = Engine.ElapsedTime - MaxRoundTripSec;
                foreach (ushort key in _rttBuffer.Keys)
                {
                    if (!_rttBuffer.TryGetValue(key, out float time) || time >= oldest)
                        continue;
                    
                    _rttBuffer.TryRemove(key, out _);
                    if (_mustAck.TryRemove(key, out byte[]? bytes))
                    {
                        Debug.Out($"Required packet sequence {key} failed to return, resending...");
                        EnqueueBroadcast(key, bytes!, true);
                    }
                    //else
                    //    Debug.Out($"Packet sequence {key} failed to return, but was not required.");
                }
            }

            //3 bits
            public enum EBroadcastType : byte
            {
                StateChange,
                Object,
                Property,
                Data,
                Transform,
                Unused5,
                Unused6,
                Unused7,
            }

            //protocol header is only 3 bytes so the flag can come right after to align back to 4 bytes
            private static readonly byte[] Protocol = [0x46, 0x52, 0x4B]; // "FRK"
            private const ushort _halfMaxSeq = 32768;
            /// <summary>
            /// Compares two sequence numbers, accounting for the wrap-around point at half the maximum value.
            /// Returns true if left is greater than right, false otherwise.
            /// </summary>
            /// <param name="left"></param>
            /// <param name="right"></param>
            /// <returns></returns>
            private static bool SeqGreater(ushort left, ushort right) =>
                ((left > right) && (left - right <= _halfMaxSeq)) ||
                ((left < right) && (right - left > _halfMaxSeq));
            /// <summary>
            /// Returns the difference between two sequence numbers, accounting for the wrap-around point at half the maximum value.
            /// If left is greater than right, returns left - right.
            /// Else, returns the wrapped-around difference.
            /// </summary>
            /// <param name="left"></param>
            /// <param name="right"></param>
            /// <returns></returns>
            private static int DiffSeq(ushort left, ushort right)
                => left > right 
                ? left - right 
                : (left - 0) + (ushort.MaxValue - right) + 1; //+1, because if right is ushort.MaxValue and left is 0, the difference is 1

            private ushort _localSequence = 0;
            private readonly Deque<ushort> _receivedRemoteSequences = [];

            /// <summary>
            /// Broadcasts the entire object to all connected clients.
            /// </summary>
            /// <param name="obj"></param>
            /// <param name="compress"></param>
            public void ReplicateObject(XRWorldObjectBase obj, bool compress, bool resendOnFailedAck)
            {
                var bytes = MemoryPackSerializer.Serialize(obj);
                //var bytes = Encoding.UTF8.GetBytes(AssetManager.Serializer.Serialize(obj));
                Send(obj.ID, compress, bytes, EBroadcastType.Object, resendOnFailedAck);
            }

            /// <summary>
            /// Broadcasts arbitrary data to all connected clients.
            /// </summary>
            /// <param name="obj"></param>
            /// <param name="value"></param>
            /// <param name="idStr"></param>
            /// <param name="compress"></param>
            public void ReplicateData(XRWorldObjectBase obj, byte[] value, string idStr, bool compress, bool resendOnFailedAck)
            {
                IdValue data = new(idStr, value);
                var bytes = MemoryPackSerializer.Serialize(data);
                //var bytes = Encoding.UTF8.GetBytes(AssetManager.Serializer.Serialize(data));
                Send(obj.ID, compress, bytes, EBroadcastType.Data, resendOnFailedAck);
            }

            /// <summary>
            /// Broadcasts a property update to all connected clients.
            /// </summary>
            /// <typeparam name="T"></typeparam>
            /// <param name="obj"></param>
            /// <param name="propName"></param>
            /// <param name="value"></param>
            /// <param name="compress"></param>
            public void ReplicatePropertyUpdated<T>(XRWorldObjectBase obj, string? propName, T value, bool compress, bool resendOnFailedAck)
            {
                var bytes1 = MemoryPackSerializer.Serialize(value);
                IdValue data = new(propName ?? string.Empty, bytes1);
                var bytes = MemoryPackSerializer.Serialize(data);
                //var bytes = Encoding.UTF8.GetBytes(AssetManager.Serializer.Serialize(data));
                Send(obj.ID, compress, bytes, EBroadcastType.Property, resendOnFailedAck);
            }

            /// <summary>
            /// Broadcasts a transform update to all connected clients.
            /// The transform handles the encoding and decoding of its own data.
            /// </summary>
            /// <param name="transform"></param>
            public void ReplicateTransform(TransformBase transform, bool resendOnFailedAck)
            {
                Send(transform.ID, false, transform.EncodeToBytes(), EBroadcastType.Transform, resendOnFailedAck);
            }

            /// <summary>
            /// This is a special method that broadcasts engine state changes that aren't tied to GUIDs between clients and the server.
            /// </summary>
            /// <param name="data"></param>
            /// <param name="compress"></param>
            public void ReplicateStateChange(StateChangeInfo data, bool compress, bool resendOnFailedAck)
            {
                var bytes = MemoryPackSerializer.Serialize(data);
                //var bytes = Encoding.UTF8.GetBytes(AssetManager.Serializer.Serialize(data));
                Send(Guid.Empty, compress, bytes, EBroadcastType.StateChange, resendOnFailedAck);
            }

            public void BroadcastRemoteJobRequest(RemoteJobRequest request, bool compress = true, bool resendOnFailedAck = false)
            {
                ArgumentNullException.ThrowIfNull(request);
                string serialized = StateChangePayloadSerializer.Serialize(request);
                ReplicateStateChange(new StateChangeInfo(EStateChangeType.RemoteJobRequest, serialized), compress, resendOnFailedAck);
            }

            public void BroadcastRemoteJobResponse(RemoteJobResponse response, bool compress = true, bool resendOnFailedAck = false)
            {
                ArgumentNullException.ThrowIfNull(response);
                string serialized = StateChangePayloadSerializer.Serialize(response);
                ReplicateStateChange(new StateChangeInfo(EStateChangeType.RemoteJobResponse, serialized), compress, resendOnFailedAck);
            }

            public void BroadcastServerError(ServerErrorMessage error, bool compress = true, bool resendOnFailedAck = false)
            {
                ArgumentNullException.ThrowIfNull(error);
                string serialized = StateChangePayloadSerializer.Serialize(error);
                ReplicateStateChange(new StateChangeInfo(EStateChangeType.ServerError, serialized), compress, resendOnFailedAck);
            }

            public void BroadcastHumanoidPoseFrame(HumanoidPoseFrame frame, bool compress = false, bool resendOnFailedAck = false)
            {
                ArgumentNullException.ThrowIfNull(frame);
                string serialized = StateChangePayloadSerializer.Serialize(frame);
                ReplicateStateChange(new StateChangeInfo(EStateChangeType.HumanoidPoseFrame, serialized), compress, resendOnFailedAck);
            }

            protected void BroadcastStateChange<TPayload>(EStateChangeType type, TPayload payload, bool compress = true, bool resendOnFailedAck = false)
            {
                string serialized = StateChangePayloadSerializer.Serialize(payload);
                ReplicateStateChange(new StateChangeInfo(type, serialized), compress, resendOnFailedAck);
            }

            private const int HeaderLen = 16; //3 bytes for protocol, 1 byte for flags, 2 bytes for sequence, 2 bytes for ack, 4 bytes for ack bitfield, 4 bytes for data length (not including header or guid)
            private const int GuidLen = 16; //Guid is always 16 bytes
            private SevenZip.Compression.LZMA.Encoder _encoder = new();
            private SevenZip.Compression.LZMA.Decoder _decoder = new();
            private MemoryStream _compStreamIn = new();
            private MemoryStream _compStreamOut = new();
            private MemoryStream _decompStreamIn = new();
            private MemoryStream _decompStreamOut = new();

            /// <summary>
            /// Sends a broadcast packet to send over the established UDP connection.
            /// </summary>
            /// <param name="id">The id of the object to replicate information to.</param>
            /// <param name="compress">If the packet's data should be compressed - adds latency.</param>
            /// <param name="data">The data to send.</param>
            /// <param name="type">The type of replication this is - each type is optimized for its use case.</param>
            /// <param name="resendOnFailedAck">If the packet MUST be recieved - if it fails to be acknowledged by the receiver, it will be sent again until it is.</param>
            /// <returns></returns>
            protected void Send(Guid id, bool compress, byte[] data, EBroadcastType type, bool resendOnFailedAck)
            {
                ushort[] acks = ReadRemoteSeqs();
                byte flags = EncodeFlags(compress, type);
                _localSequence = _localSequence == ushort.MaxValue ? (ushort)0 : (ushort)(_localSequence + 1);
                ushort seq = _localSequence;
                bool noSeq = acks.Length == 0;
                ushort maxAck = noSeq ? (ushort)0 : acks[^1];
                uint prevAckBitfield = 0;
                if (!noSeq)
                    foreach (ushort ack in acks)
                    {
                        if (ack == maxAck)
                            continue;
                        int diff = DiffSeq(maxAck, ack);
                        if (diff > 32)
                            break;
                        prevAckBitfield |= (uint)(1 << (diff - 1));
                    }

                byte[] allData;
                int uncompDataLen = data.Length;

                if (compress)
                {
                    //First, compress guid and data together
                    byte[] uncompData = new byte[GuidLen + uncompDataLen];
                    int offset = 0;
                    SetGuidAndData(id, data, uncompData, ref offset);
                    byte[] compData = Compression.Compress(uncompData, ref _encoder, ref _compStreamIn, ref _compStreamOut);

                    //Then, create the full packet with header
                    int compDataLen = compData.Length;
                    allData = new byte[HeaderLen + compDataLen];
                    SetHeader(flags, allData, compDataLen, seq, maxAck, prevAckBitfield);

                    Buffer.BlockCopy(compData, 0, allData, HeaderLen, compDataLen);
                }
                else
                {
                    allData = new byte[HeaderLen + GuidLen + uncompDataLen];
                    SetHeader(flags, allData, uncompDataLen, seq, maxAck, prevAckBitfield);

                    int offset = HeaderLen;
                    SetGuidAndData(id, data, allData, ref offset);
                }
                EnqueueBroadcast(seq, allData, resendOnFailedAck);
            }

            private static byte EncodeFlags(bool compress, EBroadcastType type)
            {
                byte flags = 0;
                if (compress)
                    flags |= 1;
                flags |= (byte)((byte)type << 1);
                return flags;
            }
            private static void DecodeFlags(out bool compressed, out EBroadcastType type, byte flags)
            {
                compressed = (flags & 1) == 1;
                type = (EBroadcastType)((flags >> 1) & 0b111);
            }

            private ushort[] ReadRemoteSeqs()
            {
                ushort[] acks;
                lock (_receivedRemoteSequences)
                    acks = [.. _receivedRemoteSequences];
                return acks;
            }
            /// <summary>
            /// Writes the sequence number to the received remote sequence queue.
            /// If the sequence is more recent than the last one we received, update the last received sequence and return true.
            /// Else, ignore the sequence and return false.
            /// </summary>
            /// <param name="seq"></param>
            /// <returns></returns>
            private bool WriteToRemoteSeqs(ushort seq)
            {
                //If the sequence is more recent than the last one we received, update the last received sequence
                lock (_receivedRemoteSequences)
                {
                    // If we have no sequences yet, or this sequence is greater than our max sequence
                    if (_receivedRemoteSequences.Count == 0 || SeqGreater(seq, _receivedRemoteSequences.PeekBack()))
                    {
                        _receivedRemoteSequences.PushBack(seq);
                        if (_receivedRemoteSequences.Count > 33) //32 bits + 1 for max ack
                            _receivedRemoteSequences.PopFront();

                        return true; // This is a new sequence we haven't seen before
                    }

                    //Sequence as not greater than the last one we received, but it could still be a *new* sequence

                    // If we've already received this sequence before, don't process it again
                    if (_receivedRemoteSequences.Contains(seq))
                        return false;
                    
                    // Insert the sequence in the right position to maintain order
                    // This handles out-of-order packet reception
                    var tempList = new List<ushort>(_receivedRemoteSequences.Cast<ushort>());

                    // Find the insertion point to maintain ordered sequences
                    int insertIndex = 0;
                    for (; insertIndex < tempList.Count; insertIndex++)
                    {
                        if (SeqGreater(tempList[insertIndex], seq))
                            break;
                    }

                    tempList.Insert(insertIndex, seq);

                    // Clear and rebuild the queue with the new ordered sequences
                    _receivedRemoteSequences.Clear();
                    foreach (var s in tempList)
                        _receivedRemoteSequences.PushBack(s);

                    if (_receivedRemoteSequences.Count > 33)
                        _receivedRemoteSequences.PopFront();

                    // We want to store this sequence for acknowledgment purposes
                    // but since it's out of order, we don't want to process its data
                    return false;
                }
            }

            private static void SetHeader(byte flags, byte[] allData, int dataLen, ushort seq, ushort ack, uint ackBitfield)
            {
                //When we compose packet headers,
                //the local sequence becomes the sequence number of the packet,
                //and the remote sequence becomes the ack.
                //The ack bitfield is calculated by looking into a queue of up to 33 packets,
                //containing sequence numbers in the range [remote sequence - 32, remote sequence].
                //We set bit n (in [1,32]) in ack bits to 1 if the sequence number remote sequence - n is in the received queue.
                for (int i = 0; i < 3; i++)
                    Buffer.SetByte(allData, i, Protocol[i]);
                Buffer.SetByte(allData, 3, flags);
                //set sequence
                Buffer.BlockCopy(BitConverter.GetBytes(seq), 0, allData, 4, 2);
                //set ack
                Buffer.BlockCopy(BitConverter.GetBytes(ack), 0, allData, 6, 2);
                //set ack bitfield
                Buffer.BlockCopy(BitConverter.GetBytes(ackBitfield), 0, allData, 8, 4);
                //set data length
                Buffer.BlockCopy(BitConverter.GetBytes(dataLen), 0, allData, 12, 4);
            }

            protected static void SetGuidAndData(Guid id, byte[] data, byte[] allData, ref int offset)
            {
                Buffer.BlockCopy(id.ToByteArray(), 0, allData, offset, 16);
                offset += 16;
                Buffer.BlockCopy(data, 0, allData, offset, data.Length);
                offset += data.Length;
            }

            protected byte[] _decompBuffer = new byte[400000];
            //private (bool compress, EBroadcastType type, ushort seq, ushort ack, uint ackBitfield, int dataLength)? _lastConsumedHeader;
            
            protected int ReadReceivedData(byte[] inBuf, int availableDataLen, byte[] decompBuffer, ref bool anyAcked, IPEndPoint? sender)
            {
                int offset = 0;
                while (availableDataLen >= HeaderLen && offset < availableDataLen)
                {
                    //Search for protocol
                    byte[] protocol = new byte[3];
                    for (int i = 0; i < 3; i++)
                        protocol[i] = inBuf[offset + i];
                    if (!protocol.SequenceEqual(Protocol))
                    {
                        //Skip to next byte
                        offset++;
                        continue;
                    }

                    //We have a protocol match
                    offset += 3;

                    ReadHeader(
                        inBuf,
                        ref offset,
                        out bool compressed,
                        out EBroadcastType type,
                        out ushort seq,
                        out ushort ack,
                        out uint ackBitfield,
                        out int dataLength);

                    bool shouldRead = WriteToRemoteSeqs(seq);

                    //When a packet is received,
                    //ack bitfield is scanned and if bit n is set,
                    //then we acknowledge sequence number packet sequence - n,
                    //if it has not been acked already.
                    for (int i = 0; i < 32; i++)
                        if ((ackBitfield & (1 << i)) != 0)
                            anyAcked |= AcknowledgeSeq((ushort)(ack - i - 1));

                    if (availableDataLen >= offset + dataLength)
                    {
                        //if (shouldRead)
                        //    Debug.Out($"Received packet with sequence number: {seq}");
                        offset += ReadPacketData(compressed, type, inBuf, decompBuffer, offset, dataLength, shouldRead, sender);
                    }
                    else
                        return 0; //Not enough data to read packet, don't progress offset
                }
                return offset;
            }

            private bool AcknowledgeSeq(ushort ackedSeq)
            {
                if (!_rttBuffer.TryRemove(ackedSeq, out float time))
                    return false;
                
                UpdateRTT(Engine.ElapsedTime - time);

                if (_mustAck.TryRemove(ackedSeq, out _))
                    Debug.Out($"Acknowledged required sequence number: {ackedSeq}");

                //Can't print here otherwise because it will be repeated up to 32 extra times according to the ack bitfield
                return true; //We acknowledged the sequence number
            }

            private float _averageRTT = 0.0f;
            public float AverageRoundTripTimeSec
            {
                get => _averageRTT;
                private set => SetField(ref _averageRTT, value);
            }

            public float AverageRoundTripTimeMs => MathF.Round(AverageRoundTripTimeSec * 1000.0f);

            private float _rttSmoothingPercent = 0.1f;
            public float RTTSmoothingPercent
            {
                get => _rttSmoothingPercent;
                set => SetField(ref _rttSmoothingPercent, value);
            }

            private void UpdateRTT(float rttSec)
            {
                AverageRoundTripTimeSec = Interp.Lerp(AverageRoundTripTimeSec, rttSec, RTTSmoothingPercent);
                //Debug.Out($"RTT: {MathF.Round(AverageRoundTripTimeSec * 1000.0f)}ms");
            }

            private static void ReadHeader(
                byte[] inBuf,
                ref int offset,
                out bool compressed,
                out EBroadcastType type,
                out ushort seq,
                out ushort ack,
                out uint ackBitfield,
                out int dataLength)
            {
                DecodeFlags(out compressed, out type, inBuf[offset++]);

                seq = BitConverter.ToUInt16(inBuf, offset);
                offset += 2;

                ack = BitConverter.ToUInt16(inBuf, offset);
                offset += 2;

                ackBitfield = BitConverter.ToUInt32(inBuf, offset);
                offset += 4;

                dataLength = BitConverter.ToInt32(inBuf, offset);
                offset += 4;
            }

            private int ReadPacketData(
                bool compressed,
                EBroadcastType type,
                byte[] inBuf,
                byte[] decompBuffer,
                int dataOffset, //Already offset by header length
                int dataLength,
                bool propogateData,
                IPEndPoint? sender)
            {
                int readLen = dataLength;
                if (!compressed)
                    readLen += GuidLen;

                // Only propagate data if this sequence should be processed (in order)
                if (propogateData)
                {
                    if (compressed)
                        ReadCompressed(type, inBuf, decompBuffer, dataOffset, dataLength, sender);
                    else
                        ReadUncompressed(type, inBuf, dataOffset, dataLength, sender);
                }

                return readLen;
            }

            private void ReadUncompressed(EBroadcastType type, byte[] inBuf, int dataOffset, int dataLength, IPEndPoint? sender)
            {
                Propogate(
                    new Guid([.. inBuf.Skip(dataOffset).Take(GuidLen)]),
                    type,
                    inBuf,
                    dataOffset + GuidLen,
                    dataLength,
                    sender);
            }

            private void ReadCompressed(EBroadcastType type, byte[] inBuf, byte[] decompBuffer, int dataOffset, int dataLength, IPEndPoint? sender)
            {
                int decompLen = Compression.Decompress(inBuf, dataOffset, dataLength, decompBuffer, 0, ref _decoder, ref _decompStreamIn, ref _decompStreamOut);
                Propogate(
                    new Guid([.. decompBuffer.Take(GuidLen)]),
                    type,
                    decompBuffer,
                    GuidLen,
                    decompLen - GuidLen,
                    sender);
            }

            //protected static void ReadHeader(
            //    byte[] header,
            //    out bool compress,
            //    out EBroadcastType type,
            //    out int dataLength,
            //    out float elapsed,
            //    int offset = 0)
            //{
            //    byte flag = header[offset];
            //    compress = (flag & 1) == 1;
            //    type = (EBroadcastType)((flag >> 1) & 3);
            //    dataLength = BitConverter.ToInt32(header, offset) & 0x00FFFFFF;
            //    elapsed = BitConverter.ToSingle(header, offset + 4);
            //}

            /// <summary>
            /// Finds the target object in the cache and applies the data to it.
            /// </summary>
            /// <param name="id">The GUID of the object to update.</param>
            /// <param name="type">The type of update to propogate.</param>
            /// <param name="data">The full allocated data buffer. Use data offset to skip to data.</param>
            /// <param name="dataOffset">The offset to the data for this object.</param>
            /// <param name="dataLen">The length of data for this object.</param>
            protected void Propogate(Guid id, EBroadcastType type, byte[] data, int dataOffset, int dataLen, IPEndPoint? sender)
            {
                if (type == EBroadcastType.StateChange)
                {
                    StateChangeInfo? change = MemoryPackSerializer.Deserialize<StateChangeInfo>(data.AsSpan(dataOffset, dataLen));
                    if (change is not null)
                        HandleStateChange(change, sender);
                    return;
                }

                if (!XRObjectBase.ObjectsCache.TryGetValue(id, out var obj) || obj is not XRWorldObjectBase worldObj)
                    return;

                switch (type)
                {
                    case EBroadcastType.Object:
                        {
                            //string dataStr = Encoding.UTF8.GetString(data, dataOffset, dataLen);
                            //var newObj = AssetManager.Deserializer.Deserialize(dataStr, worldObj.GetType()) as XRWorldObjectBase;
                            var newObj = MemoryPackSerializer.Deserialize<XRWorldObjectBase>(data.AsSpan(dataOffset, dataLen));

                            if (newObj is not null)
                                worldObj.CopyFrom(newObj);
                            break;
                        }
                    case EBroadcastType.Property:
                        {
                            //string dataStr = Encoding.UTF8.GetString(data, dataOffset, dataLen);
                            //var (propName, value) = AssetManager.Deserializer.Deserialize<(string propName, object value)>(dataStr);
                            IdValue d = MemoryPackSerializer.Deserialize<IdValue>(data.AsSpan(dataOffset, dataLen));

                            worldObj.SetReplicatedProperty(d.key, d.value);
                            break;
                        }
                    case EBroadcastType.Data:
                        {
                            //string dataStr = Encoding.UTF8.GetString(data, dataOffset, dataLen);
                            //var (id2, value2) = AssetManager.Deserializer.Deserialize<(string id, object data)>(dataStr);
                            IdValue d = MemoryPackSerializer.Deserialize<IdValue>(data.AsSpan(dataOffset, dataLen));

                            worldObj.ReceiveData(d.key, d.value);
                            break;
                        }
                    case EBroadcastType.Transform:
                        {
                            if (worldObj is TransformBase transform)
                            {
                                byte[] slice = new byte[dataLen];
                                Buffer.BlockCopy(data, dataOffset, slice, 0, dataLen);
                                transform.DecodeFromBytes(slice);
                            }
                            break;
                        }
                }
            }

            protected virtual void HandleStateChange(StateChangeInfo change, IPEndPoint? sender)
            {
                if (change.Type == EStateChangeType.HumanoidPoseFrame)
                {
                    if (StateChangePayloadSerializer.TryDeserialize<HumanoidPoseFrame>(change.Data, out var frame) && frame is not null)
                        HumanoidPoseFrameReceived?.Invoke(frame);
                    return;
                }

                if (change.Type == EStateChangeType.RemoteJobRequest)
                {
                    if (StateChangePayloadSerializer.TryDeserialize<RemoteJobRequest>(change.Data, out var request) && request is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(request.TargetId) && !string.Equals(request.TargetId, LocalPeerId, StringComparison.OrdinalIgnoreCase))
                            return;

                        _ = DispatchRemoteJobRequestAsync(request);
                    }
                    return;
                }

                if (change.Type == EStateChangeType.RemoteJobResponse)
                {
                    if (StateChangePayloadSerializer.TryDeserialize<RemoteJobResponse>(change.Data, out var response) && response is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(response.TargetId) && !string.Equals(response.TargetId, LocalPeerId, StringComparison.OrdinalIgnoreCase))
                            return;

                        RemoteJobResponseReceived?.Invoke(response);
                    }
                    return;
                }

                if (change.Type == EStateChangeType.ServerError)
                {
                    if (StateChangePayloadSerializer.TryDeserialize<ServerErrorMessage>(change.Data, out var error) && error is not null)
                    {
                        ServerErrorReceived?.Invoke(error);
                    }
                    return;
                }
            }

            private async Task DispatchRemoteJobRequestAsync(RemoteJobRequest request)
            {
                var handler = RemoteJobRequestReceived;
                if (handler is null)
                    return;

                try
                {
                    var response = await handler(request).ConfigureAwait(false);
                    if (response != null)
                    {
                        var enriched = new RemoteJobResponse
                        {
                            JobId = response.JobId,
                            Success = response.Success,
                            Payload = response.Payload,
                            Error = response.Error,
                            Metadata = response.Metadata,
                            SenderId = LocalPeerId,
                            TargetId = request.SenderId,
                        };
                        BroadcastRemoteJobResponse(enriched);
                    }
                }
                catch (Exception ex)
                {
                    BroadcastRemoteJobResponse(new RemoteJobResponse
                    {
                        JobId = request.JobId,
                        Success = false,
                        Error = ex.Message,
                        SenderId = LocalPeerId,
                        TargetId = request.SenderId,
                    });
                }
            }

            [Flags]
            protected enum ETransformValueFlags
            {
                Quats = 1,
                Vector3s = 2,
                Rotators = 4,
                Scalars = 8,
                Ints = 16
            }

            protected static ETransformValueFlags MakeFlags((object value, int bitsPerComponent)[] values)
            {
                ETransformValueFlags flags = 0;
                foreach (var value in values)
                {
                    if (value.value is Quaternion)
                        flags |= ETransformValueFlags.Quats;
                    else if (value.value is Vector3)
                        flags |= ETransformValueFlags.Vector3s;
                    else if (value.value is Rotator)
                        flags |= ETransformValueFlags.Rotators;
                    else if (value.value is float)
                        flags |= ETransformValueFlags.Scalars;
                    else if (value.value is int)
                        flags |= ETransformValueFlags.Ints;
                }
                return flags;
            }

            #region TCP
            public static async Task SendFileAsync(string filePath, string targetIP, int port, IProgress<double> progress)
            {
                var fileInfo = new FileInfo(filePath);
                long fileLength = fileInfo.Length;

                using TcpClient client = new();
                await client.ConnectAsync(targetIP, port);
                using NetworkStream ns = client.GetStream();

                byte[] lengthBytes = BitConverter.GetBytes(fileLength);
                await ns.WriteAsync(lengthBytes);

                byte[] buffer = new byte[8192];
                long totalSent = 0;
                using FileStream fs = File.OpenRead(filePath);
                int bytesRead;
                while ((bytesRead = await fs.ReadAsync(buffer)) > 0)
                {
                    await ns.WriteAsync(buffer.AsMemory(0, bytesRead));
                    totalSent += bytesRead;
                    progress.Report((double)totalSent / fileLength * 100);
                }
            }

            public static async Task SendStreamAsync(Stream stream, string targetIP, int port, IProgress<double> progress)
            {
                long fileLength = stream.Length;
                using TcpClient client = new();
                await client.ConnectAsync(targetIP, port);
                using NetworkStream ns = client.GetStream();
                byte[] lengthBytes = BitConverter.GetBytes(fileLength);
                await ns.WriteAsync(lengthBytes);
                byte[] buffer = new byte[8192];
                long totalSent = 0;
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
                {
                    await ns.WriteAsync(buffer.AsMemory(0, bytesRead));
                    totalSent += bytesRead;
                    progress.Report((double)totalSent / fileLength * 100);
                }
            }

            public static async Task ReceiveFileAsync(string filePath, int port, IProgress<double> progress)
            {
                using TcpListener listener = new(IPAddress.Any, port);
                listener.Start();
                using TcpClient client = await listener.AcceptTcpClientAsync();
                using NetworkStream ns = client.GetStream();
                byte[] lengthBytes = new byte[8];
                await ns.ReadExactlyAsync(lengthBytes);
                long fileLength = BitConverter.ToInt64(lengthBytes);
                byte[] buffer = new byte[8192];
                long totalReceived = 0;
                using FileStream fs = File.OpenWrite(filePath);
                int bytesRead;
                while (totalReceived < fileLength && (bytesRead = await ns.ReadAsync(buffer)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, bytesRead));
                    totalReceived += bytesRead;
                    progress.Report((double)totalReceived / fileLength * 100);
                }
            }

            public static async Task ReceiveStreamAsync(Stream stream, int port, IProgress<double> progress)
            {
                using TcpListener listener = new(IPAddress.Any, port);
                listener.Start();
                using TcpClient client = await listener.AcceptTcpClientAsync();
                using NetworkStream ns = client.GetStream();
                byte[] lengthBytes = new byte[8];
                await ns.ReadExactlyAsync(lengthBytes);
                long fileLength = BitConverter.ToInt64(lengthBytes);
                byte[] buffer = new byte[8192];
                long totalReceived = 0;
                int bytesRead;
                while (totalReceived < fileLength && (bytesRead = await ns.ReadAsync(buffer)) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    totalReceived += bytesRead;
                    progress.Report((double)totalReceived / fileLength * 100);
                }
            }

            ///// <summary>
            ///// Receives from server or from other clients in a p2p scenario.
            ///// </summary>
            //public TcpClient? TcpReceiver { get; set; }
            ///// <summary>
            ///// Sends from client to server.
            ///// </summary>
            //public TcpClient? TcpSender { get; set; }
            ///// <summary>
            ///// Listener for incoming TCP connections.
            ///// </summary>
            //public TcpListener? TcpListener { get; set; }
            ///// <summary>
            ///// List of TCP clients connected to this server, or in a p2p scenario, connected to this client.
            ///// </summary>
            //public List<TcpClient> TcpClients { get; } = [];

            //public bool TCPConnectionEstablished => TcpReceiver?.Connected ?? false;

            //private void StartTcpSender(IPAddress serverIP, int tcpPort)
            //{
            //    TcpSender = new TcpClient();
            //    if (ServerIP is not null)
            //        TcpSender.Connect(serverIP, tcpPort);
            //}

            //private void StartTcpListener(IPAddress tcpListenerIP, int tcpListenerPort)
            //{
            //    TcpListener = new TcpListener(tcpListenerIP, tcpListenerPort);
            //    TcpListener.Start();
            //}

            //private void ClientSendTcp()
            //{
            //    //Send outgoing data
            //    if (PeerToPeer)
            //    {
            //        //Multicast to other clients like a server would
            //        BroadcastToTcpClients();
            //    }
            //    else
            //    {
            //        //Send directly to server
            //        SendDirectTcp();
            //    }
            //}

            //private void SendDirectTcp()
            //{
            //    if (TcpReceiver is null)
            //        return;

            //    NetworkStream stream = TcpReceiver.GetStream();
            //    while (TcpSendQueue.TryDequeue(out byte[]? bytes))
            //    {
            //        stream.Write(bytes, 0, bytes.Length);
            //        stream.Flush();
            //    }
            //}

            //private void BroadcastToTcpClients()
            //{
            //    AcceptClientConnections();
            //    while (TcpSendQueue.TryDequeue(out byte[]? bytes))
            //    {
            //        lock (TcpClients)
            //        {
            //            List<TcpClient> disconnectedClients = [];
            //            foreach (var client in TcpClients)
            //            {
            //                try
            //                {
            //                    if (client.Connected)
            //                    {
            //                        NetworkStream stream = client.GetStream();
            //                        stream.Write(bytes, 0, bytes.Length);
            //                        stream.Flush();
            //                    }
            //                    else
            //                    {
            //                        Debug.Out("Client disconnected");
            //                        disconnectedClients.Add(client);
            //                    }
            //                }
            //                catch
            //                {
            //                    Debug.Out("Client disconnected");
            //                    disconnectedClients.Add(client);
            //                }
            //            }
            //            foreach (var client in disconnectedClients)
            //            {
            //                TcpClients.Remove(client);
            //                client.Close();
            //            }
            //        }
            //    }
            //}

            //private void AcceptClientConnections()
            //{
            //    while (TcpListener?.Pending() ?? false)
            //    {
            //        var client = TcpListener.AcceptTcpClient();
            //        lock (TcpClients)
            //        {
            //            TcpClients.Add(client);
            //        }
            //    }
            //}

            //private async Task ReadTCP()
            //{
            //    if (!(TcpReceiver?.Connected ?? false))
            //        return;

            //    NetworkStream stream = TcpReceiver.GetStream();
            //    while (stream.DataAvailable)
            //    {
            //        int bytesRead = await stream.ReadAsync(_tcpInBuffer.AsMemory(_tcpBufferOffset, _tcpInBuffer.Length - _tcpBufferOffset));
            //        _tcpBufferOffset += bytesRead;
            //    }

            //    ReadReceivedData(_tcpInBuffer, ref _tcpBufferOffset, _decompBuffer);
            //}
            #endregion
        }
    }
    [MemoryPackable]
    internal partial record struct IdValue(string key, byte[] value)
    {
        public static implicit operator (string idStr, byte[] value)(IdValue value)
        {
            return (value.key, value.value);
        }

        public static implicit operator IdValue((string idStr, byte[] value) value)
        {
            return new IdValue(value.idStr, value.value);
        }
    }
}
