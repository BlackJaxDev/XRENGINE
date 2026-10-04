using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

/// <summary>Device-local engine buffer with explicit, borrowed CPU uploads and asynchronous readback.</summary>
public sealed unsafe class WebGpuDataBuffer(WebGpuRendererHost renderer, XRDataBuffer data)
    : WebGpuObject<XRDataBuffer>(renderer, data), IApiDataBuffer
{
    private int _handle;
    private WebGpuResourceRequest? _allocationRequest;
    private int _allocatedBytes;
    private uint _uploadedBytes;
    private int _pendingStart = int.MaxValue;
    private int _pendingEnd;
    private bool _initializationPending;
    private uint _replacementFrameSequence;
    private int _replacementCount;

    public int ResourceHandle => _handle;
    public override bool IsGenerated => _handle != 0 && _allocationRequest is null;
    public ulong BackendAllocatedByteSize => (ulong)_allocatedBytes;
    public ulong BackendUploadedByteCount => _uploadedBytes;
    public bool BackendHasPendingUpload => !IsGenerated || _initializationPending || _pendingStart != int.MaxValue;
    public bool BackendIsReadyForGpuUse => IsGenerated && _allocatedBytes >= Data.Length;
    public bool BackendIsPersistentlyMapped => false;
    public XRBufferResolvedRoute BackendResolvedRoute => XRBufferResolvedRoute.StagingUpload;

    public override nint GetHandle() => IsGenerated ? _handle : 0;

    public override void Generate()
    {
        ValidateOwnerGeneration();
        if (_handle != 0 && _allocatedBytes >= Data.Length && _allocationRequest is null)
            return;
        if (_handle != 0 && ((ResolveUsage(Data.Target) & BrowserBufferUsage.Storage) == 0 || Data.GpuProduced))
            throw Unsupported("Resize", "only CPU-backed storage-capable buffers may grow while dependent bindings exist");
        if (_handle != 0 && Renderer.IsRecordingEngineFrame && _replacementFrameSequence == Renderer.EngineFrameSequence && _replacementCount >= 32)
            throw Unsupported("Resize", "the active frame exceeds thirty-two storage generation replacements");

        int length = checked((int)Data.Length);
        int allocationLength = checked((Math.Max(length, 1) + 3) & ~3);
        if (Data.Target == EBufferTarget.ShaderStorageBuffer && allocationLength > 8 * 1024 * 1024)
            throw Unsupported("Resize", "the storage profile exceeds the bounded 8 MiB frame upload capacity");
        int oldHandle = _handle, oldAllocated = _allocatedBytes;
        int oldPendingStart = _pendingStart, oldPendingEnd = _pendingEnd;
        uint oldUploadedBytes = _uploadedBytes;
        bool oldInitializationPending = _initializationPending;
        BrowserBufferDescription descriptor = new(allocationLength, ResolveUsage(Data.Target), Data.Name ?? Data.AttributeName);
        if (_allocationRequest is { } obsolete && !obsolete.Descriptor.Equals(descriptor))
        {
            Renderer.CancelEngineResourceRequest(obsolete);
            _allocationRequest = null;
        }
        if (_allocationRequest is null)
        {
            VoidPtr snapshotAddress = VoidPtr.Zero;
            if (!Data.GpuProduced && length != 0)
            {
                if (Data.ClientSideSource is not { } source || source.Length < Data.Length ||
                    !Data.TryGetAddress(out VoidPtr address) || address == VoidPtr.Zero)
                    throw Unsupported("Upload", "the complete engine CPU buffer source is unavailable");
                snapshotAddress = address;
            }
            _allocationRequest = Renderer.RequestEngineResource(this, 1, descriptor);
            if (snapshotAddress != VoidPtr.Zero)
            {
                try { Renderer.CaptureEngineResourceUpload(_allocationRequest, new ReadOnlySpan<byte>(snapshotAddress.Pointer, length)); }
                catch { Renderer.CancelEngineResourceRequest(_allocationRequest); _allocationRequest = null; throw; }
            }
        }
        int handle = Renderer.RequireEngineResource(_allocationRequest, claim: false);
        SetField(ref _handle, handle, publishNotifications: false);
        SetField(ref _allocatedBytes, allocationLength, publishNotifications: false);
        try
        {
            if (_allocationRequest.Uploads is { Count: > 0 })
            {
                // The candidate image is owned until the shared acceptance import.
                // Subsequent mutations still retain their exact command boundary.
                Renderer.StageEngineResourceInitialUploads(_allocationRequest, handle);
                if (!Data.GpuProduced) SetField(ref _uploadedBytes, (uint)length, publishNotifications: false);
                SetField(ref _initializationPending, true, publishNotifications: false);
                Renderer.RegisterInitializingBuffer(this);
                SetField(ref _pendingStart, int.MaxValue, publishNotifications: false);
                SetField(ref _pendingEnd, 0, publishNotifications: false);
            }
            Renderer.ClaimEngineResource(_allocationRequest);
            _allocationRequest = null;
            ReportState();
        }
        catch
        {
            SetField(ref _handle, oldHandle);
            SetField(ref _allocatedBytes, oldAllocated);
            SetField(ref _pendingStart, oldPendingStart, publishNotifications: false);
            SetField(ref _pendingEnd, oldPendingEnd, publishNotifications: false);
            SetField(ref _uploadedBytes, oldUploadedBytes, publishNotifications: false);
            SetField(ref _initializationPending, oldInitializationPending, publishNotifications: false);
            Renderer.RetireEngineResourceAfterFrame(handle);
            _allocationRequest = null;
            throw;
        }
        if (oldHandle != 0)
        {
            // Later commands must bind the newly published physical storage. Earlier
            // packet records retain the old handle until their frame is submitted.
            Renderer.ReleaseEngineStorageGeneration(this, oldHandle);
            if (Renderer.IsRecordingEngineFrame)
            {
                SetField(ref _replacementCount, _replacementFrameSequence == Renderer.EngineFrameSequence ? _replacementCount + 1 : 1,
                    publishNotifications: false);
                SetField(ref _replacementFrameSequence, Renderer.EngineFrameSequence, publishNotifications: false);
            }
            Renderer.RetireEngineResourceAfterFrame(oldHandle);
        }
    }

    public override void Destroy()
    {
        Renderer.CancelEngineResourceRequests(this);
        _allocationRequest = null;
        if (_handle == 0)
            return;
        Renderer.ReleaseEngineDrawDependencies(this);
        if (Renderer.State != BrowserRendererState.Disposed)
            Renderer.RetireEngineResourceAfterFrame(_handle);
        Renderer.UnregisterPendingStorage(this);
        Renderer.UnregisterInitializingBuffer(this);
        SetField(ref _handle, 0);
        SetField(ref _allocatedBytes, 0);
        SetField(ref _uploadedBytes, 0u);
        SetField(ref _pendingStart, int.MaxValue);
        SetField(ref _pendingEnd, 0);
        SetField(ref _initializationPending, false, publishNotifications: false);
        ReportState();
    }

    public void PushData()
    {
        ValidateOwnerGeneration();
        if (!IsGenerated)
        {
            WebGpuResourceRequest? previous = _allocationRequest;
            RefreshPendingInitialImage();
            try { Generate(); }
            catch (RenderResourcePreparationPendingException)
            {
                if (!ReferenceEquals(previous, _allocationRequest)) RefreshPendingInitialImage();
                return;
            }
            if (!Data.GpuProduced)
                return;
        }
        if (Data.Length > _allocatedBytes)
        {
            try { Generate(); }
            catch (RenderResourcePreparationPendingException) { RefreshPendingInitialImage(); }
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
            WebGpuResourceRequest? previous = _allocationRequest;
            RefreshPendingInitialImage(offset, checked((int)length));
            try { Generate(); }
            catch (RenderResourcePreparationPendingException)
            {
                if (!ReferenceEquals(previous, _allocationRequest)) RefreshPendingInitialImage(offset, checked((int)length));
                return;
            }
            if (!Data.GpuProduced)
                return;
        }
        if (Data.Length > _allocatedBytes)
        {
            try { Generate(); }
            catch (RenderResourcePreparationPendingException) { RefreshPendingInitialImage(offset, checked((int)length)); }
            return;
        }
        Upload(offset, checked((int)length));
        ReportState();
    }

    private void RefreshPendingInitialImage(int offset = 0, int length = -1)
    {
        if (_allocationRequest is not { } request || Data.Length == 0) return;
        if (Data.ClientSideSource is not { } source || source.Length < Data.Length ||
            !Data.TryGetAddress(out VoidPtr address) || address == VoidPtr.Zero)
            throw Unsupported("Upload", "the complete engine CPU buffer source is unavailable");
        if (Data.GpuProduced)
        {
            if (length < 0) length = checked((int)Data.Length);
            if ((offset | length) % 4 != 0)
                throw Unsupported("Upload", "unaligned writes cannot replace adjacent GPU-produced bytes");
            if (length != 0) Renderer.CaptureEngineResourceUpload(request,
                new ReadOnlySpan<byte>((byte*)address.Pointer + offset, length), bufferOffset: offset);
            SetField(ref _uploadedBytes, Math.Max(_uploadedBytes, checked((uint)(offset + length))), publishNotifications: false);
        }
        else Renderer.ReplaceEngineResourceBufferImage(request, new ReadOnlySpan<byte>(address.Pointer, checked((int)Data.Length)));
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
        int uploadEnd = checked((end + 3) & ~3);
        if (!candidateAllocation)
        {
            SnapshotBufferUpload(start, uploadEnd, (byte*)address.Pointer);
            SetField(ref _pendingStart, Math.Min(_pendingStart, start), publishNotifications: false);
            SetField(ref _pendingEnd, Math.Max(_pendingEnd, uploadEnd), publishNotifications: false);
            Renderer.RegisterPendingStorage(this);
            SetField(ref _uploadedBytes, Math.Max(_uploadedBytes, (uint)end), publishNotifications: false);
            return;
        }
        SnapshotBufferUpload(start, uploadEnd, (byte*)address.Pointer, initialization: true);
        SetField(ref _uploadedBytes, Math.Max(_uploadedBytes, (uint)end), publishNotifications: false);
    }

    private void SnapshotBufferUpload(int start, int end, byte* address, bool initialization = false)
    {
        int sourceLength = checked((int)Data.Length);
        int available = Math.Min(end, sourceLength) - start;
        int records = available == end - start || available < 4 ? 1 : 2;
        if (initialization)
            Renderer.PrepareEngineBufferInitialization(_handle, start, end - start, records);
        else if (records > 1)
            Renderer.PrepareEngineBufferMutation(_handle, start, end - start, records);
        if (available == end - start)
        {
            StageSnapshot(start, new ReadOnlySpan<byte>(address + start, available), initialization);
            return;
        }
        // Only the final three bytes can be outside the CPU allocation.
        int aligned = available & ~3;
        if (aligned > 0)
            StageSnapshot(start, new ReadOnlySpan<byte>(address + start, aligned), initialization);
        Span<byte> tail = stackalloc byte[4];
        tail.Clear();
        new ReadOnlySpan<byte>(address + start + aligned, available - aligned).CopyTo(tail);
        StageSnapshot(start + aligned, tail, initialization);
    }

    private void StageSnapshot(int offset, ReadOnlySpan<byte> bytes, bool initialization)
    {
        if (initialization) Renderer.StageEngineBufferPreparation(_handle, offset, bytes);
        else Renderer.StageEngineBufferUpload(_handle, offset, bytes);
    }

    /// <summary>Checks pending publication without borrowing mutable CPU bytes again on a retry.</summary>
    internal void StagePendingUpload()
    {
        ValidateOwnerGeneration();
        if (_pendingStart != int.MaxValue && IsGenerated && Renderer.IsRecordingEngineFrame &&
            !Renderer.HasPendingEngineBufferUpload(_handle))
            throw Unsupported("Upload", "the pending buffer mutation has no owned immutable snapshot");
    }

    internal void AcceptSubmittedUploads()
    {
        SetField(ref _pendingStart, int.MaxValue, publishNotifications: false);
        SetField(ref _pendingEnd, 0, publishNotifications: false);
        ReportState();
    }

    internal void AcceptSubmittedInitialization()
    {
        SetField(ref _initializationPending, false, publishNotifications: false);
        ReportState();
    }

    public void EnsureStorageAllocatedForGpuUse() => Generate();

    public bool TryGetBindingId(out uint bindingId)
    {
        bindingId = IsGenerated ? (uint)_handle : 0;
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
