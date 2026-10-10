using System.Buffers.Binary;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private const int EngineStorageCapacity = 8 * 1024 * 1024;
    private const int EngineMaximumUploads = 4096;
    private const int EngineMaximumPacketUploads = 2 * EngineMaximumUploads;
    private const int EngineUploadPayloadCapacity = 2 * EngineStorageCapacity;
    private const int EngineUploadRecordBytes = 24;
    private readonly byte[] _engineUploadArena = new byte[EngineMaximumPacketUploads * EngineUploadRecordBytes];
    private readonly byte[] _engineStorageArena = new byte[EngineUploadPayloadCapacity];
    private readonly List<WebGpuDataBuffer> _enginePendingStorage = new(EngineMaximumPacketUploads);
    private int _engineUploadCount;
    private int _engineStorageBytes;
    private int _engineRetryUploadCount;
    private int _engineRetryUploadBytes;
    private bool _engineUploadsSubmitted;
    private bool _engineUploadsNeedCompaction;

    internal void RegisterPendingStorage(WebGpuDataBuffer buffer)
    {
        if (!_enginePendingStorage.Contains(buffer))
            _enginePendingStorage.Add(buffer);
    }

    internal void UnregisterPendingStorage(WebGpuDataBuffer buffer)
        => _enginePendingStorage.Remove(buffer);

    private void DiscardRetiredBufferUploads(int handle)
    {
        if (_engineUploadsSubmitted) return;
        // Retirement outside recording makes these writes unobservable. Mark them
        // frame-local so the next preamble compaction frees their exact byte ranges.
        // Keep this attempt's recorded statistics intact through cleanup.
        for (int index = 0; index < _engineUploadCount; index++)
        {
            Span<byte> upload = _engineUploadArena.AsSpan(index * EngineUploadRecordBytes, EngineUploadRecordBytes);
            if (BinaryPrimitives.ReadInt32LittleEndian(upload) == handle)
            {
                BinaryPrimitives.WriteInt32LittleEndian(upload[20..], 0);
                SetField(ref _engineUploadsNeedCompaction, true, publishNotifications: false);
            }
        }
    }

    private void AcceptSubmittedBufferUploads()
    {
        Exception? failure = null;
        while (_enginePendingStorage.Count != 0)
        {
            int index = _enginePendingStorage.Count - 1;
            WebGpuDataBuffer buffer = _enginePendingStorage[index];
            _enginePendingStorage.RemoveAt(index);
            try { buffer.AcceptSubmittedUploads(); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal void StageEngineStorageUpload(int handle, int destinationOffset, ReadOnlySpan<byte> bytes)
        => StageEngineBufferUpload(handle, destinationOffset, bytes, retainUntilSubmitted: false);

    /// <summary>Preflights current-attempt uploads independently of the bounded retry preamble.</summary>
    internal bool CanStageEngineStorageUploads(long byteCount, int recordCount)
        => byteCount >= 0 && recordCount >= 0 &&
           byteCount <= EngineStorageCapacity - (_engineStorageBytes - _engineRetryUploadBytes) &&
           recordCount <= EngineMaximumUploads - (_engineUploadCount - _engineRetryUploadCount);

    /// <summary>Copies an authored mutation immediately; failed frame attempts retain its exact bytes.</summary>
    internal void StageEngineBufferUpload(int handle, int destinationOffset, ReadOnlySpan<byte> bytes,
        bool retainUntilSubmitted = true)
    {
        RequireReady();
        if (!_resources.Contains(handle) || (!_engineRecording && !retainUntilSubmitted) ||
            (_engineRecording && _engineUploadsSubmitted))
            throw new InvalidOperationException("WebGPU.Frame.BufferUploadOwner: uploads require a live owned buffer before its frame submission.");
        if (retainUntilSubmitted)
            PrepareEngineBufferMutation(handle, destinationOffset, bytes.Length, 1);
        if (bytes.IsEmpty || destinationOffset < 0 || (destinationOffset | bytes.Length) % 4 != 0 ||
            _engineUploadCount == EngineMaximumPacketUploads || bytes.Length > EngineUploadPayloadCapacity - _engineStorageBytes ||
            (_engineRecording && !CanStageEngineStorageUploads(bytes.Length, 1)))
            throw new InvalidOperationException("WebGPU.Frame.BufferUploadCapacity: aligned buffer snapshots exceed the bounded frame arena, including retained unsubmitted mutations.");
        bytes.CopyTo(_engineStorageArena.AsSpan(_engineStorageBytes, bytes.Length));
        Span<byte> upload = _engineUploadArena.AsSpan(_engineUploadCount * EngineUploadRecordBytes, EngineUploadRecordBytes);
        upload.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(upload, handle);
        BinaryPrimitives.WriteInt32LittleEndian(upload[4..], destinationOffset);
        BinaryPrimitives.WriteInt32LittleEndian(upload[8..], _engineStorageBytes);
        BinaryPrimitives.WriteInt32LittleEndian(upload[12..], bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(upload[16..], _engineRecording ? _engineCommandCount : 0);
        BinaryPrimitives.WriteInt32LittleEndian(upload[20..], retainUntilSubmitted ? 1 : 0);
        SetField(ref _engineStorageBytes, _engineStorageBytes + bytes.Length, publishNotifications: false);
        SetField(ref _engineUploadCount, _engineUploadCount + 1, publishNotifications: false);
        if (_engineRecording)
            SetField(ref _engineUploadsNeedCompaction, true, publishNotifications: false);
        else
        {
            SetField(ref _engineRetryUploadCount, _engineUploadCount, publishNotifications: false);
            SetField(ref _engineRetryUploadBytes, _engineStorageBytes, publishNotifications: false);
        }
    }

    /// <summary>Reserves a complete mutation, reclaiming only covered retry writes before the first new command.</summary>
    internal void PrepareEngineBufferMutation(int handle, int destinationOffset, int byteLength, int recordCount)
    {
        RequireReady();
        if (!_resources.Contains(handle) || (_engineRecording && _engineUploadsSubmitted))
            throw new InvalidOperationException("WebGPU.Frame.BufferUploadOwner: mutations require a live owned buffer before its frame submission.");
        if (!_engineRecording && (_engineUploadsSubmitted || _engineUploadsNeedCompaction))
            BeginEngineBufferUploads();
        int removedCount = 0, removedBytes = 0;
        if (!_engineRecording || _engineCommandCount == 0)
            for (int index = 0; index < _engineRetryUploadCount; index++)
            {
                ReadOnlySpan<byte> upload = _engineUploadArena.AsSpan(index * EngineUploadRecordBytes, EngineUploadRecordBytes);
                if (!EngineUploadCoveredBy(upload, handle, destinationOffset, byteLength)) continue;
                removedCount++;
                removedBytes += BinaryPrimitives.ReadInt32LittleEndian(upload[12..]);
            }
        bool exceedsBudget = _engineRecording
            ? !CanStageEngineStorageUploads(byteLength, recordCount)
            : byteLength > EngineStorageCapacity - _engineStorageBytes + removedBytes ||
              recordCount > EngineMaximumUploads - _engineUploadCount + removedCount;
        if (byteLength <= 0 || recordCount <= 0 || destinationOffset < 0 || (destinationOffset | byteLength) % 4 != 0 || exceedsBudget)
            throw new InvalidOperationException("WebGPU.Frame.BufferUploadCapacity: exact buffer mutations exceed the bounded frame arena, including unsubmitted retry ranges.");
        if (removedCount == 0) return;
        int count = 0, bytes = 0;
        for (int index = 0; index < _engineUploadCount; index++)
        {
            Span<byte> upload = _engineUploadArena.AsSpan(index * EngineUploadRecordBytes, EngineUploadRecordBytes);
            if (index < _engineRetryUploadCount && EngineUploadCoveredBy(upload, handle, destinationOffset, byteLength)) continue;
            CopyEngineUpload(upload, count++, ref bytes);
        }
        SetField(ref _engineRetryUploadCount, _engineRetryUploadCount - removedCount, publishNotifications: false);
        SetField(ref _engineRetryUploadBytes, _engineRetryUploadBytes - removedBytes, publishNotifications: false);
        SetField(ref _engineUploadCount, count, publishNotifications: false);
        SetField(ref _engineStorageBytes, bytes, publishNotifications: false);
    }

    private static bool EngineUploadCoveredBy(ReadOnlySpan<byte> upload, int handle, int offset, int length)
    {
        int start = BinaryPrimitives.ReadInt32LittleEndian(upload[4..]);
        int bytes = BinaryPrimitives.ReadInt32LittleEndian(upload[12..]);
        return BinaryPrimitives.ReadInt32LittleEndian(upload) == handle && start >= offset &&
            (long)start + bytes <= (long)offset + length;
    }

    private void CopyEngineUpload(Span<byte> upload, int index, ref int bytes)
    {
        int sourceOffset = BinaryPrimitives.ReadInt32LittleEndian(upload[8..]);
        int length = BinaryPrimitives.ReadInt32LittleEndian(upload[12..]);
        _engineStorageArena.AsSpan(sourceOffset, length).CopyTo(_engineStorageArena.AsSpan(bytes, length));
        BinaryPrimitives.WriteInt32LittleEndian(upload[8..], bytes);
        upload.CopyTo(_engineUploadArena.AsSpan(index * EngineUploadRecordBytes, EngineUploadRecordBytes));
        bytes += length;
    }

    internal bool HasPendingEngineBufferUpload(int handle)
        => HasUnsubmittedEngineBufferUpload(handle, authoredOnly: true);

    internal bool HasUnsubmittedEngineBufferUpload(int handle, bool authoredOnly = false)
        => handle != 0 && FindUnsubmittedEngineBufferUpload(handle, authoredOnly);

    private bool FindUnsubmittedEngineBufferUpload(int handle, bool authoredOnly)
    {
        if (_engineUploadsSubmitted) return false;
        for (int index = 0; index < _engineUploadCount; index++)
        {
            ReadOnlySpan<byte> upload = _engineUploadArena.AsSpan(index * EngineUploadRecordBytes, EngineUploadRecordBytes);
            bool retained = BinaryPrimitives.ReadInt32LittleEndian(upload[20..]) == 1;
            if ((retained || _engineRecording) && (!authoredOnly || retained) &&
                (handle == 0 || BinaryPrimitives.ReadInt32LittleEndian(upload) == handle))
                return true;
        }
        return false;
    }

    private void BeginEngineBufferUploads()
    {
        if (!_engineUploadsSubmitted && !_engineUploadsNeedCompaction)
        {
            RequireEngineRetryUploadCapacity(_engineUploadCount, _engineStorageBytes);
            SetField(ref _engineRetryUploadCount, _engineUploadCount, publishNotifications: false);
            SetField(ref _engineRetryUploadBytes, _engineStorageBytes, publishNotifications: false);
            return;
        }
        int count = 0, bytes = 0;
        if (!_engineUploadsSubmitted)
            for (int index = 0; index < _engineUploadCount; index++)
            {
                Span<byte> upload = _engineUploadArena.AsSpan(index * EngineUploadRecordBytes, EngineUploadRecordBytes);
                if (BinaryPrimitives.ReadInt32LittleEndian(upload[20..]) == 0) continue;
                bool superseded = false;
                for (int later = index + 1; later < _engineUploadCount; later++)
                {
                    ReadOnlySpan<byte> next = _engineUploadArena.AsSpan(later * EngineUploadRecordBytes, EngineUploadRecordBytes);
                    if (BinaryPrimitives.ReadInt32LittleEndian(next[20..]) != 0 &&
                        EngineUploadCoveredBy(upload, BinaryPrimitives.ReadInt32LittleEndian(next),
                            BinaryPrimitives.ReadInt32LittleEndian(next[4..]), BinaryPrimitives.ReadInt32LittleEndian(next[12..])))
                    {
                        superseded = true;
                        break;
                    }
                }
                if (superseded) continue;
                // No draw from the rejected attempt reached the queue. Replay its
                // authored writes in order before any commands of the next attempt.
                BinaryPrimitives.WriteInt32LittleEndian(upload[16..], 0);
                CopyEngineUpload(upload, count++, ref bytes);
            }
        SetField(ref _engineUploadCount, count, publishNotifications: false);
        SetField(ref _engineStorageBytes, bytes, publishNotifications: false);
        SetField(ref _engineRetryUploadCount, count, publishNotifications: false);
        SetField(ref _engineRetryUploadBytes, bytes, publishNotifications: false);
        SetField(ref _engineUploadsSubmitted, false, publishNotifications: false);
        SetField(ref _engineUploadsNeedCompaction, false, publishNotifications: false);
        RequireEngineRetryUploadCapacity(count, bytes);
    }

    private static void RequireEngineRetryUploadCapacity(int count, int bytes)
    {
        if (bytes > EngineStorageCapacity || count > EngineMaximumUploads)
            throw new InvalidOperationException("WebGPU.Frame.RetryUploadCapacity: distinct unsubmitted buffer ranges exceed the 8 MiB or 4096-record retry preamble; retire obsolete resources or publish a smaller mutation set before retrying.");
    }

    private void RequireStandaloneSubmissionBoundary()
    {
        if (_engineRecording || _enginePreparationCount != 0 || FindUnsubmittedEngineBufferUpload(0, authoredOnly: false))
            throw new NotSupportedException("WebGPU.Commands.PendingFrameUnsupported: standalone submission cannot overtake an active engine frame or its unsubmitted buffer mutations.");
    }
}
