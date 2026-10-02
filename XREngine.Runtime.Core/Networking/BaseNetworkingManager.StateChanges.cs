using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using XREngine.Data;
using XREngine.Networking;

namespace XREngine;

public abstract partial class BaseNetworkingManager
{
    public event Func<RemoteJobRequest, Task<RemoteJobResponse?>>? RemoteJobRequestReceived;
    public event Action<RemoteJobResponse>? RemoteJobResponseReceived;
    public event Action<ServerErrorMessage>? ServerErrorReceived;
    /// <summary>Raised for every accepted humanoid pose packet. The view is valid only during the call.</summary>
    public event HumanoidPosePacketHandler? HumanoidPosePacketReceived;
    public event Action<NetworkAuthorityLease>? AuthorityLeaseUpdated;
    /// <summary>Object-based clock-sync notification. A message is materialized only while this has subscribers.</summary>
    public event Action<ClockSyncMessage>? ClockSyncReceived;
    public event Action<NetworkSnapshotEnvelope>? ReplicationSnapshotReceived;
    /// <summary>Object-based delta notification. An envelope is materialized only while this has subscribers.</summary>
    public event Action<NetworkDeltaEnvelope>? ReplicationDeltaReceived;
    /// <summary>Raised for every replication-delta packet. The view is valid only during the call.</summary>
    public event NetworkDeltaPacketHandler? ReplicationDeltaPacketReceived;

    private long _frameTruncatedRejects;
    private long _frameOversizedRejects;
    private long _frameUnknownTypeRejects;
    private long _frameProtocolMismatchRejects;
    private long _frameLengthMismatchRejects;
    private long _payloadDecodeFailures;
    private long _highRateQueueDrops;
    private long _highRateOversizeFallbacks;
    private long _poseSlabExhausted;
    private int _protocolMismatchLogged;

    /// <summary>Counts of rejected state-change frames and high-rate lane drops since this manager started.</summary>
    public StateChangeRejectionSnapshot StateChangeRejections
        => new(
            Volatile.Read(ref _frameTruncatedRejects),
            Volatile.Read(ref _frameOversizedRejects),
            Volatile.Read(ref _frameUnknownTypeRejects),
            Volatile.Read(ref _frameProtocolMismatchRejects),
            Volatile.Read(ref _frameLengthMismatchRejects),
            Volatile.Read(ref _payloadDecodeFailures),
            Volatile.Read(ref _highRateQueueDrops),
            Volatile.Read(ref _highRateOversizeFallbacks),
            Volatile.Read(ref _poseSlabExhausted));

    /// <summary>Counts a high-rate packet that could not be queued for the simulation thread.</summary>
    protected void RecordPoseSlabExhausted() => Interlocked.Increment(ref _poseSlabExhausted);

    /// <summary>Counts a state-change payload that passed framing but failed its decoder or validation.</summary>
    protected void RecordPayloadDecodeFailure() => Interlocked.Increment(ref _payloadDecodeFailures);

    [ThreadStatic]
    private static RealtimeScratchBufferWriter? _stateChangeFrameWriter;

    /// <summary>
    /// Starts a state-change frame in the per-thread scratch writer with its sub-header reserved.
    /// The frame must be completed with <see cref="CompleteStateChangeFrame"/> before another
    /// frame is started on the same thread.
    /// </summary>
    private static RealtimeScratchBufferWriter BeginStateChangeFrame()
    {
        RealtimeScratchBufferWriter writer = _stateChangeFrameWriter ??= new RealtimeScratchBufferWriter(4096);
        writer.Reset();
        writer.Reserve(StateChangeFrame.HeaderSize);
        return writer;
    }

    private static ReadOnlySpan<byte> CompleteStateChangeFrame(RealtimeScratchBufferWriter writer, EStateChangeType type)
    {
        StateChangeFrame.WriteHeader(writer.WrittenSpanMutable, type, writer.WrittenCount - StateChangeFrame.HeaderSize);
        return writer.WrittenSpan;
    }

