namespace XREngine.Rendering.WebGPU;

/// <summary>
/// Backend-owned storage for an immutable publication or GPU-produced work stream.
/// It has no second logical XRDataBuffer image; canonical CPU bytes remain database-owned.
/// </summary>
internal sealed class WebGpuOwnedStorageBuffer : AbstractRenderAPIObject
{
    private readonly WebGpuRendererHost _renderer;
    private readonly string _label;
    private readonly BrowserBufferUsage _usage;
    private int _handle;
    private int _byteLength;
    private uint _recordedFrameSequence;

    internal WebGpuOwnedStorageBuffer(WebGpuRendererHost renderer, string label,
        BrowserBufferUsage additionalUsage = 0) : base(renderer)
    {
        _renderer = renderer;
        _label = label;
        _usage = BrowserBufferUsage.Storage | BrowserBufferUsage.CopyDestination | additionalUsage;
    }

    internal int ResourceHandle => _handle;
    internal uint ByteLength => checked((uint)_byteLength);
    public override bool IsGenerated => _handle != 0;
    public override nint GetHandle() => _handle;
    public override string GetDescribingName() => _label;

    public override void Generate()
    {
        RequireLiveOwner();
        if (!IsGenerated) EnsureCapacity(16);
    }

    /// <summary>Called only for a completion-reclaimed slot, before its next commands are recorded.</summary>
    internal void EnsureCapacity(int requiredBytes)
    {
        RequireLiveOwner();
        if (requiredBytes <= 0) throw new ArgumentOutOfRangeException(nameof(requiredBytes));
        if (_handle != 0 && requiredBytes <= _byteLength) return;
        RequireUnrecordedPreparation();
        int limit = _renderer.MaximumAdvancedStorageBytes;
        if (requiredBytes > limit)
            throw new NotSupportedException("WebGPU.Advanced.StorageCapacity: a canonical arena exceeds the selected device storage binding limit.");
        int capacity = Math.Max(_byteLength, 16);
        while (capacity < requiredBytes)
            capacity = checked((int)Math.Min(limit, (long)capacity * 2));
        int replacement = _renderer.CreateBuffer(new BrowserBufferDescription(capacity, _usage, _label));
        int previous = _handle;
        if (previous != 0)
            _renderer.ReleaseEngineStorageGeneration(this, previous);
        SetField(ref _handle, replacement, publishNotifications: false);
        SetField(ref _byteLength, capacity, publishNotifications: false);
        if (previous != 0) _renderer.RetireEngineResourceAfterFrame(previous);
    }

    internal void StageUpload(ReadOnlySpan<byte> bytes, int destinationOffset = 0)
    {
        RequireLiveOwner();
        if (_handle == 0 || destinationOffset < 0 || bytes.Length > _byteLength - destinationOffset)
            throw new InvalidOperationException("WebGPU.Advanced.StorageRange: an upload exceeds its retained buffer generation.");
        _renderer.StageEngineStorageUpload(_handle, destinationOffset, bytes);
    }

    /// <summary>Prepares a complete unexposed slot even when its image exceeds a frame's upload arena.</summary>
    internal void UploadPreparation(ReadOnlySpan<byte> bytes, int destinationOffset = 0)
    {
        RequireLiveOwner();
        RequireUnrecordedPreparation();
        if (_handle == 0 || destinationOffset < 0 || bytes.Length > _byteLength - destinationOffset)
            throw new InvalidOperationException("WebGPU.Advanced.PreparationRange: the candidate arena exceeds its storage generation.");
        const int chunkBytes = 64 * 1024 * 1024;
        for (int offset = 0; offset < bytes.Length; offset += chunkBytes)
        {
            ReadOnlySpan<byte> chunk = bytes.Slice(offset, Math.Min(chunkBytes, bytes.Length - offset));
            _renderer.CountAdvancedPreparationUpload(chunk.Length);
            _renderer.WriteBuffer(_handle, checked(destinationOffset + offset), chunk);
        }
    }

    internal void MarkRecorded()
        => SetField(ref _recordedFrameSequence, _renderer.EngineFrameSequence, publishNotifications: false);

    private void RequireUnrecordedPreparation()
    {
        if (_renderer.IsRecordingEngineFrame && _recordedFrameSequence == _renderer.EngineFrameSequence)
            throw new InvalidOperationException("WebGPU.Advanced.RecordedStorage: immediate writes or capacity replacement cannot change an earlier unsubmitted command's storage.");
    }

    private void RequireLiveOwner()
    {
        ObjectDisposedException.ThrowIf(IsRetired, this);
        ValidateOwnerGeneration();
    }

    public override void Destroy()
    {
        if (_handle == 0) return;
        _renderer.ReleaseEngineDrawDependencies(this);
        if (_renderer.State != BrowserRendererState.Disposed)
            _renderer.RetireEngineResourceAfterFrame(_handle);
        SetField(ref _handle, 0, publishNotifications: false);
        SetField(ref _byteLength, 0, publishNotifications: false);
        SetField(ref _recordedFrameSequence, 0u, publishNotifications: false);
    }
}
