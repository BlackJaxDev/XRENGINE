using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

/// <summary>Device-local engine buffer with explicit, borrowed CPU uploads and asynchronous readback.</summary>
public sealed unsafe class WebGpuDataBuffer(WebGpuRendererHost renderer, XRDataBuffer data)
    : WebGpuObject<XRDataBuffer>(renderer, data), IApiDataBuffer
{
    private const int MaximumUploadBytes = 64 * 1024 * 1024;
    private int _handle;
    private int _allocatedBytes;
    private uint _uploadedBytes;
    private int _pendingStart = int.MaxValue;
    private int _pendingEnd;
    private uint _stagedFrameSequence;
    private readonly List<int> _supersededHandles = new(2);

    public int ResourceHandle => _handle;
    public override bool IsGenerated => _handle != 0;
    public ulong BackendAllocatedByteSize => (ulong)_allocatedBytes;
    public ulong BackendUploadedByteCount => _uploadedBytes;
    public bool BackendHasPendingUpload => !IsGenerated || _pendingStart != int.MaxValue;
    public bool BackendIsReadyForGpuUse => IsGenerated && _allocatedBytes >= Data.Length;
    public bool BackendIsPersistentlyMapped => false;
    public XRBufferResolvedRoute BackendResolvedRoute => XRBufferResolvedRoute.StagingUpload;

    public override nint GetHandle() => _handle;

    public override void Generate()
    {
        ValidateOwnerGeneration();
        if (IsGenerated && _allocatedBytes >= Data.Length)
            return;
        if (IsGenerated && ((ResolveUsage(Data.Target) & BrowserBufferUsage.Storage) == 0 || Data.GpuProduced))
            throw Unsupported("Resize", "only CPU-backed storage-capable buffers may grow while dependent bindings exist");
        if (IsGenerated && Renderer.IsRecordingEngineFrame && _supersededHandles.Count >= 32)
            throw Unsupported("Resize", "too many uncommitted storage generations are awaiting an accepted frame");

        int length = checked((int)Data.Length);
        int allocationLength = checked((Math.Max(length, 1) + 3) & ~3);
        if (Data.Target == EBufferTarget.ShaderStorageBuffer && allocationLength > 8 * 1024 * 1024)
            throw Unsupported("Resize", "the storage profile exceeds the bounded 8 MiB frame upload capacity");
        int oldHandle = _handle, oldAllocated = _allocatedBytes;
        int oldPendingStart = _pendingStart, oldPendingEnd = _pendingEnd;
        uint oldUploadedBytes = _uploadedBytes;
        int handle = Renderer.CreateBuffer(new BrowserBufferDescription(
            allocationLength, ResolveUsage(Data.Target), Data.Name ?? Data.AttributeName));
        SetField(ref _handle, handle);
        SetField(ref _allocatedBytes, allocationLength);
        try
        {
            if (!Data.GpuProduced && length != 0)
            {
                // This fresh physical candidate cannot occur in an earlier retained
                // command. Prepare its complete image in bounded queue-write chunks;
                // ordinary mutations still use copies at their ordered frame boundary.
                Upload(0, length, candidateAllocation: true);
                SetField(ref _pendingStart, int.MaxValue, publishNotifications: false);
                SetField(ref _pendingEnd, 0, publishNotifications: false);
            }
            ReportState();
        }
        catch
        {
            SetField(ref _handle, oldHandle);
            SetField(ref _allocatedBytes, oldAllocated);
            SetField(ref _pendingStart, oldPendingStart, publishNotifications: false);
            SetField(ref _pendingEnd, oldPendingEnd, publishNotifications: false);
            SetField(ref _uploadedBytes, oldUploadedBytes, publishNotifications: false);
            Renderer.RetireEngineResourceAfterFrame(handle);
            throw;
        }
        if (oldHandle != 0)
        {
            // Later commands must bind the newly published physical storage. Earlier
            // packet records retain the old handle until their frame is submitted.
            Renderer.ReleaseEngineStorageGeneration(this, oldHandle);
            if (Renderer.IsRecordingEngineFrame)
                _supersededHandles.Add(oldHandle);
            else
            {
                Renderer.RetireEngineResourceAfterFrame(oldHandle);
            }
        }
    }

    public override void Destroy()
    {
        if (_handle == 0)
            return;
        Renderer.ReleaseEngineDrawDependencies(this);
        if (Renderer.State != BrowserRendererState.Disposed)
        {
            Renderer.RetireEngineResourceAfterFrame(_handle);
            foreach (int handle in _supersededHandles)
                Renderer.RetireEngineResourceAfterFrame(handle);
        }
        _supersededHandles.Clear();
        SetField(ref _handle, 0);
        SetField(ref _allocatedBytes, 0);
        SetField(ref _uploadedBytes, 0u);
        SetField(ref _pendingStart, int.MaxValue);
        SetField(ref _pendingEnd, 0);
        ReportState();
    }

    public void PushData()
    {
        ValidateOwnerGeneration();
        if (!IsGenerated)
        {
            Generate();
            if (!Data.GpuProduced)
                return;
        }
        if (Data.Length > _allocatedBytes)
        {
            Generate();
            return;
        }
        Upload(0, checked((int)Data.Length));
        ReportState();
    }

    public void PushSubData() => PushData();

    public void PushSubData(int offset, uint length)
    {
        ValidateOwnerGeneration();
        if (offset < 0 || (ulong)offset + length > Data.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (!IsGenerated)
        {
            Generate();
            if (!Data.GpuProduced)
                return;
        }
        if (Data.Length > _allocatedBytes)
        {
            Generate();
            return;
        }
        Upload(offset, checked((int)length));
        ReportState();
    }

    private void Upload(int offset, int length, bool candidateAllocation = false)
    {
        if (length == 0)
            return;
        if (Data.ClientSideSource is not { } source || source.Length < Data.Length ||
            !Data.TryGetAddress(out VoidPtr address) || address == VoidPtr.Zero)
            throw Unsupported("Upload", "the complete engine CPU buffer source is unavailable");
        if (Data.GpuProduced && ((offset | length) & 3) != 0)
            throw Unsupported("Upload", "unaligned writes cannot replace adjacent GPU-produced bytes");

        int start = offset & ~3;
        int end = checked(offset + length);
        int alignedEnd = end & ~3;
        if (!candidateAllocation && (ResolveUsage(Data.Target) & BrowserBufferUsage.Storage) != 0 && Renderer.IsRecordingEngineFrame)
        {
            int uploadEnd = checked((end + 3) & ~3);
            SetField(ref _pendingStart, Math.Min(_pendingStart, start), publishNotifications: false);
            SetField(ref _pendingEnd, Math.Max(_pendingEnd, uploadEnd), publishNotifications: false);
            Renderer.RegisterPendingStorage(this);
            if (_stagedFrameSequence == Renderer.EngineFrameSequence)
                SnapshotStorageUpload(start, uploadEnd, (byte*)address.Pointer);
            else
                SnapshotStorageUpload(_pendingStart, Math.Min(_pendingEnd, checked((checked((int)Data.Length) + 3) & ~3)),
                    (byte*)address.Pointer);
            SetField(ref _stagedFrameSequence, Renderer.EngineFrameSequence, publishNotifications: false);
            SetField(ref _uploadedBytes, Math.Max(_uploadedBytes, (uint)end), publishNotifications: false);
            return;
        }
        while (start < alignedEnd)
        {
            int bytes = Math.Min(alignedEnd - start, MaximumUploadBytes);
            // The import copies this borrowed native span synchronously; no pointer or
            // view survives the call. Padding never reads beyond the engine source.
            Renderer.WriteBuffer(_handle, start, new Span<byte>((byte*)address.Pointer + start, bytes));
            start += bytes;
        }
        if (end != alignedEnd)
        {
            Span<byte> tail = stackalloc byte[4];
            tail.Clear();
            int available = Math.Min(4, checked((int)Data.Length) - alignedEnd);
            new ReadOnlySpan<byte>((byte*)address.Pointer + alignedEnd, available).CopyTo(tail);
            Renderer.WriteBuffer(_handle, alignedEnd, tail);
        }
        SetField(ref _uploadedBytes, Math.Max(_uploadedBytes, (uint)end), publishNotifications: false);
    }

    private void SnapshotStorageUpload(int start, int end, byte* address)
    {
        int sourceLength = checked((int)Data.Length);
        int available = Math.Min(end, sourceLength) - start;
        if (available == end - start)
        {
            Renderer.StageEngineStorageUpload(_handle, start, new ReadOnlySpan<byte>(address + start, available));
            return;
        }
        // Only the final three bytes can be outside the CPU allocation.
        int aligned = available & ~3;
        if (aligned > 0)
            Renderer.StageEngineStorageUpload(_handle, start, new ReadOnlySpan<byte>(address + start, aligned));
        Span<byte> tail = stackalloc byte[4];
        tail.Clear();
        new ReadOnlySpan<byte>(address + start + aligned, available - aligned).CopyTo(tail);
        Renderer.StageEngineStorageUpload(_handle, start + aligned, tail);
    }

    internal void StagePendingUpload()
    {
        if (_pendingStart == int.MaxValue || !IsGenerated || !Renderer.IsRecordingEngineFrame ||
            _stagedFrameSequence == Renderer.EngineFrameSequence)
            return;
        if (!Data.TryGetAddress(out VoidPtr address) || address == VoidPtr.Zero ||
            Data.ClientSideSource is not { } source || source.Length < Data.Length)
            throw Unsupported("Upload", "the pending storage source is unavailable");
        int end = Math.Min(_pendingEnd, checked((checked((int)Data.Length) + 3) & ~3));
        if (_pendingStart >= end)
        {
            SetField(ref _pendingStart, int.MaxValue, publishNotifications: false);
            SetField(ref _pendingEnd, 0, publishNotifications: false);
            return;
        }
        SnapshotStorageUpload(_pendingStart, end, (byte*)address.Pointer);
        SetField(ref _stagedFrameSequence, Renderer.EngineFrameSequence, publishNotifications: false);
    }

    internal bool AcceptSubmittedUploads()
    {
        if (_pendingStart != int.MaxValue && _stagedFrameSequence != Renderer.EngineFrameSequence)
            return false;
        SetField(ref _pendingStart, int.MaxValue, publishNotifications: false);
        SetField(ref _pendingEnd, 0, publishNotifications: false);
        foreach (int handle in _supersededHandles)
        {
            Renderer.RetireEngineResourceAfterFrame(handle);
        }
        _supersededHandles.Clear();
        ReportState();
        return true;
    }

    public void EnsureStorageAllocatedForGpuUse() => Generate();

    public bool TryGetBindingId(out uint bindingId)
    {
        bindingId = (uint)_handle;
        return IsGenerated;
    }

    /// <summary>Queues a bounded readback without exposing a mapped GPU address.</summary>
    public Task<byte[]> ReadAsync(int offset, int length, CancellationToken cancellationToken = default)
    {
        ValidateOwnerGeneration();
        if (!IsGenerated || offset < 0 || length <= 0 || (long)offset + length > Data.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return Renderer.ReadBufferAsync(new BrowserBufferReadbackDescription(_handle, offset, length), cancellationToken);
    }

    public void MapBufferData() => throw MappingUnsupported();
    public void UnmapBufferData() => throw MappingUnsupported();
    public void Flush() => throw MappingUnsupported();
    public void FlushRange(int offset, uint length) => throw MappingUnsupported();
    public bool TryReadMapped(DataBufferMappedReadCallback callback) => throw MappingUnsupported();
    public bool TryWriteMapped(DataBufferMappedWriteCallback callback) => throw MappingUnsupported();
    public bool TryReadMapped<TState>(ref TState state, DataBufferMappedReadCallback<TState> callback)
        where TState : allows ref struct => throw MappingUnsupported();
    public bool TryWriteMapped<TState>(ref TState state, DataBufferMappedWriteCallback<TState> callback)
        where TState : allows ref struct => throw MappingUnsupported();

    public void SetUniformBlockName(XRRenderProgram program, string blockName)
        => throw Unsupported(nameof(SetUniformBlockName), "engine program binding layouts are not available");
    public void SetBlockIndex(uint blockIndex)
        => throw Unsupported(nameof(SetBlockIndex), "engine program binding layouts are not available");
    public void Bind() => throw Unsupported(nameof(Bind), "WebGPU buffers require an explicit program or vertex-layout binding");
    public void Unbind() => throw Unsupported(nameof(Unbind), "WebGPU buffers require an explicit program or vertex-layout binding");
    public void BindSSBO(XRRenderProgram program, uint? bindingIndexOverride = null)
    {
        if (bindingIndexOverride is not { } binding)
            throw Unsupported(nameof(BindSSBO), "a cooked compute buffer requires an explicit binding index");
        _ = Renderer.GetOrCreateAPIRenderObject(program);
        program.BindBuffer(Data, binding);
    }

    private void ReportState()
        => Data.ReportBackendUploadState(BackendAllocatedByteSize, BackendUploadedByteCount,
            BackendHasPendingUpload, BackendResolvedRoute, BackendIsReadyForGpuUse);

    private static BrowserBufferUsage ResolveUsage(EBufferTarget target)
    {
        BrowserBufferUsage usage = target switch
        {
            EBufferTarget.ArrayBuffer => BrowserBufferUsage.Vertex | BrowserBufferUsage.Storage,
            EBufferTarget.ElementArrayBuffer => BrowserBufferUsage.Index | BrowserBufferUsage.Storage,
            EBufferTarget.UniformBuffer => BrowserBufferUsage.Uniform,
            EBufferTarget.ShaderStorageBuffer or EBufferTarget.ParameterBuffer => BrowserBufferUsage.Storage,
            EBufferTarget.DrawIndirectBuffer or EBufferTarget.DispatchIndirectBuffer =>
                BrowserBufferUsage.Indirect | BrowserBufferUsage.Storage,
            EBufferTarget.CopyReadBuffer or EBufferTarget.CopyWriteBuffer => 0,
            _ => throw Unsupported("Create", $"buffer target '{target}' has no admitted WebGPU encoding"),
        };
        return usage | BrowserBufferUsage.CopySource | BrowserBufferUsage.CopyDestination;
    }

    private static NotSupportedException MappingUnsupported()
        => Unsupported("Map", "synchronous GPU mapping is unavailable; use explicit CPU uploads or ReadAsync");

    private static NotSupportedException Unsupported(string operation, string reason)
        => new($"WebGPU.Buffer.OperationUnsupported: {operation}: {reason}.");
}
