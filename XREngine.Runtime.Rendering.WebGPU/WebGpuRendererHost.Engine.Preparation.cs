using System.Buffers.Binary;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private const int EnginePreparationHeaderBytes = 24;
    private const int EnginePreparationRecordBytes = 48;
    private const int EngineMaximumPreparations = 4096;
    private const int EnginePreparationCapacity = 256 * 1024 * 1024;
    private readonly byte[] _enginePreparationCommands = new byte[EnginePreparationHeaderBytes + EngineMaximumPreparations * EnginePreparationRecordBytes];
    private readonly List<WebGpuDataBuffer> _engineInitializingBuffers = new(64);
    private byte[] _enginePreparationPayload = [];
    private int _enginePreparationCount;
    private int _enginePreparationBytes;
    private uint _engineCompletedSequence;
    private bool _engineAcceptanceAttempted;

    /// <summary>Owns exact initialization bytes until the shared acceptance boundary, independently of scene readiness.</summary>
    internal void StageEngineBufferPreparation(int handle, int destinationOffset, ReadOnlySpan<byte> bytes)
    {
        PrepareEngineBufferInitialization(handle, destinationOffset, bytes.Length, 1);
        bytes.CopyTo(_enginePreparationPayload.AsSpan(_enginePreparationBytes, bytes.Length));
        Span<byte> record = _enginePreparationCommands.AsSpan(
            EnginePreparationHeaderBytes + _enginePreparationCount * EnginePreparationRecordBytes, EnginePreparationRecordBytes);
        record.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(record, handle);
        BinaryPrimitives.WriteInt32LittleEndian(record[4..], destinationOffset);
        BinaryPrimitives.WriteInt32LittleEndian(record[8..], _enginePreparationBytes);
        BinaryPrimitives.WriteInt32LittleEndian(record[12..], bytes.Length);
        SetField(ref _enginePreparationCount, _enginePreparationCount + 1, publishNotifications: false);
        SetField(ref _enginePreparationBytes, _enginePreparationBytes + bytes.Length, publishNotifications: false);
    }

    /// <summary>Preflights a complete immutable image before publishing any of its padded ranges.</summary>
    internal void PrepareEngineBufferInitialization(int handle, int destinationOffset, int byteLength, int recordCount)
    {
        RequireOwnedResource(handle);
        if ((_engineRecording && _engineAcceptanceAttempted) || HasUnsubmittedEngineBufferUpload(handle))
            throw new InvalidOperationException("WebGPU.Preparation.Owner: initialization requires unsubmitted storage without an ordered mutation.");
        if (byteLength <= 0 || recordCount <= 0 || destinationOffset < 0 || (destinationOffset | byteLength) % 4 != 0)
            throw new ArgumentOutOfRangeException(nameof(byteLength), "Initialization requires positive aligned ranges.");
        int removedCount = 0, removedBytes = 0;
        for (int index = 0; index < _enginePreparationCount; index++)
        {
            ReadOnlySpan<byte> record = EnginePreparationRecord(index);
            if (BinaryPrimitives.ReadInt32LittleEndian(record[16..]) != 0 ||
                !EngineUploadCoveredBy(record, handle, destinationOffset, byteLength)) continue;
            removedCount++;
            removedBytes += BinaryPrimitives.ReadInt32LittleEndian(record[12..]);
        }
        if (recordCount > EngineMaximumPreparations - _enginePreparationCount + removedCount ||
            byteLength > EnginePreparationCapacity - _enginePreparationBytes + removedBytes)
            throw new InvalidOperationException("WebGPU.Preparation.Capacity: exact pending initialization exceeds 256 MiB or 4096 ranges; retire obsolete generations before retrying.");
        EnsureEnginePreparationCapacity(checked(_enginePreparationBytes - removedBytes + byteLength));
        if (removedCount != 0) CompactEnginePreparation(handle, destinationOffset, byteLength);
    }

    private void EnsureEnginePreparationCapacity(int required)
    {
        if (_enginePreparationPayload.Length < required)
        {
            int capacity = Math.Max(64 * 1024, _enginePreparationPayload.Length);
            while (capacity < required) capacity = checked((int)Math.Min(EnginePreparationCapacity, (long)capacity * 2));
            Array.Resize(ref _enginePreparationPayload, capacity);
        }
    }

    private Span<byte> EnginePreparationRecord(int index)
        => _enginePreparationCommands.AsSpan(EnginePreparationHeaderBytes + index * EnginePreparationRecordBytes, EnginePreparationRecordBytes);

    internal bool HasPendingEnginePreparation(int handle)
    {
        for (int index = 0; index < _enginePreparationCount; index++)
            if (BinaryPrimitives.ReadInt32LittleEndian(EnginePreparationRecord(index)) == handle) return true;
        return false;
    }

    private bool HasPendingEngineTextureCopySource(int handle)
    {
        for (int index = 0; index < _enginePreparationCount; index++)
        {
            ReadOnlySpan<byte> record = EnginePreparationRecord(index);
            if (BinaryPrimitives.ReadInt32LittleEndian(record[16..]) == 2 &&
                BinaryPrimitives.ReadInt32LittleEndian(record[36..]) == handle) return true;
        }
        return false;
    }

    private void CompactEnginePreparation(int handle, int offset = 0, int length = int.MaxValue)
    {
        int count = 0, bytes = 0;
        for (int index = 0; index < _enginePreparationCount; index++)
        {
            Span<byte> record = EnginePreparationRecord(index);
            if (BinaryPrimitives.ReadInt32LittleEndian(record) == handle &&
                (length == int.MaxValue || BinaryPrimitives.ReadInt32LittleEndian(record[16..]) == 0 &&
                    EngineUploadCoveredBy(record, handle, offset, length))) continue;
            int source = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
            int size = BinaryPrimitives.ReadInt32LittleEndian(record[12..]);
            _enginePreparationPayload.AsSpan(source, size).CopyTo(_enginePreparationPayload.AsSpan(bytes, size));
            BinaryPrimitives.WriteInt32LittleEndian(record[8..], bytes);
            record.CopyTo(EnginePreparationRecord(count++));
            bytes += size;
        }
        SetField(ref _enginePreparationCount, count, publishNotifications: false);
        SetField(ref _enginePreparationBytes, bytes, publishNotifications: false);
    }

    internal void RegisterInitializingBuffer(WebGpuDataBuffer buffer)
    {
        if (!_engineInitializingBuffers.Contains(buffer)) _engineInitializingBuffers.Add(buffer);
    }

    internal void UnregisterInitializingBuffer(WebGpuDataBuffer buffer) => _engineInitializingBuffers.Remove(buffer);

    private bool AcceptEngineFrame(Span<byte> commands, Span<byte> uniforms, Span<byte> storage, ref bool accepted)
    {
        if (!_engineRecording || _engineAcceptanceAttempted)
            throw new InvalidOperationException("WebGPU.Preparation.AcceptanceOwner: each engine attempt has one acceptance boundary.");
        SetField(ref _engineAcceptanceAttempted, true, publishNotifications: false);
        Span<byte> header = _enginePreparationCommands.AsSpan(0, EnginePreparationHeaderBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x50524758);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], _session);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], _engineFrameSequence);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], _enginePreparationCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], _enginePreparationBytes);
        CountEngineFrameSubmission(commands.Length, uniforms.Length, storage.Length);
        CountEnginePreparationSubmission(_enginePreparationCount, _enginePreparationBytes);
        PrepareEngineResourceBatch();
        double receipt = WebGpuImports.SubmitEngineFrame(_session, commands, uniforms, storage,
            _enginePreparationCommands.AsSpan(0, EnginePreparationHeaderBytes + _enginePreparationCount * EnginePreparationRecordBytes),
            _enginePreparationPayload.AsSpan(0, _enginePreparationBytes), _engineResourceDescriptions,
            _engineResourceReceipts.AsSpan(0, _engineResourceBatch.Count * EngineResourceReceiptBytes));
        accepted = true;
        if (!commands.IsEmpty) SetField(ref _engineUploadsSubmitted, true, publishNotifications: false);
        // A returned receipt transfers every preparation byte to queue ownership.
        // Clear first so later managed notifications cannot replay an accepted image.
        SetField(ref _enginePreparationCount, 0, publishNotifications: false);
        SetField(ref _enginePreparationBytes, 0, publishNotifications: false);
        AcceptEngineResourceReceipts();
        if (!double.IsFinite(receipt) || receipt < 0 || receipt > (double)_engineFrameSequence * 2 + 1 || receipt != Math.Truncate(receipt))
            throw new InvalidOperationException("WebGPU.Preparation.InvalidReceipt: the executor returned an invalid acceptance or completion watermark.");
        uint completed = checked((uint)(receipt / 2));
        if (completed < _engineCompletedSequence)
            throw new InvalidOperationException("WebGPU.Preparation.ObsoleteReceipt: the queue completion watermark regressed.");
        SetField(ref _engineCompletedSequence, completed, publishNotifications: false);
        return receipt % 2 != 0;
    }

    private void SubmitPendingEnginePreparation()
    {
        if (_engineAcceptanceAttempted) return;
        bool accepted = false;
        try
        {
            _ = AcceptEngineFrame(Span<byte>.Empty, Span<byte>.Empty, Span<byte>.Empty, ref accepted);
            CountEngineFrameSubmissionResult(presented: false);
        }
        finally { if (accepted) NotifyAcceptedBufferInitializations(); }
    }

    private void CountEnginePreparationSubmission(int ranges, int bytes)
    {
        if (!_engineFrameStatisticsActive || _engineFrameStatistics is not { } statistics) return;
        statistics.PreparationUploadRecords += ranges;
        statistics.PreparationUploadBytes += bytes;
        statistics.LastFramePreparationUploadRecords = ranges;
        statistics.LastFramePreparationUploadBytes = bytes;
    }

    private void NotifyAcceptedBufferInitializations()
    {
        Exception? failure = null;
        while (_engineInitializingBuffers.Count != 0)
        {
            int index = _engineInitializingBuffers.Count - 1;
            WebGpuDataBuffer buffer = _engineInitializingBuffers[index];
            _engineInitializingBuffers.RemoveAt(index);
            try { buffer.AcceptSubmittedInitialization(); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void ResetEnginePreparation()
    {
        ResetEngineResourceRequests();
        _engineInitializingBuffers.Clear();
        _engineRecordedTextures.Clear();
        SetField(ref _enginePreparationCount, 0, publishNotifications: false);
        SetField(ref _enginePreparationBytes, 0, publishNotifications: false);
        SetField(ref _enginePreparationPayload, [], publishNotifications: false);
        SetField(ref _engineCompletedSequence, 0u, publishNotifications: false);
    }
}
