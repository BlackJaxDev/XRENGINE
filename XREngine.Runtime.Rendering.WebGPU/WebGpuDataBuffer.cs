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

    public int ResourceHandle => _handle;
    public override bool IsGenerated => _handle != 0;
    public ulong BackendAllocatedByteSize => (ulong)_allocatedBytes;
    public ulong BackendUploadedByteCount => _uploadedBytes;
    public bool BackendHasPendingUpload => !IsGenerated;
    public bool BackendIsReadyForGpuUse => IsGenerated && _allocatedBytes >= Data.Length;
    public bool BackendIsPersistentlyMapped => false;
    public XRBufferResolvedRoute BackendResolvedRoute => XRBufferResolvedRoute.StagingUpload;

    public override nint GetHandle() => _handle;

    public override void Generate()
    {
        ValidateOwnerGeneration();
        if (IsGenerated && _allocatedBytes >= Data.Length)
            return;
        if (IsGenerated)
            throw Unsupported("Resize", "release dependent bindings and regenerate the buffer at a resource-generation boundary");

        int length = checked((int)Data.Length);
        int allocationLength = checked((Math.Max(length, 1) + 3) & ~3);
        int handle = Renderer.CreateBuffer(new BrowserBufferDescription(
            allocationLength, ResolveUsage(Data.Target), Data.Name ?? Data.AttributeName));
        SetField(ref _handle, handle);
        SetField(ref _allocatedBytes, allocationLength);
        try
        {
            if (!Data.GpuProduced && length != 0)
                Upload(0, length);
            ReportState();
        }
        catch
        {
            Destroy();
            throw;
        }
    }

    public override void Destroy()
    {
        if (_handle == 0)
            return;
        Renderer.ReleaseEngineDrawDependencies(this);
        if (Renderer.State != BrowserRendererState.Disposed)
            Renderer.RetireEngineResource(_handle);
        SetField(ref _handle, 0);
        SetField(ref _allocatedBytes, 0);
        SetField(ref _uploadedBytes, 0u);
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
            throw Unsupported(nameof(PushData), "the new size exceeds the published allocation");
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
        Upload(offset, checked((int)length));
        ReportState();
    }

    private void Upload(int offset, int length)
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
        => throw Unsupported(nameof(BindSSBO), "engine program binding layouts are not available");

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
            EBufferTarget.ShaderStorageBuffer => BrowserBufferUsage.Storage,
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
