using MemoryPack;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Transforms.Rotations;
using XREngine.Execution;
using XREngine.Networking;
using XREngine.Scene.Transforms;
using XREngine.Timers;

namespace XREngine;

/// <summary>
/// Shared realtime transport: datagram framing, per-peer sequencing and acknowledgement, the
/// reliable control lane, and inbound dispatch. State-change payload handling lives in the
/// state-change partial and the allocation-free unreliable lane in the high-rate partial.
/// </summary>
public abstract partial class BaseNetworkingManager : XRBase, IDisposable
{
    internal static long CurrentEngineTicks()
        => RuntimeTimingServices.Current.ElapsedTicks;

    internal static long SecondsToStopwatchTicks(double seconds)
        => (long)Math.Round(seconds * Stopwatch.Frequency, MidpointRounding.AwayFromZero);

    internal static double TickDeltaToSeconds(long currentTicks, long previousTicks)
        => Math.Max(0L, currentTicks - previousTicks) / (double)Stopwatch.Frequency;

    internal static bool HasElapsed(long currentTicks, long previousTicks, double intervalSeconds)
        => Math.Max(0L, currentTicks - previousTicks) >= SecondsToStopwatchTicks(intervalSeconds);

    internal static bool HasElapsed(long currentTicks, long previousTicks, long intervalTicks)
        => Math.Max(0L, currentTicks - previousTicks) >= Math.Max(0L, intervalTicks);

    internal static long GetWindowStartTicks(long currentTicks, double windowSeconds)
        => Math.Max(0L, currentTicks - SecondsToStopwatchTicks(windowSeconds));

    private readonly ConcurrentDictionary<IPEndPoint, UdpPeerState> _udpPeers = new();
    private readonly object _peerListSync = new();
    /// <summary>Copy-on-write view of <see cref="_udpPeers"/> so the per-frame send loop iterates without allocating.</summary>
    private volatile UdpPeerState[] _peerSnapshot = [];
    private readonly object _sendTargetSync = new();
    private readonly List<IPEndPoint> _sendTargetsScratch = new(8);
    private long _badMacRejects;
    private long _badSourceRejects;
    private long _replayRejects;
    private long _unauthorizedRejects;
    private long _rateLimitedRejects;

    public RealtimeTransportRejectionSnapshot RealtimeTransportRejections
        => new(Volatile.Read(ref _badMacRejects), Volatile.Read(ref _badSourceRejects), Volatile.Read(ref _replayRejects), Volatile.Read(ref _unauthorizedRejects), Volatile.Read(ref _rateLimitedRejects), _udpPeers.Count);

    protected void RecordBadMacRejection() => Interlocked.Increment(ref _badMacRejects);
    protected void RecordBadSourceRejection() => Interlocked.Increment(ref _badSourceRejects);
    protected void RecordReplayRejection() => Interlocked.Increment(ref _replayRejects);
    protected void RecordUnauthorizedRejection() => Interlocked.Increment(ref _unauthorizedRejects);

    /// <summary>Upper bound for one UDP datagram accepted by the realtime framing layer.</summary>
    public const int MaxInboundDatagramBytes = 65_507;
    /// <summary>Upper bound for one encoded frame, excluding its fixed header.</summary>
    public const int MaxInboundFrameBytes = 65_491;
    /// <summary>Upper bound for a decompressed frame, including its object identifier.</summary>
    public const int MaxInboundDecompressedBytes = 400_000;
    public const int MaxTrackedInboundUdpPeers = 256;
    public const int DefaultMaxInboundPacketsPerSecond = 120;
    public const int DefaultMaxInboundBytesPerSecond = 1_048_576;

    /// <summary>Bounds one receive pump so a flood cannot starve the frame that drives it.</summary>
    private const int MaxDatagramsPerPump = 4096;

    public abstract bool IsServer { get; }
    public abstract bool IsClient { get; }

    /// <summary>
    /// Gets whether this manager currently has at least one confirmed remote peer that
    /// can exchange application data. A bound or configured socket alone is not a
    /// connection.
    /// </summary>
    public abstract bool HasConnectedRemotePeer { get; }

    public bool UDPServerConnectionEstablished
        => UdpReceiver is { } transport && (transport.Connected || transport.IsBound);
    /// <summary>Bounds browser queues and coalesces only complete transform snapshots.</summary>
    protected virtual bool UseBoundedRealtimeQueues => false;
    private string? _wireProtocolFailure;
    /// <summary>Reports an incompatible packet rejected before it can affect peer state.</summary>
    public string? WireProtocolFailure => _wireProtocolFailure;
    private void RecordWireProtocolMismatch()
    {
        if (!SetField(ref _wireProtocolFailure, RealtimeWireProtocol.UpdateRequiredMessage))
            return;
        Debug.NetworkingWarning("[Net] {0}", RealtimeWireProtocol.UpdateRequiredMessage);
    }
    protected virtual void OnRealtimeQueueOverflow()
        => throw new NetworkTransportException("Realtime send backlog exceeded its bounded capacity.", 0);
    public string LocalPeerId { get; }
    protected static string CurrentProtocolVersion => RuntimeNetworkingHostServices.Current.ProtocolVersion;

    /// <summary>Disabled by default because remote jobs execute application-defined work.</summary>
    protected virtual bool AllowsRemoteJobTraffic => false;

    private readonly CancellationTokenSource _consumeCts = new();
    private Task _consumeTask = Task.CompletedTask;
    private bool _disposed;

    /// <summary>Reused for every inbound datagram. Only the receive pump touches it.</summary>
    private readonly byte[] _receiveBuffer = new byte[MaxInboundDatagramBytes];
    /// <summary>Reused for the protected form of every outbound datagram. Only the send pump touches it.</summary>
    private readonly byte[] _sendScratch = new byte[MaxInboundDatagramBytes];

