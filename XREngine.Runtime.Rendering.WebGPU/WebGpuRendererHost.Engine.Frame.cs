using System.Buffers.Binary;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private const int EngineFrameHeaderBytes = 48;
    private const int EngineFrameRecordBytes = 80;
    private const int EngineFrameMaximumRecords = 4097;
    private const int EngineUniformCapacity = 4 * 1024 * 1024;
    private const int EngineStorageCapacity = 8 * 1024 * 1024;
    private const int EngineMaximumUploads = 4096;
    private const int EngineUploadRecordBytes = 24;
    private readonly byte[] _engineCommandArena = new byte[EngineFrameHeaderBytes + EngineFrameMaximumRecords * EngineFrameRecordBytes + EngineMaximumUploads * EngineUploadRecordBytes];
    private readonly byte[] _engineUploadArena = new byte[EngineMaximumUploads * EngineUploadRecordBytes];
    private readonly byte[] _engineStorageArena = new byte[EngineStorageCapacity];
    private readonly List<WebGpuDataBuffer> _enginePendingStorage = new(8);
    private readonly List<int> _engineDeferredReleases = new(EngineFrameMaximumRecords);
    private byte[]? _engineUniformArena;
    private int _engineUniformBuffer;
    private int _engineUniformBytes;
    private int _engineUniformAlignment;
    private int _engineCommandCount;
    private int _engineUploadCount;
    private int _engineStorageBytes;
    private uint _engineFrameSequence;
    private bool _engineRecording;
    private XRViewport? _engineViewport;
    private bool _engineDrawPending;
    private int _engineMeshDrawCount;
    private WebGpuMeshResolutionTrace[]? _engineMeshResolutionTraces;
    private bool _engineMeshResolutionTraceEnabled;
    private int _engineMeshResolutionTraceCount;
    private bool _engineMeshResolutionTraceTruncated;
    private IShaderProgramArtifactResolver? _shaderArtifacts;
    private EngineMaterialVariantCatalog? _materialVariants;

    /// <summary>Session-scoped cooked shader identities loaded through the engine asset source.</summary>
    public IShaderProgramArtifactResolver? ShaderArtifacts => _shaderArtifacts;
    internal EngineMaterialVariantCatalog? MaterialVariants => _materialVariants;
    public int LastEngineMeshDrawCount => _engineMeshDrawCount;
    public int LastEngineMeshResolutionTraceCount => _engineMeshResolutionTraceCount;
    public bool LastEngineMeshResolutionTraceTruncated => _engineMeshResolutionTraceTruncated;
    public bool EngineMeshResolutionTraceEnabled => _engineMeshResolutionTraceEnabled;
    public WebGpuMeshResolutionTrace GetEngineMeshResolutionTrace(int index)
        => _engineMeshResolutionTraces is { } traces && (uint)index < (uint)_engineMeshResolutionTraceCount
            ? traces[index]
            : throw new ArgumentOutOfRangeException(nameof(index));
    internal uint EngineFrameSequence => _engineFrameSequence;
    internal bool IsRecordingEngineFrame => _engineRecording;
    internal string? BoundEngineFrameBufferName => _boundEngineFrameBuffer?.Data.Name;

    /// <summary>Enables bounded material-resolution snapshots for explicit diagnostics only.</summary>
    public void ConfigureEngineMeshResolutionTrace(bool enabled)
    {
        ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);
        if (_engineRecording)
            throw new InvalidOperationException("WebGPU.Trace.ActiveFrame: change trace capture between frames.");
        if (enabled && _engineMeshResolutionTraces is null)
            SetField(ref _engineMeshResolutionTraces, new WebGpuMeshResolutionTrace[64], publishNotifications: false);
        if (!enabled && _engineMeshResolutionTraces is { } traces)
            Array.Clear(traces);
        SetField(ref _engineMeshResolutionTraceEnabled, enabled, publishNotifications: false);
        SetField(ref _engineMeshResolutionTraceCount, 0, publishNotifications: false);
        SetField(ref _engineMeshResolutionTraceTruncated, false, publishNotifications: false);
    }

    /// <summary>Installs the immutable shader catalog before engine program preparation begins.</summary>
    public void BindShaderArtifacts(IShaderProgramArtifactResolver? artifacts, EngineMaterialVariantCatalog? materialVariants = null)
    {
        ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);
        if (_engineRecording || RenderObjectCache.Count != 0)
            throw new InvalidOperationException("WebGPU.Shaders.AlreadyActive: bind the shader catalog before creating engine API objects.");
        SetField(ref _shaderArtifacts, artifacts);
        SetField(ref _materialVariants, materialVariants);
    }

    /// <summary>Binds a host-owned engine viewport without acquiring desktop window services.</summary>
    public void BindEngineViewport(XRViewport? viewport)
    {
        ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);
        if (_engineRecording)
            throw new InvalidOperationException("WebGPU.Frame.Active: the engine viewport cannot change during recording.");
        SetField(ref _engineViewport, viewport);
    }

    /// <summary>Updates the bound engine viewport in its renderer's owner scope after the host changes the canvas surface.</summary>
    public void SynchronizeEngineViewport(bool invalidateResources)
    {
        ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);
        if (_engineRecording)
            throw new InvalidOperationException("WebGPU.Frame.Active: canvas dimensions cannot change during recording.");
        XRViewport viewport = _engineViewport
            ?? throw new InvalidOperationException("WebGPU.Viewport.Required: bind the engine viewport before synchronizing its surface.");
        using ThreadCurrentScope scope = EnterThreadCurrentScope(this);
        if (invalidateResources)
            viewport.RenderPipelineInstance.InvalidatePhysicalResources();
        if (_target.Surface.CanRender)
            viewport.Resize(checked((uint)_target.Surface.PhysicalWidth), checked((uint)_target.Surface.PhysicalHeight));
    }

    internal int EnsureEngineUniformBuffer()
    {
        RequireReady();
        if (_engineUniformBuffer != 0)
            return _engineUniformBuffer;
        if (DeviceCapabilities is not { } capabilities ||
            !capabilities.Limits.TryGetValue("minUniformBufferOffsetAlignment", out long alignment) ||
            alignment <= 0 || alignment > EngineUniformCapacity)
            throw new InvalidOperationException("WebGPU.UniformAlignment.Unavailable: the selected device did not publish a usable uniform alignment.");
        int handle = CreateBuffer(new BrowserBufferDescription(EngineUniformCapacity,
            BrowserBufferUsage.Uniform | BrowserBufferUsage.CopyDestination, "Engine frame uniforms"));
        SetField(ref _engineUniformBuffer, handle);
        SetField(ref _engineUniformAlignment, checked((int)alignment));
        SetField(ref _engineUniformArena, new byte[EngineUniformCapacity]);
        return handle;
    }

    private void BeginEngineFrame()
    {
        if (_engineRecording)
            throw new InvalidOperationException("WebGPU.Frame.Reentrant: an engine frame is already recording.");
        if (_engineFrameSequence == uint.MaxValue)
            throw new InvalidOperationException("WebGPU.Frame.SequenceExhausted: restart the canvas renderer.");
        SetField(ref _engineFrameSequence, _engineFrameSequence + 1, publishNotifications: false);
        SetField(ref _engineCommandCount, 0, publishNotifications: false);
        SetField(ref _engineUniformBytes, 0, publishNotifications: false);
        SetField(ref _engineUploadCount, 0, publishNotifications: false);
        SetField(ref _engineStorageBytes, 0, publishNotifications: false);
        SetField(ref _engineDrawPending, false, publishNotifications: false);
        SetField(ref _engineMeshDrawCount, 0, publishNotifications: false);
        if (_engineMeshResolutionTraceEnabled)
        {
            SetField(ref _engineMeshResolutionTraceCount, 0, publishNotifications: false);
            SetField(ref _engineMeshResolutionTraceTruncated, false, publishNotifications: false);
        }
        SetField(ref _submittedFrame, false, publishNotifications: false);
        SetField(ref _engineRecording, true, publishNotifications: false);
    }

    internal void MarkEngineDrawPending()
        => SetField(ref _engineDrawPending, true, publishNotifications: false);

    internal void CountEngineMeshDraw()
        => SetField(ref _engineMeshDrawCount, _engineMeshDrawCount + 1, publishNotifications: false);

    internal int RecordEngineMeshResolution(in WebGpuMeshResolutionTrace trace)
    {
        if (!_engineMeshResolutionTraceEnabled || _engineMeshResolutionTraces is not { } traces)
            return -1;
        if (_engineMeshResolutionTraceCount == traces.Length)
        {
            SetField(ref _engineMeshResolutionTraceTruncated, true, publishNotifications: false);
            return -1;
        }
        int index = _engineMeshResolutionTraceCount;
        traces[index] = trace;
        SetField(ref _engineMeshResolutionTraceCount, _engineMeshResolutionTraceCount + 1, publishNotifications: false);
        return index;
    }

    internal void UpdateEngineMeshResolutionStage(int index, string stage)
    {
        if (_engineMeshResolutionTraces is { } traces && (uint)index < (uint)_engineMeshResolutionTraceCount)
            traces[index] = traces[index] with { Stage = stage };
    }

    internal void UpdateEngineMeshResolutionFailure(int index, Exception error)
    {
        if (_engineMeshResolutionTraces is { } traces && (uint)index < (uint)_engineMeshResolutionTraceCount)
            traces[index] = traces[index] with
            {
                Stage = "Faulted",
                FailureType = error.GetType().FullName,
                FailureMessage = error.Message,
            };
    }

    internal uint SnapshotEngineUniforms(ReadOnlySpan<byte> data)
    {
        if (!_engineRecording || _engineUniformArena is null || _engineUniformAlignment == 0)
            throw new InvalidOperationException("WebGPU.Frame.UniformsUnavailable: prepare the uniform arena before recording a draw.");
        int offset = checked(((_engineUniformBytes + _engineUniformAlignment - 1) / _engineUniformAlignment) * _engineUniformAlignment);
        if (data.Length <= 0 || (data.Length & 3) != 0 || offset > EngineUniformCapacity - data.Length)
            throw new InvalidOperationException("WebGPU.Frame.UniformCapacity: uniform snapshots exceed the bounded frame arena.");
        data.CopyTo(_engineUniformArena.AsSpan(offset, data.Length));
        SetField(ref _engineUniformBytes, offset + data.Length, publishNotifications: false);
        return (uint)offset;
    }

    internal void RecordEngineCommands(int handle, ReadOnlySpan<uint> dynamicOffsets, uint? instanceCount = null)
    {
        if (!_engineRecording || !_resources.Contains(handle))
            throw new InvalidOperationException("WebGPU.Frame.CommandOwner: recording requires a command owned by the active renderer.");
        if (_engineCommandCount == EngineFrameMaximumRecords || dynamicOffsets.Length > 16)
            throw new InvalidOperationException("WebGPU.Frame.CommandCapacity: the frame exceeds its bounded command or binding capacity.");
        Span<byte> record = _engineCommandArena.AsSpan(
            EngineFrameHeaderBytes + _engineCommandCount * EngineFrameRecordBytes, EngineFrameRecordBytes);
        record.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(record, handle);
        BinaryPrimitives.WriteInt32LittleEndian(record[4..], dynamicOffsets.Length);
        for (int i = 0; i < dynamicOffsets.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(record[(8 + i * 4)..], dynamicOffsets[i]);
        if (instanceCount.HasValue)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(record[72..], instanceCount.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(record[76..], 1);
        }
        SetField(ref _engineCommandCount, _engineCommandCount + 1, publishNotifications: false);
    }

    internal void RegisterPendingStorage(WebGpuDataBuffer buffer)
    {
        if (!_enginePendingStorage.Contains(buffer))
            _enginePendingStorage.Add(buffer);
    }

    internal void StageEngineStorageUpload(int handle, int destinationOffset, ReadOnlySpan<byte> bytes)
    {
        if (!_engineRecording || !_resources.Contains(handle))
            throw new InvalidOperationException("WebGPU.Frame.StorageOwner: storage uploads require an active owned frame.");
        if (bytes.IsEmpty || (destinationOffset | bytes.Length) % 4 != 0 ||
            _engineUploadCount == EngineMaximumUploads || bytes.Length > EngineStorageCapacity - _engineStorageBytes)
            throw new InvalidOperationException("WebGPU.Frame.StorageCapacity: the aligned storage upload exceeds the bounded frame arena.");
        bytes.CopyTo(_engineStorageArena.AsSpan(_engineStorageBytes, bytes.Length));
        Span<byte> upload = _engineUploadArena.AsSpan(_engineUploadCount * EngineUploadRecordBytes, EngineUploadRecordBytes);
        upload.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(upload, handle);
        BinaryPrimitives.WriteInt32LittleEndian(upload[4..], destinationOffset);
        BinaryPrimitives.WriteInt32LittleEndian(upload[8..], _engineStorageBytes);
        BinaryPrimitives.WriteInt32LittleEndian(upload[12..], bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(upload[16..], _engineCommandCount);
        SetField(ref _engineStorageBytes, _engineStorageBytes + bytes.Length, publishNotifications: false);
        SetField(ref _engineUploadCount, _engineUploadCount + 1, publishNotifications: false);
    }

    private void SubmitEngineFrame(in RenderFrameOutputDescription output)
    {
        int uploadStart = EngineFrameHeaderBytes + _engineCommandCount * EngineFrameRecordBytes;
        int uploadLength = _engineUploadCount * EngineUploadRecordBytes;
        int length = uploadStart + uploadLength;
        _engineUploadArena.AsSpan(0, uploadLength).CopyTo(_engineCommandArena.AsSpan(uploadStart, uploadLength));
        Span<byte> header = _engineCommandArena.AsSpan(0, EngineFrameHeaderBytes);
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x45475258);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], length);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], _engineCommandCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], _session);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], checked((uint)output.TargetGeneration));
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], output.Properties.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], output.Properties.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header[32..], _engineUniformBuffer);
        BinaryPrimitives.WriteInt32LittleEndian(header[36..], _engineUniformBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], _engineFrameSequence);
        BinaryPrimitives.WriteInt32LittleEndian(header[44..], _engineUploadCount);
        Span<byte> uniforms = _engineUniformArena is null ? Span<byte>.Empty : _engineUniformArena.AsSpan(0, _engineUniformBytes);
        bool presented = WebGpuImports.SubmitEngineFrame(_session, _engineCommandArena.AsSpan(0, length), uniforms,
            _engineStorageArena.AsSpan(0, _engineStorageBytes));
        for (int index = _enginePendingStorage.Count - 1; index >= 0; index--)
        {
            if (_enginePendingStorage[index].AcceptSubmittedUploads())
                _enginePendingStorage.RemoveAt(index);
        }
        CommitDirectionalShadowDefaults();
        SetField(ref _submittedFrame, presented && !_engineDrawPending);
    }
}