    /// <summary>
    /// Broadcasts an engine state change that is not tied to an object id. The payload bytes are
    /// written directly behind the state-change sub-header; nothing is text-encoded.
    /// </summary>
    public void ReplicateStateChange(EStateChangeType type, ReadOnlySpan<byte> payload, bool compress, bool resendOnFailedAck, float maxAckWaitSec = DefaultAckTimeoutSec)
    {
        if (payload.Length > StateChangeFrame.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), $"State-change payload exceeds the {StateChangeFrame.MaxPayloadBytes}-byte limit.");

        RealtimeScratchBufferWriter writer = BeginStateChangeFrame();
        payload.CopyTo(writer.GetSpan(payload.Length));
        writer.Advance(payload.Length);
        Send(Guid.Empty, compress, CompleteStateChangeFrame(writer, type), EBroadcastType.StateChange, resendOnFailedAck, maxAckWaitSec);
    }

    public void BroadcastRemoteJobRequest(RemoteJobRequest request, bool compress = true, bool resendOnFailedAck = false, float maxAckWaitSec = DefaultAckTimeoutSec)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AllowsRemoteJobTraffic || !IsValidRemoteJob(request))
            throw new InvalidOperationException("Remote jobs are not enabled or exceed realtime transport limits.");
        BroadcastStateChange(EStateChangeType.RemoteJobRequest, request, compress, resendOnFailedAck, maxAckWaitSec);
    }

    public void BroadcastRemoteJobResponse(RemoteJobResponse response, bool compress = true, bool resendOnFailedAck = false, float maxAckWaitSec = DefaultAckTimeoutSec)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!AllowsRemoteJobTraffic || !IsValidRemoteJob(response))
            throw new InvalidOperationException("Remote jobs are not enabled or exceed realtime transport limits.");
        BroadcastStateChange(EStateChangeType.RemoteJobResponse, response, compress, resendOnFailedAck, maxAckWaitSec);
    }

    public void BroadcastServerError(ServerErrorMessage error, bool compress = true, bool resendOnFailedAck = false, float maxAckWaitSec = DefaultAckTimeoutSec)
    {
        ArgumentNullException.ThrowIfNull(error);
        BroadcastStateChange(EStateChangeType.ServerError, error, compress, resendOnFailedAck, maxAckWaitSec);
    }

    protected void BroadcastAuthorityLeaseUpdate(NetworkAuthorityLease lease, bool compress = true, bool resendOnFailedAck = true, float maxAckWaitSec = DefaultAckTimeoutSec)
    {
        ArgumentNullException.ThrowIfNull(lease);
        BroadcastStateChange(EStateChangeType.AuthorityLeaseUpdate, lease, compress, resendOnFailedAck, maxAckWaitSec);
    }

    public void BroadcastReplicationSnapshot(NetworkSnapshotEnvelope envelope, bool compress = true, bool resendOnFailedAck = false, float maxAckWaitSec = DefaultAckTimeoutSec)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        BroadcastStateChange(EStateChangeType.ReplicationSnapshot, envelope, compress, resendOnFailedAck, maxAckWaitSec);
    }

    /// <summary>Broadcasts a control message, serialized by its generated formatter straight into the frame.</summary>
    protected void BroadcastStateChange<TPayload>(EStateChangeType type, TPayload payload, bool compress = true, bool resendOnFailedAck = false, float maxAckWaitSec = DefaultAckTimeoutSec)
    {
        RealtimeScratchBufferWriter writer = BeginStateChangeFrame();
        StateChangeCodec.Write(writer, payload);
        Send(Guid.Empty, compress, CompleteStateChangeFrame(writer, type), EBroadcastType.StateChange, resendOnFailedAck, maxAckWaitSec);
    }

    protected void SendStateChangeTo<TPayload>(IPEndPoint target, EStateChangeType type, TPayload payload, bool compress = true, bool resendOnFailedAck = false, float maxAckWaitSec = DefaultAckTimeoutSec)
    {
        RealtimeScratchBufferWriter writer = BeginStateChangeFrame();
        StateChangeCodec.Write(writer, payload);
        SendToTarget(target, Guid.Empty, compress, CompleteStateChangeFrame(writer, type), EBroadcastType.StateChange, resendOnFailedAck, maxAckWaitSec);
    }

    protected void BroadcastStateChangeToTargets<TPayload>(IReadOnlyList<IPEndPoint> targets, EStateChangeType type, TPayload payload, bool compress = true, bool resendOnFailedAck = false, float maxAckWaitSec = DefaultAckTimeoutSec)
    {
        RealtimeScratchBufferWriter writer = BeginStateChangeFrame();
        StateChangeCodec.Write(writer, payload);
        SendToTargets(targets, Guid.Empty, compress, CompleteStateChangeFrame(writer, type), EBroadcastType.StateChange, resendOnFailedAck, maxAckWaitSec);
    }

    /// <summary>
    /// Validates a state-change sub-header and dispatches its payload. Every rejection is counted
    /// and a decoder failure never escapes the receive loop.
    /// </summary>
    private void DispatchStateChangeFrame(ReadOnlySpan<byte> frame, IPEndPoint? sender)
    {
        if (!StateChangeFrame.TryRead(frame, out EStateChangeType type, out ReadOnlySpan<byte> payload, out EStateChangeFrameError error))
        {
            RecordStateChangeFrameRejection(error, frame, sender);
            return;
        }

        try
        {
            using var measurement = type is EStateChangeType.HumanoidPoseFrame or EStateChangeType.ClockSync or EStateChangeType.ReplicationDelta
                ? HighRateReceiveMeasurements.Begin(payload.Length) : default;
            HandleStateChange(type, payload, sender);
        }
        catch (Exception ex)
        {
            RecordPayloadDecodeFailure();
            Debug.NetworkingWarning("[Net] Dropped state-change frame {0} from {1}: {2}", type, sender?.ToString() ?? "<unknown>", ex.Message);
        }
    }

    private void RecordStateChangeFrameRejection(EStateChangeFrameError error, ReadOnlySpan<byte> frame, IPEndPoint? sender)
    {
        switch (error)
        {
            case EStateChangeFrameError.Truncated:
                Interlocked.Increment(ref _frameTruncatedRejects);
                break;
            case EStateChangeFrameError.Oversized:
                Interlocked.Increment(ref _frameOversizedRejects);
                break;
            case EStateChangeFrameError.UnknownType:
                Interlocked.Increment(ref _frameUnknownTypeRejects);
                break;
            case EStateChangeFrameError.LengthMismatch:
                Interlocked.Increment(ref _frameLengthMismatchRejects);
                break;
            case EStateChangeFrameError.ProtocolMismatch:
                Interlocked.Increment(ref _frameProtocolMismatchRejects);
                // Name both versions once. Repeating it per packet would flood the log.
                if (Interlocked.Exchange(ref _protocolMismatchLogged, 1) == 0)
                    Debug.NetworkingWarning("[Net] {0} Peer: {1}.", RealtimeProtocol.DescribeMismatch(StateChangeFrame.PeekWireVersion(frame)), sender?.ToString() ?? "<unknown>");
                break;
        }
    }

    /// <summary>
    /// Decodes exactly one complete state-change FRK frame without touching peer,
    /// reliability, acknowledgement, or application state. Managed transports use
    /// this before committing their outer replay counter. For a compressed frame the returned
    /// payload points into the shared decompression scratch and is valid only until the receive
    /// loop decodes its next frame.
    /// </summary>
    protected bool TryDecodeStateChangeFrame(ReadOnlyMemory<byte> frame, out EStateChangeType type, out ReadOnlySpan<byte> payload)
    {
        type = EStateChangeType.Invalid;
        payload = default;
        ReadOnlySpan<byte> span = frame.Span;
        if (span.Length < HeaderLen || !span[..3].SequenceEqual(Protocol))
            return false;

        byte flags = span[3];
        bool compressed = (flags & 1) != 0;
        if ((EBroadcastType)((flags >> 1) & 0b111) != EBroadcastType.StateChange)
            return false;

        int wireLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
        if (wireLength < 0 || wireLength > MaxInboundFrameBytes
            || span.Length != HeaderLen + wireLength + (compressed ? 0 : GuidLen))
        {
            return false;
        }

        ReadOnlySpan<byte> body;
        if (compressed)
        {
            if (!MemoryMarshal.TryGetArray(frame, out ArraySegment<byte> segment) || segment.Array is null)
                return false;

            int decodedLength;
            try
            {
                lock (_decompressionStateSync)
                    decodedLength = Compression.Decompress(segment.Array, segment.Offset + HeaderLen, wireLength, _decompBuffer, 0, ref _decoder, ref _decompStreamIn, ref _decompStreamOut);
            }
            catch
            {
                return false;
            }

            if (decodedLength < GuidLen || decodedLength > _decompBuffer.Length || new Guid(_decompBuffer.AsSpan(0, GuidLen)) != Guid.Empty)
                return false;
            body = _decompBuffer.AsSpan(GuidLen, decodedLength - GuidLen);
        }
        else
        {
            if (new Guid(span.Slice(HeaderLen, GuidLen)) != Guid.Empty)
                return false;
            body = span.Slice(HeaderLen + GuidLen, wireLength);
        }

        return StateChangeFrame.TryRead(body, out type, out payload, out _);
    }

    /// <summary>Reads a control payload and counts a decoder failure.</summary>
    protected bool TryReadStateChangePayload<TPayload>(ReadOnlySpan<byte> payload, out TPayload value) where TPayload : class
    {
        if (StateChangeCodec.TryRead(payload, out TPayload? decoded) && decoded is not null)
        {
            value = decoded;
            return true;
        }

        value = null!;
        RecordPayloadDecodeFailure();
        return false;
    }

    /// <summary>
    /// Handles one validated state change. <paramref name="payload"/> points into the receive
    /// buffer and is valid only for the duration of the call; anything deferred must be copied
    /// or decoded first.
    /// </summary>
    protected virtual void HandleStateChange(EStateChangeType type, ReadOnlySpan<byte> payload, IPEndPoint? sender)
    {
        switch (type)
        {
            case EStateChangeType.HumanoidPoseFrame:
                if (HumanoidPosePacket.TryRead(payload, out HumanoidPosePacketView pose))
                    RaiseHumanoidPosePacketReceived(pose);
                else
                    RecordPayloadDecodeFailure();
                break;

            case EStateChangeType.AuthorityLeaseUpdate:
                if (TryReadStateChangePayload(payload, out NetworkAuthorityLease lease))
                    AuthorityLeaseUpdated?.Invoke(lease);
                break;

            case EStateChangeType.ClockSync:
                if (ClockSyncPacket.TryRead(payload, out ClockSyncSample clock, out ReadOnlySpan<byte> clientIdUtf8))
                    OnClockSyncReceived(clock, clientIdUtf8);
                else
                    RecordPayloadDecodeFailure();
                break;

            case EStateChangeType.ReplicationSnapshot:
                if (TryReadStateChangePayload(payload, out NetworkSnapshotEnvelope snapshot))
                    ReplicationSnapshotReceived?.Invoke(snapshot);
                break;

            case EStateChangeType.ReplicationDelta:
                if (NetworkDeltaPacket.TryRead(payload, out NetworkDeltaPacketView delta))
                    OnReplicationDeltaReceived(delta);
                else
                    RecordPayloadDecodeFailure();
                break;

            case EStateChangeType.RemoteJobRequest:
                if (AllowsRemoteJobTraffic && TryReadStateChangePayload(payload, out RemoteJobRequest request))
                {
                    if (!IsValidRemoteJob(request))
                        return;
                    if (!string.IsNullOrWhiteSpace(request.TargetId) && !string.Equals(request.TargetId, LocalPeerId, StringComparison.OrdinalIgnoreCase))
                        return;

                    _ = DispatchRemoteJobRequestAsync(request);
                }
                break;

            case EStateChangeType.RemoteJobResponse:
                if (AllowsRemoteJobTraffic && TryReadStateChangePayload(payload, out RemoteJobResponse response))
                {
                    if (!IsValidRemoteJob(response))
                        return;
                    if (!string.IsNullOrWhiteSpace(response.TargetId) && !string.Equals(response.TargetId, LocalPeerId, StringComparison.OrdinalIgnoreCase))
                        return;

                    RemoteJobResponseReceived?.Invoke(response);
                }
                break;

            case EStateChangeType.ServerError:
                if (TryReadStateChangePayload(payload, out ServerErrorMessage serverError))
                    ServerErrorReceived?.Invoke(serverError);
                break;
        }
    }

    /// <summary>Publishes an accepted pose packet to local subscribers.</summary>
    protected void RaiseHumanoidPosePacketReceived(in HumanoidPosePacketView packet)
        => HumanoidPosePacketReceived?.Invoke(packet);

    /// <summary>
    /// Publishes a pose frame that arrived inside a session-synchronization message. The frame is
    /// encoded on the stack so subscribers see the same view as a packet from the pose channel.
    /// </summary>
    protected void RaiseHumanoidPoseFrameReceived(HumanoidPoseFrame frame)
    {
        if (HumanoidPosePacketReceived is null)
            return;

        Span<byte> buffer = stackalloc byte[HumanoidPosePacket.MaxPacketBytes];
        if (HumanoidPosePacket.TryWrite(buffer, frame, out int written) && HumanoidPosePacket.TryRead(buffer[..written], out HumanoidPosePacketView view))
            RaiseHumanoidPosePacketReceived(view);
    }

    /// <summary>Handles a clock-sync packet. The default publishes it to object-based subscribers.</summary>
    protected virtual void OnClockSyncReceived(in ClockSyncSample sample, ReadOnlySpan<byte> clientIdUtf8)
    {
        if (ClockSyncReceived is { } handler)
            handler(ClockSyncPacket.ToMessage(sample, clientIdUtf8));
    }

    /// <summary>Handles a replication-delta packet. An envelope is allocated only for object-based subscribers.</summary>
    protected virtual void OnReplicationDeltaReceived(in NetworkDeltaPacketView packet)
    {
        ReplicationDeltaPacketReceived?.Invoke(packet);
        if (ReplicationDeltaReceived is { } handler)
            handler(packet.ToEnvelope());
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

    private static bool IsValidRemoteJob(RemoteJobRequest request)
        => request.JobId != Guid.Empty
            && request.Operation.Length is > 0 and <= 128
            && IsValidRemoteJobPayload(request.Payload)
            && IsValidRemoteJobMetadata(request.Metadata)
            && IsBoundedPeerId(request.SenderId)
            && IsBoundedPeerId(request.TargetId);

    private static bool IsValidRemoteJob(RemoteJobResponse response)
        => response.JobId != Guid.Empty
            && (response.Error?.Length ?? 0) <= 1_024
            && IsValidRemoteJobPayload(response.Payload)
            && IsValidRemoteJobMetadata(response.Metadata)
            && IsBoundedPeerId(response.SenderId)
            && IsBoundedPeerId(response.TargetId);

    private static bool IsValidRemoteJobPayload(byte[]? payload)
        => payload is null || payload.Length <= 128 * 1024;

    private static bool IsValidRemoteJobMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
            return true;
        if (metadata.Count > 32)
            return false;

        foreach (KeyValuePair<string, string> pair in metadata)
        {
            if (pair.Key.Length > 128 || pair.Value.Length > 1_024)
                return false;
        }

        return true;
    }

    private static bool IsBoundedPeerId(string? value)
        => value is null || value.Length <= 128;

    /// <summary>Encodes a string as UTF-8 into caller storage. Returns false when it does not fit.</summary>
    protected static bool TryEncodeUtf8(string? value, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        if (string.IsNullOrEmpty(value))
            return true;
        if (Encoding.UTF8.GetByteCount(value) > destination.Length)
            return false;

        bytesWritten = Encoding.UTF8.GetBytes(value, destination);
        return true;
    }
}