    protected BaseNetworkingManager(string? peerId = null)
    {
        LocalPeerId = string.IsNullOrWhiteSpace(peerId) ? Guid.NewGuid().ToString("N") : peerId;
        RuntimeTimingServices.Current.Update += OnUpdateFrame;
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
            RuntimeTimingServices.Current.Update -= OnUpdateFrame;

            _consumeCts.Dispose();

            try
            {
                if (!OperatingSystem.IsBrowser() && !RuntimeWorkScheduler.IsCallerThread)
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
            RuntimeTimingServices.Current.Update -= OnUpdateFrame;
            DisposeSockets();
        }
    }

    /// <summary>
    /// Sends from server to all connected clients, or from client to all other p2p clients.
    /// </summary>
    public IDatagramTransport? UdpMulticastSender { get; set; }
    /// <summary>
    /// Receives from server or from other p2p clients.
    /// </summary>
    public IDatagramTransport? UdpReceiver { get; set; }
    public IPEndPoint? MulticastEndPoint { get; set; }

    protected virtual void DisposeSockets()
    {
        IDatagramTransport? receiver = UdpReceiver;
        IDatagramTransport? sender = UdpMulticastSender;

        try
        {
            receiver?.Close();
            receiver?.Dispose();
        }
        catch { }

        if (!ReferenceEquals(sender, receiver))
        {
            try
            {
                sender?.Close();
                sender?.Dispose();
            }
            catch { }
        }

        UdpReceiver = null;
        UdpMulticastSender = null;
        lock (_peerListSync)
        {
            _udpPeers.Clear();
            _peerSnapshot = [];
        }
    }

    public static bool IsConnected()
        => NetworkTransportServices.Required.IsNetworkAvailable();

    public static string[] GetAllLocalIPv4(NetworkInterfaceType type)
        => NetworkTransportServices.Required.GetLocalIPv4((int)type);

    protected abstract Task SendUDP();

    /// <summary>
    /// Drains pending datagrams. Transports with span support are read synchronously into one
    /// reusable buffer, so the steady-state receive path performs no heap allocation.
    /// </summary>
    protected virtual Task ReadUDP()
    {
        IDatagramTransport? receiver = UdpReceiver;
        if (receiver is null)
            return Task.CompletedTask;

        if (!receiver.SupportsSpanDatagrams)
            return ReadUdpAllocatingAsync(receiver);

        bool anyAcked = false;
        byte[] buffer = _receiveBuffer;
        for (int received = 0; received < MaxDatagramsPerPump && !_disposed; received++)
        {
            if (!receiver.TryReceive(buffer, out int length, out IPEndPoint? remote))
                break;

            if (length < 0 || remote is null)
            {
                Debug.NetworkingWarning("[Net] Dropped oversized UDP datagram from {0}.", remote?.ToString() ?? "<unknown>");
                continue;
            }

            ProcessDatagram(buffer, length, remote, ref anyAcked);
        }

        //TODO: verify this is correct and not ruining the average
        if (!anyAcked)
            UpdateRTT(0.0f);
        return Task.CompletedTask;
    }

    /// <summary>Receive path for transports that only expose the allocating asynchronous API.</summary>
    private async Task ReadUdpAllocatingAsync(IDatagramTransport receiver)
    {
        bool anyAcked = false;
        for (int received = 0; received < MaxDatagramsPerPump && !_disposed && receiver.Available > 0; received++)
        {
            DatagramReceiveResult result = await receiver.ReceiveAsync(_consumeCts.Token).ConfigureAwait(false);
            if (result.Buffer.Length > MaxInboundDatagramBytes)
            {
                Debug.NetworkingWarning("[Net] Dropped oversized UDP datagram ({0} bytes) from {1}.", result.Buffer.Length, result.RemoteEndPoint);
                continue;
            }

            ProcessDatagram(result.Buffer, result.Buffer.Length, result.RemoteEndPoint, ref anyAcked);
        }

        if (!anyAcked)
            UpdateRTT(0.0f);
    }

    private void ProcessDatagram(byte[] buffer, int length, IPEndPoint remote, ref bool anyAcked)
    {
        ReadOnlyMemory<byte> datagram = buffer.AsMemory(0, length);
        if (RealtimeWireProtocol.IsIncompatible(datagram.Span))
        {
            RecordWireProtocolMismatch();
            return;
        }
        ReadOnlyMemory<byte> accepted = datagram;
        bool isManagedEnvelope = ManagedUdpEnvelope.TryRead(datagram.Span, out _, out _, out _);
        if ((RequiresManagedUdpTransport && !isManagedEnvelope)
            || (isManagedEnvelope && !TryUnwrapManagedDatagram(datagram, remote, out accepted)))
        {
            Debug.NetworkingWarning("[Net] Dropped unauthenticated managed UDP envelope from {0}.", remote);
            return;
        }

        // An unwrapped datagram is a slice of the receive buffer, never a copy.
        if (!MemoryMarshal.TryGetArray(accepted, out ArraySegment<byte> segment) || segment.Array is null)
            return;

        ReadReceivedData(segment.Array, segment.Offset, segment.Count, _decompBuffer, ref anyAcked, remote);
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
            Debug.Log(ELogCategory.Networking, $"[Net] ConsumeQueues exception: {ex}");
        }
    }

    /// <summary>
    /// Run on server or p2p client - sends to all clients
    /// </summary>
    protected void StartUdpMulticastSender(IPAddress udpMulticastIP, int udpMulticastPort)
    {
        IDatagramTransport udpClient = NetworkTransportServices.Required.CreateDatagram("multicast sender");
        UdpMulticastSender = udpClient;
        MulticastEndPoint = new IPEndPoint(udpMulticastIP, udpMulticastPort);
    }

    /// <summary>
    /// Run on client or p2p client - receives from server
    /// </summary>
    protected void StartUdpMulticastReceiver(IPAddress serverIP, IPAddress udpMulticastServerIP, int upMulticastServerPort)
    {
        // Multiple local clients need to share the same multicast port.
        // On Windows this requires ReuseAddress + ExclusiveAddressUse=false before binding.
        IDatagramTransport udpClient = NetworkTransportServices.Required.CreateDatagram("multicast receiver");
        udpClient.MulticastLoopback = false;
        udpClient.ExclusiveAddressUse = false;
        udpClient.ReuseAddress = true;
        udpClient.Bind(new IPEndPoint(IPAddress.Any, upMulticastServerPort));
        udpClient.JoinMulticastGroup(udpMulticastServerIP);
        UdpReceiver = udpClient;
    }

    private readonly record struct PendingAckPacket(Guid OwnerId, byte[] Bytes, long FirstSendTicks, long TimeoutTicks);

    /// <summary>
    /// A reliable-lane frame awaiting transmission. Its sequence and acknowledgement fields are
    /// stamped when it is sent, so sequence order always matches wire order across both lanes.
    /// </summary>
    private readonly record struct QueuedUdpPacket(byte[] Bytes, bool ResendOnFailedAck, Guid OwnerId, long FirstSendTicks, long TimeoutTicks);

    private sealed class UdpPeerState
    {
        public UdpPeerState(IPEndPoint endPoint)
        {
            EndPoint = endPoint;
            LastTokenUpdateTicks = CurrentEngineTicks();
        }

        public IPEndPoint EndPoint { get; }
        public ConcurrentQueue<QueuedUdpPacket> SendQueue { get; } = new();
        public Dictionary<Guid, QueuedUdpPacket> PendingTransforms { get; } = [];
        public int PendingBytes;
        public int ReliablePacketCount;
        public ConcurrentDictionary<ushort, PendingAckPacket> MustAck { get; } = new();
        public ConcurrentDictionary<ushort, long> RttBuffer { get; } = new();

        /// <summary>Guards sequencing, the receive window, and the high-rate ring.</summary>
        public readonly object Sync = new();

        /// <summary>Unreliable lane storage. Created on the first high-rate send to this peer.</summary>
        public RealtimePacketSendRing? HighRateRing;
        public long[]? HighRateSendTicks;
        public ushort[]? HighRateSendSequences;

        public ushort LocalSequence;
        public bool HasRemoteSequence;
        public ushort MaxRemoteSequence;
        /// <summary>Bit n-1 is set when sequence <c>MaxRemoteSequence - n</c> was received (n in 1..32).</summary>
        public uint RemoteSequenceBits;

        public double PacketTokens;
        public long LastTokenUpdateTicks;
        public double InboundPacketTokens;
        public double InboundByteTokens;
        public long LastInboundTokenUpdateTicks;
    }

    private UdpPeerState RegisterUdpPeer(IPEndPoint endPoint)
    {
        if (_udpPeers.TryGetValue(endPoint, out UdpPeerState? existing))
            return existing;

        lock (_peerListSync)
        {
            if (_udpPeers.TryGetValue(endPoint, out existing))
                return existing;

            // Key on a private copy so a caller mutating its endpoint cannot corrupt the table.
            IPEndPoint key = new(endPoint.Address, endPoint.Port);
            UdpPeerState peer = new(key);
            _udpPeers[key] = peer;
            _peerSnapshot = [.. _udpPeers.Values];
            return peer;
        }
    }

    private int _maxInboundPacketsPerSecond = DefaultMaxInboundPacketsPerSecond;
    /// <summary>Per-peer receive packet rate. Zero disables only this rate limit.</summary>
    public int MaxInboundPacketsPerSecond
    {
        get => _maxInboundPacketsPerSecond;
        set => SetField(ref _maxInboundPacketsPerSecond, Math.Max(0, value));
    }

    private int _maxInboundBytesPerSecond = DefaultMaxInboundBytesPerSecond;
    /// <summary>Per-peer receive byte rate. Zero disables only this rate limit.</summary>
    public int MaxInboundBytesPerSecond
    {
        get => _maxInboundBytesPerSecond;
        set => SetField(ref _maxInboundBytesPerSecond, Math.Max(0, value));
    }

    private bool TryRegisterInboundUdpPeer(IPEndPoint endPoint, out UdpPeerState peer)
    {
        if (_udpPeers.TryGetValue(endPoint, out peer!))
            return true;

        if (_udpPeers.Count >= MaxTrackedInboundUdpPeers)
        {
            peer = null!;
            Debug.NetworkingWarning("[Net] Dropped inbound peer {0}; the {1}-peer transport limit is reached.", endPoint, MaxTrackedInboundUdpPeers);
            return false;
        }

        peer = RegisterUdpPeer(endPoint);
        return true;
    }

    private bool TryConsumeInboundBudget(UdpPeerState peer, int byteCount)
    {
        int packetLimit = MaxInboundPacketsPerSecond;
        int byteLimit = MaxInboundBytesPerSecond;
        if (packetLimit == 0 && byteLimit == 0)
            return true;

        long nowTicks = CurrentEngineTicks();
        double elapsedSeconds = TickDeltaToSeconds(nowTicks, peer.LastInboundTokenUpdateTicks);
        if (peer.LastInboundTokenUpdateTicks == 0L)
        {
            peer.InboundPacketTokens = packetLimit;
            peer.InboundByteTokens = byteLimit;
        }
        else
        {
            peer.InboundPacketTokens = Math.Min(packetLimit, peer.InboundPacketTokens + elapsedSeconds * packetLimit);
            peer.InboundByteTokens = Math.Min(byteLimit, peer.InboundByteTokens + elapsedSeconds * byteLimit);
        }
        peer.LastInboundTokenUpdateTicks = nowTicks;

        if ((packetLimit > 0 && peer.InboundPacketTokens < 1.0d)
            || (byteLimit > 0 && peer.InboundByteTokens < byteCount))
        {
            return false;
        }

        if (packetLimit > 0)
            peer.InboundPacketTokens -= 1.0d;
        if (byteLimit > 0)
            peer.InboundByteTokens -= byteCount;
        return true;
    }

    protected void UnregisterUdpPeer(IPEndPoint? endPoint)
    {
        if (endPoint is null)
            return;

        lock (_peerListSync)
        {
            if (_udpPeers.TryRemove(endPoint, out _))
                _peerSnapshot = [.. _udpPeers.Values];
        }
    }

    private static bool ShouldResendRequiredPacket(Guid ownerId)
    {
        if (ownerId == Guid.Empty)
            return true;

        if (!XRObjectBase.ObjectsCache.TryGetValue(ownerId, out XRObjectBase? obj))
            return false;

        if (obj is XRComponent component)
            return component.IsActiveInHierarchy;

        return true;
    }

    private const float DefaultAckTimeoutSec = 5.0f;

    private float _maxRoundTripSec = 1.0f;
    public float MaxRoundTripSec
    {
        get => _maxRoundTripSec;
        set => SetField(ref _maxRoundTripSec, value);
    }

    private int? _maxSendablePacketsPerSecond = null;
    public int? MaxSendablePacketsPerSecond
    {
        get => _maxSendablePacketsPerSecond;
        set => SetField(ref _maxSendablePacketsPerSecond, value);
    }

    private void UpdatePacketTokens(UdpPeerState peer)
    {
        var perSec = MaxSendablePacketsPerSecond;
        if (perSec is null)
            return;

        long nowTicks = CurrentEngineTicks();
        double elapsedSeconds = TickDeltaToSeconds(nowTicks, peer.LastTokenUpdateTicks);
        peer.PacketTokens += elapsedSeconds * perSec.Value;
        if (peer.PacketTokens > perSec.Value)
            peer.PacketTokens = perSec.Value;
        peer.LastTokenUpdateTicks = nowTicks;
    }

    private const int SendTelemetryBucketCount = 101;
    private static readonly long SendTelemetryBucketTicks = Math.Max(1L, (Stopwatch.Frequency + 99L) / 100L);
    private readonly object _sendTelemetrySync = new();
    private readonly long[] _sendTelemetryEpochs = new long[SendTelemetryBucketCount];
    private readonly long[] _sendTelemetryBytes = new long[SendTelemetryBucketCount];
    private readonly long[] _sendTelemetryPackets = new long[SendTelemetryBucketCount];

    /// <summary>Whether the rolling send window contains a packet, at 10 ms bucket precision.</summary>
    public bool HasSentBytesInTheLastSecond => ReadSendTelemetry().Packets != 0;

    /// <summary>Packet count over the last second, including at most one extra 10 ms boundary bucket.</summary>
    public float PacketsPerSecond => ReadSendTelemetry().Packets;

    /// <summary>Byte count over the last second, including at most one extra 10 ms boundary bucket.</summary>
    public int BytesSentLastSecond => (int)Math.Min(int.MaxValue, ReadSendTelemetry().Bytes);
    public float KBytesSentLastSecond => BytesSentLastSecond / 1024.0f;
    public float MBytesSentLastSecond => KBytesSentLastSecond / 1024.0f;

    private (long Packets, long Bytes) ReadSendTelemetry()
    {
        lock (_sendTelemetrySync)
        {
            long epoch = Math.Max(0L, CurrentEngineTicks()) / SendTelemetryBucketTicks;
            long packets = 0;
            long bytes = 0;
            for (int i = 0; i < SendTelemetryBucketCount; i++)
            {
                long storedEpoch = _sendTelemetryEpochs[i] - 1L;
                if (storedEpoch < 0 || storedEpoch > epoch || epoch - storedEpoch >= SendTelemetryBucketCount)
                    continue;
                packets += _sendTelemetryPackets[i];
                bytes += _sendTelemetryBytes[i];
            }
            return (packets, bytes);
        }
    }

    private void RecordBytesSent(int byteCount)
    {
        lock (_sendTelemetrySync)
        {
            long epoch = Math.Max(0L, CurrentEngineTicks()) / SendTelemetryBucketTicks;
            int index = (int)(epoch % SendTelemetryBucketCount);
            if (_sendTelemetryEpochs[index] != epoch + 1L)
            {
                _sendTelemetryEpochs[index] = epoch + 1L;
                _sendTelemetryBytes[index] = 0;
                _sendTelemetryPackets[index] = 0;
            }
            _sendTelemetryBytes[index] += byteCount;
            _sendTelemetryPackets[index]++;
        }
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

    protected void ConsumeAndSendUDPQueue(IDatagramTransport? client, IPEndPoint? endPoint)
    {
        if (endPoint is null)
            return;

        ConsumeAndSendUDPQueue(client, RegisterUdpPeer(endPoint));
    }

    protected void ConsumeAndSendUDPQueues(IDatagramTransport? client)
    {
        UdpPeerState[] peers = _peerSnapshot;
        for (int i = 0; i < peers.Length; i++)
            ConsumeAndSendUDPQueue(client, peers[i]);
    }

    private void ConsumeAndSendUDPQueue(IDatagramTransport? client, UdpPeerState peer)
    {
        ClearOldRTTs(peer);

        bool hasHighRate = peer.HighRateRing is { Count: > 0 };
        if (!UseBoundedRealtimeQueues && peer.SendQueue.IsEmpty && !hasHighRate)
            return;

        int packetsAllowed = int.MaxValue;
        if (MaxSendablePacketsPerSecond is not null)
        {
            UpdatePacketTokens(peer);
            packetsAllowed = (int)Math.Floor(peer.PacketTokens);
        }

        // The reliable lane drains first so a control frame queued during admission is the first
        // datagram a newly routable peer receives.
        int packetsSent = 0;
        byte[] scratch = _sendScratch;
        while (packetsSent < packetsAllowed && TryTakeQueuedPacket(peer, out QueuedUdpPacket data))
        {
            if (client is null)
            {
                ReleaseReliablePacket(peer, data.ResendOnFailedAck);
                continue;
            }

            ushort sequence;
            lock (peer.Sync)
            {
                NextSequence_NoLock(peer, out sequence, out ushort ack, out uint ackBits);
                WriteSequenceFields(data.Bytes, sequence, ack, ackBits);
            }

            if (!TryProtectOutboundDatagram(data.Bytes, peer.EndPoint, scratch, out int written))
            {
                // A managed association can be provisional while its simulation-thread
                // admission publishes. Retain the inner reliable frame until a transport
                // key is routable instead of silently losing its first assignment.
                EnqueueForPeer(peer, data, retainReliableReservation: true, requeue: true);
                break;
            }

            long sentTicks = CurrentEngineTicks();
            peer.RttBuffer[sequence] = sentTicks;
            if (data.ResendOnFailedAck)
                peer.MustAck[sequence] = new PendingAckPacket(data.OwnerId, data.Bytes, data.FirstSendTicks == 0L ? sentTicks : data.FirstSendTicks, data.TimeoutTicks);

            client.Send(scratch.AsSpan(0, written), peer.EndPoint);
            RecordBytesSent(written);
            packetsSent++;
        }

        if (packetsSent < packetsAllowed)
            packetsSent += DrainHighRateRing(client, peer, packetsAllowed - packetsSent);

        peer.PacketTokens -= packetsSent;
        if (peer.PacketTokens < 0)
            peer.PacketTokens = 0.0;
    }

    private void ClearOldRTTs(UdpPeerState peer)
    {
        if (peer.RttBuffer.IsEmpty)
            return;

        long nowTicks = CurrentEngineTicks();
        long oldestTicks = GetWindowStartTicks(nowTicks, MaxRoundTripSec);
        foreach (KeyValuePair<ushort, long> entry in peer.RttBuffer)
        {
            if (entry.Value >= oldestTicks)
                continue;

            ushort key = entry.Key;
            if (!peer.RttBuffer.TryRemove(key, out _))
                continue;

            if (!peer.MustAck.TryRemove(key, out PendingAckPacket pending))
                continue;

            if (pending.TimeoutTicks > 0L && nowTicks - pending.FirstSendTicks >= pending.TimeoutTicks)
            {
                ReleaseReliablePacket(peer, true);
                double timeoutSeconds = TickDeltaToSeconds(pending.TimeoutTicks, 0L);
                Debug.Out($"Required packet sequence {key} to {peer.EndPoint} timed out after {timeoutSeconds:0.###}s, dropping...");
                continue;
            }

            if (ShouldResendRequiredPacket(pending.OwnerId))
            {
                // The resend takes a fresh sequence when it is transmitted, so the receiver
                // processes it instead of discarding it as an out-of-order duplicate.
                Debug.Out($"Required packet sequence {key} to {peer.EndPoint} failed to return, resending...");
                EnqueueForPeer(peer, new QueuedUdpPacket(pending.Bytes, true, pending.OwnerId, pending.FirstSendTicks, pending.TimeoutTicks), retainReliableReservation: true);
            }
            else
                ReleaseReliablePacket(peer, true);
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
    private static ReadOnlySpan<byte> Protocol => RealtimeWireProtocol.FrameMagic;
    private const ushort _halfMaxSeq = 32768;
    /// <summary>
    /// Compares two sequence numbers, accounting for the wrap-around point at half the maximum value.
    /// Returns true if left is greater than right, false otherwise.
    /// </summary>
    private static bool SeqGreater(ushort left, ushort right) =>
        ((left > right) && (left - right <= _halfMaxSeq)) ||
        ((left < right) && (right - left > _halfMaxSeq));
    /// <summary>
    /// Returns the difference between two sequence numbers, accounting for the wrap-around point at half the maximum value.
    /// If left is greater than right, returns left - right.
    /// Else, returns the wrapped-around difference.
    /// </summary>
    private static int DiffSeq(ushort left, ushort right)
        => left > right
        ? left - right
        : (left - 0) + (ushort.MaxValue - right) + 1; //+1, because if right is ushort.MaxValue and left is 0, the difference is 1

    /// <summary>
    /// Broadcasts the entire object to all connected clients.
    /// </summary>
    public void ReplicateObject(RuntimeWorldObjectBase obj, bool compress, bool resendOnFailedAck, float maxAckWaitSec = DefaultAckTimeoutSec)
    {
        var bytes = MemoryPackSerializer.Serialize(obj);
        Send(obj.ID, compress, bytes, EBroadcastType.Object, resendOnFailedAck, maxAckWaitSec);
    }

    /// <summary>
    /// Broadcasts arbitrary data to all connected clients.
    /// </summary>
    public void ReplicateData(RuntimeWorldObjectBase obj, byte[] value, string idStr, bool compress, bool resendOnFailedAck, float maxAckWaitSec = DefaultAckTimeoutSec)
    {
        IdValue data = new(idStr, value);
        var bytes = MemoryPackSerializer.Serialize(data);
        Send(obj.ID, compress, bytes, EBroadcastType.Data, resendOnFailedAck, maxAckWaitSec);
    }

    /// <summary>
    /// Broadcasts a property update to all connected clients.
    /// </summary>
    public void ReplicatePropertyUpdated<T>(RuntimeWorldObjectBase obj, string? propName, T value, bool compress, bool resendOnFailedAck, float maxAckWaitSec = DefaultAckTimeoutSec)
    {
        var bytes1 = MemoryPackSerializer.Serialize(value);
        IdValue data = new(propName ?? string.Empty, bytes1);
        var bytes = MemoryPackSerializer.Serialize(data);
        Send(obj.ID, compress, bytes, EBroadcastType.Property, resendOnFailedAck, maxAckWaitSec);
    }

    /// <summary>
    /// Broadcasts a transform update to all connected clients.
    /// The transform handles the encoding and decoding of its own data.
    /// </summary>
    public void ReplicateTransform(TransformBase transform, bool resendOnFailedAck, float maxAckWaitSec = DefaultAckTimeoutSec)
    {
        // A coalesced transform cannot depend on a delta that the queue can replace.
        byte[] bytes = UseBoundedRealtimeQueues ? transform.EncodeToBytes(false) : transform.EncodeToBytes();
        Send(transform.ID, false, bytes, EBroadcastType.Transform, resendOnFailedAck, maxAckWaitSec);
    }

    private const int HeaderLen = 16; //3 bytes for protocol, 1 byte for flags, 2 bytes for sequence, 2 bytes for ack, 4 bytes for ack bitfield, 4 bytes for data length (not including header or guid)
    private const int GuidLen = 16; //Guid is always 16 bytes
    private SevenZip.Compression.LZMA.Encoder _encoder = new();
    private SevenZip.Compression.LZMA.Decoder _decoder = new();
    private MemoryStream _compStreamIn = new();
    private MemoryStream _compStreamOut = new();
    private MemoryStream _decompStreamIn = new();
    private MemoryStream _decompStreamOut = new();
    private readonly object _compressionStateSync = new();
    private readonly object _decompressionStateSync = new();

    /// <summary>
    /// Queues a reliable-lane frame for every current send target.
    /// </summary>
    /// <param name="id">The id of the object to replicate information to.</param>
    /// <param name="compress">If the packet's data should be compressed - adds latency.</param>
    /// <param name="data">The data to send. It is copied before this method returns.</param>
    /// <param name="type">The type of replication this is - each type is optimized for its use case.</param>
    /// <param name="resendOnFailedAck">If the packet MUST be recieved - if it fails to be acknowledged by the receiver, it will be sent again until it is.</param>
    /// <param name="maxAckWaitSec">Maximum total time to wait for an ack before giving up.</param>
    protected void Send(Guid id, bool compress, ReadOnlySpan<byte> data, EBroadcastType type, bool resendOnFailedAck, float maxAckWaitSec = DefaultAckTimeoutSec)
    {
        lock (_sendTargetSync)
        {
            _sendTargetsScratch.Clear();
            CollectUdpSendTargets(_sendTargetsScratch);
            SendToTargets(_sendTargetsScratch, id, compress, data, type, resendOnFailedAck, maxAckWaitSec);
            _sendTargetsScratch.Clear();
        }
    }

    protected void SendToTarget(IPEndPoint target, Guid id, bool compress, ReadOnlySpan<byte> data, EBroadcastType type, bool resendOnFailedAck, float maxAckWaitSec = DefaultAckTimeoutSec)
    {
        ArgumentNullException.ThrowIfNull(target);
        byte[]? compressed = PrepareReliablePayload(id, compress, data, out int payloadDataLen, out int wirePayloadLength);
        EnqueueReliableFrame(target, id, compress, data, compressed, payloadDataLen, wirePayloadLength, type, resendOnFailedAck, maxAckWaitSec);
    }

    protected void SendToTargets(IReadOnlyList<IPEndPoint> targets, Guid id, bool compress, ReadOnlySpan<byte> data, EBroadcastType type, bool resendOnFailedAck, float maxAckWaitSec = DefaultAckTimeoutSec)
    {
        if (targets.Count == 0)
            return;

        byte[]? compressed = PrepareReliablePayload(id, compress, data, out int payloadDataLen, out int wirePayloadLength);
        for (int i = 0; i < targets.Count; i++)
            EnqueueReliableFrame(targets[i], id, compress, data, compressed, payloadDataLen, wirePayloadLength, type, resendOnFailedAck, maxAckWaitSec);
    }

    /// <summary>Validates the payload size and, when requested, compresses the object id plus data once for all targets.</summary>
    private byte[]? PrepareReliablePayload(Guid id, bool compress, ReadOnlySpan<byte> data, out int payloadDataLen, out int wirePayloadLength)
    {
        if (data.Length > MaxInboundFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(data), $"Realtime payload exceeds the {MaxInboundFrameBytes}-byte transport limit.");

        byte[]? compressed = null;
        payloadDataLen = data.Length;
        if (compress)
        {
            byte[] uncompressed = new byte[GuidLen + data.Length];
            id.TryWriteBytes(uncompressed);
            data.CopyTo(uncompressed.AsSpan(GuidLen));
            lock (_compressionStateSync)
                compressed = Compression.Compress(uncompressed, ref _encoder, ref _compStreamIn, ref _compStreamOut);
            payloadDataLen = compressed.Length;
        }

        wirePayloadLength = compress ? payloadDataLen : GuidLen + data.Length;
        if (wirePayloadLength > MaxInboundFrameBytes || HeaderLen + wirePayloadLength > MaxInboundDatagramBytes)
            throw new InvalidOperationException("Compressed realtime payload exceeds the transport datagram limit.");

        return compressed;
    }

    private void EnqueueReliableFrame(IPEndPoint target, Guid id, bool compress, ReadOnlySpan<byte> data, byte[]? compressed, int payloadDataLen, int wirePayloadLength, EBroadcastType type, bool resendOnFailedAck, float maxAckWaitSec)
    {
        UdpPeerState peer = RegisterUdpPeer(target);
        byte[] frame = new byte[HeaderLen + wirePayloadLength];
        WriteFrameHeader(frame, EncodeFlags(compress, type), payloadDataLen);
        if (compress)
        {
            compressed!.CopyTo(frame.AsSpan(HeaderLen));
        }
        else
        {
            id.TryWriteBytes(frame.AsSpan(HeaderLen, GuidLen));
            data.CopyTo(frame.AsSpan(HeaderLen + GuidLen));
        }

        EnqueueForPeer(peer, new QueuedUdpPacket(frame, resendOnFailedAck, id, 0L, resendOnFailedAck ? SecondsToStopwatchTicks(maxAckWaitSec) : 0L));
    }

    protected virtual void CollectUdpSendTargets(List<IPEndPoint> targets)
    {
        if (MulticastEndPoint is not null)
            targets.Add(MulticastEndPoint);
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

    /// <summary>
    /// Assigns the next outbound sequence and snapshots the acknowledgement state. The ack is the
    /// highest remote sequence seen, and bit n-1 of the bitfield acknowledges remote sequence
    /// <c>ack - n</c>.
    /// </summary>
    private static void NextSequence_NoLock(UdpPeerState peer, out ushort sequence, out ushort ack, out uint ackBits)
    {
        peer.LocalSequence = peer.LocalSequence == ushort.MaxValue ? (ushort)0 : (ushort)(peer.LocalSequence + 1);
        sequence = peer.LocalSequence;
        ack = peer.HasRemoteSequence ? peer.MaxRemoteSequence : (ushort)0;
        ackBits = peer.HasRemoteSequence ? peer.RemoteSequenceBits : 0u;
    }

    /// <summary>
    /// Records a received sequence in the fixed 33-entry window. Returns true only when the
    /// sequence is newer than everything seen so far; an older or duplicate sequence is still
    /// acknowledged but its data is not processed.
    /// </summary>
    private static bool RecordRemoteSequence(UdpPeerState peer, ushort sequence)
    {
        lock (peer.Sync)
        {
            if (!peer.HasRemoteSequence)
            {
                peer.HasRemoteSequence = true;
                peer.MaxRemoteSequence = sequence;
                peer.RemoteSequenceBits = 0u;
                return true;
            }

            if (SeqGreater(sequence, peer.MaxRemoteSequence))
            {
                int shift = DiffSeq(sequence, peer.MaxRemoteSequence);
                peer.RemoteSequenceBits = shift > 32
                    ? 0u
                    : (shift == 32 ? 0u : peer.RemoteSequenceBits << shift) | (1u << (shift - 1));
                peer.MaxRemoteSequence = sequence;
                return true;
            }

            if (sequence == peer.MaxRemoteSequence)
                return false;

            int distance = DiffSeq(peer.MaxRemoteSequence, sequence);
            if (distance <= 32)
                peer.RemoteSequenceBits |= 1u << (distance - 1);
            return false;
        }
    }

    private static void WriteFrameHeader(Span<byte> frame, byte flags, int dataLen)
    {
        //When we compose packet headers,
        //the local sequence becomes the sequence number of the packet,
        //and the remote sequence becomes the ack.
        //The ack bitfield covers the 32 sequences before the ack:
        //bit n-1 is set if sequence number (remote sequence - n) was received.
        Protocol.CopyTo(frame);
        frame[3] = flags;
        frame.Slice(4, 8).Clear();
        BinaryPrimitives.WriteInt32LittleEndian(frame[12..], dataLen);
    }

    private static void WriteSequenceFields(Span<byte> frame, ushort sequence, ushort ack, uint ackBits)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(frame[4..], sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[6..], ack);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[8..], ackBits);
    }

    /// <summary>
    /// Scratch for decompressed inbound frames. It is reused by every receive-loop decode, so a
    /// span into it is valid only until the next frame is decoded.
    /// </summary>
    protected byte[] _decompBuffer = new byte[MaxInboundDecompressedBytes];

    /// <summary>Parses every frame in one accepted datagram and dispatches the in-order ones.</summary>
    protected int ReadReceivedData(byte[] inBuf, int start, int length, byte[] decompBuffer, ref bool anyAcked, IPEndPoint? sender)
    {
        if (inBuf is null || start < 0 || start > inBuf.Length || length < 0 || length > inBuf.Length - start || length > MaxInboundDatagramBytes)
            return 0;
        if (RealtimeWireProtocol.IsIncompatible(inBuf.AsSpan(start, length)))
        {
            RecordWireProtocolMismatch();
            return 0;
        }

        int end = start + length;
        int offset = start;
        while (end - offset >= HeaderLen)
        {
            //Search for protocol
            if (!inBuf.AsSpan(offset, 3).SequenceEqual(Protocol))
            {
                //Skip to next byte
                offset++;
                continue;
            }

            ReadOnlySpan<byte> header = inBuf.AsSpan(offset, HeaderLen);
            DecodeFlags(out bool compressed, out EBroadcastType type, header[3]);
            ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            ushort ack = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
            uint ackBitfield = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            int dataLength = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            offset += HeaderLen;

            if (type > EBroadcastType.Transform || dataLength < 0 || dataLength > MaxInboundFrameBytes)
            {
                Debug.NetworkingWarning("[Net] Dropped malformed realtime frame from {0}: type={1}, length={2}.", sender?.ToString() ?? "<unknown>", type, dataLength);
                return 0;
            }

            // Do not create peer/ACK/replay state for an endpoint that the role has not
            // authorized for this frame class. Server admission may explicitly allow a
            // join state frame, while all later messages require its admitted binding.
            if (!IsAllowedInboundSender(sender, type))
                return 0;

            int wirePayloadLength = compressed ? dataLength : dataLength + GuidLen;
            if (end - offset < wirePayloadLength)
                return 0;

            if (!TryDecodeRealtimeFrame(inBuf.AsMemory(offset - HeaderLen, HeaderLen + wirePayloadLength), decompBuffer,
                out _, out Guid ownerId, out ReadOnlySpan<byte> data, sender))
                return 0;

            if (!TryRegisterInboundUdpPeer(sender!, out UdpPeerState peer))
                return 0;

            if (!TryConsumeInboundBudget(peer, HeaderLen + wirePayloadLength))
            {
                Interlocked.Increment(ref _rateLimitedRejects);
                Debug.NetworkingWarning("[Net] Dropped rate-limited realtime frame from {0}.", sender?.ToString() ?? "<unknown>");
                return 0;
            }

            bool shouldRead = RecordRemoteSequence(peer, seq);

            //When a packet is received,
            //ack bitfield is scanned and if bit n is set,
            //then we acknowledge sequence number packet sequence - n,
            //if it has not been acked already.
            if (ack != 0 || ackBitfield != 0)
                anyAcked |= AcknowledgeSeq(peer, ack);
            for (int i = 0; i < 32; i++)
                if ((ackBitfield & (1u << i)) != 0)
                    anyAcked |= AcknowledgeSeq(peer, (ushort)(ack - i - 1));

            // Only propagate data if this sequence should be processed (in order)
            if (shouldRead)
                Propogate(ownerId, type, data, sender);

            offset += wirePayloadLength;
        }
        return offset - start;
    }

    /// <summary>
    /// Applies role-specific source/direction policy before a datagram can mutate peer
    /// sequencing, acknowledgements, RTT, or application state. Implementations must
    /// not derive authorization from an application payload identifier.
    /// </summary>
    protected virtual bool IsAllowedInboundSender(IPEndPoint? sender, EBroadcastType type)
        => sender is not null;

    /// <summary>
    /// Verifies an outer managed UDP envelope before the inner realtime packet can update peer
    /// state. Roles own handshake and association state; the default is fail-closed. A successful
    /// unwrap returns a slice of <paramref name="datagram"/>, not a copy.
    /// </summary>
    protected virtual bool TryUnwrapManagedDatagram(ReadOnlyMemory<byte> datagram, IPEndPoint sender, out ReadOnlyMemory<byte> innerDatagram)
    {
        innerDatagram = default;
        return false;
    }

    /// <summary>When enabled, bare realtime packets are rejected before they can create peer or ACK state.</summary>
    protected virtual bool RequiresManagedUdpTransport => false;

    /// <summary>
    /// Writes the wire form of a queued inner realtime datagram into <paramref name="destination"/>
    /// immediately before transmission, so retries receive fresh outer counters. The default
    /// copies the datagram unchanged. Returns false when the target is not routable yet.
    /// </summary>
    protected virtual bool TryProtectOutboundDatagram(ReadOnlySpan<byte> innerDatagram, IPEndPoint target, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        if (innerDatagram.Length > destination.Length)
            return false;

        innerDatagram.CopyTo(destination);
        bytesWritten = innerDatagram.Length;
        return true;
    }

    private bool AcknowledgeSeq(UdpPeerState peer, ushort ackedSeq)
    {
        if (peer.RttBuffer.TryRemove(ackedSeq, out long timeTicks))
        {
            UpdateRTT((float)TickDeltaToSeconds(CurrentEngineTicks(), timeTicks));

            if (peer.MustAck.TryRemove(ackedSeq, out _))
            {
                ReleaseReliablePacket(peer, true);
                Debug.Out($"Acknowledged required sequence number {ackedSeq} from {peer.EndPoint}");
            }

            //Can't print here otherwise because it will be repeated up to 32 extra times according to the ack bitfield
            return true; //We acknowledged the sequence number
        }

        return TryAcknowledgeHighRateSequence(peer, ackedSeq);
    }

    private float _averageRTT = 0.0f;
    public float AverageRoundTripTimeSec
    {
        get => _averageRTT;
        // Updated for every acknowledged packet, so it opts out of change-event publication.
        private set => SetField(ref _averageRTT, value, publishNotifications: false);
    }

    public float AverageRoundTripTimeMs => MathF.Round(AverageRoundTripTimeSec * 1000.0f);

    private float _rttSmoothingPercent = 0.1f;
    public float RTTSmoothingPercent
    {
        get => _rttSmoothingPercent;
        set => SetField(ref _rttSmoothingPercent, value);
    }

    private void UpdateRTT(float rttSec)
        => AverageRoundTripTimeSec = Interp.Lerp(AverageRoundTripTimeSec, rttSec, RTTSmoothingPercent);

    /// <summary>
    /// Finds the target object in the cache and applies the data to it.
    /// </summary>
    /// <param name="id">The GUID of the object to update.</param>
    /// <param name="type">The type of update to propogate.</param>
    /// <param name="data">The frame data for this object. Valid only for the duration of the call.</param>
    /// <param name="sender">The endpoint the frame arrived from.</param>
    protected void Propogate(Guid id, EBroadcastType type, ReadOnlySpan<byte> data, IPEndPoint? sender)
    {
        if (type == EBroadcastType.StateChange)
        {
            DispatchStateChangeFrame(data, sender);
            return;
        }

        if (!XRObjectBase.ObjectsCache.TryGetValue(id, out var obj) || obj is not RuntimeWorldObjectBase worldObj)
            return;

        switch (type)
        {
            case EBroadcastType.Object:
                {
                    var newObj = MemoryPackSerializer.Deserialize<RuntimeWorldObjectBase>(data);

                    if (newObj is not null)
                        worldObj.CopyFrom(newObj);
                    break;
                }
            case EBroadcastType.Property:
                {
                    IdValue d = MemoryPackSerializer.Deserialize<IdValue>(data);

                    worldObj.SetReplicatedProperty(d.key, d.value);
                    break;
                }
            case EBroadcastType.Data:
                {
                    IdValue d = MemoryPackSerializer.Deserialize<IdValue>(data);

                    worldObj.ReceiveData(d.key, d.value);
                    break;
                }
            case EBroadcastType.Transform:
                {
                    if (worldObj is TransformBase transform)
                        transform.DecodeFromBytes(data.ToArray());
                    break;
                }
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
    private static void RequireHostFileTransfer()
    {
        if (OperatingSystem.IsBrowser() || RuntimeWorkScheduler.IsCallerThread
            || DirectStorageIO.Source is { SupportsSynchronousReads: false } or IRuntimeAssetCatalog)
            throw new NotSupportedException(
                "NetworkFileTransfer.HostFileUnavailable: this host cannot transfer operating-system file paths; use an already opened stream with an available transport.");
    }

    public static async Task SendFileAsync(string filePath, string targetIP, int port, IProgress<double> progress)
    {
        RequireHostFileTransfer();
        var fileInfo = new FileInfo(filePath);
        long fileLength = fileInfo.Length;

        using Stream ns = await NetworkTransportServices.Required.ConnectStreamAsync(targetIP, port);

        byte[] lengthBytes = BitConverter.GetBytes(fileLength);
        await ns.WriteAsync(lengthBytes);

        byte[] buffer = new byte[8192];
        long totalSent = 0;
        RequireHostFileTransfer();
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
        using Stream ns = await NetworkTransportServices.Required.ConnectStreamAsync(targetIP, port);
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
        RequireHostFileTransfer();
        using Stream ns = await NetworkTransportServices.Required.AcceptStreamAsync(port);
        byte[] lengthBytes = new byte[8];
        await ns.ReadExactlyAsync(lengthBytes);
        long fileLength = BitConverter.ToInt64(lengthBytes);
        byte[] buffer = new byte[8192];
        long totalReceived = 0;
        RequireHostFileTransfer();
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
        using Stream ns = await NetworkTransportServices.Required.AcceptStreamAsync(port);
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

    #endregion
}
