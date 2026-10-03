using System.Buffers.Binary;
using XREngine.Data.Geometry;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private const int EngineFrameHeaderBytes = 48;
    private const int EngineFrameRecordBytes = 112;
    private const int EngineFrameMaximumRecords = 4097;
    private const int EngineUniformCapacity = 4 * 1024 * 1024;
    private readonly byte[] _engineCommandArena = new byte[EngineFrameHeaderBytes + EngineFrameMaximumRecords * EngineFrameRecordBytes + EngineMaximumPacketUploads * EngineUploadRecordBytes];
    private readonly List<IWebGpuProducedTexture> _engineProducedTextures = new(16);
    private readonly List<int> _engineDeferredReleases = new(EngineFrameMaximumRecords);
    private byte[]? _engineUniformArena;
    private int _engineUniformBuffer;
    private int _engineUniformBytes;
    private int _engineUniformAlignment;
    private int _engineCommandCount;
    private uint _engineFrameSequence;
    private bool _engineRecording;
    private XRViewport? _engineViewport;
    private bool _engineDrawPending;
    private int _engineMeshDrawCount;
    private ulong _submittedEngineSurfaceGeneration;
    private RenderTargetOutputProperties _submittedEngineOutputProperties;
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
    /// <summary>Reports the last recording attempt without allocating a statistics snapshot.</summary>
    public int LastEngineCommandCount => _engineCommandCount;
    /// <summary>Reports whether a producer deferred the last recording attempt.</summary>
    public bool HasPendingEngineDraw => _engineDrawPending;
    /// <summary>Reports a complete engine submission for the current drawable surface, not an earlier resize generation.</summary>
    public bool IsEngineOutputFrameReady => _submittedFrame &&
        TryDescribeFrameOutput(out RenderFrameOutputDescription output) &&
        _submittedEngineSurfaceGeneration == output.TargetGeneration &&
        _submittedEngineOutputProperties == output.Properties;
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
        DiscardEngineViewHistory();
        SetField(ref _engineViewport, viewport);
    }

    /// <summary>Updates the bound engine viewport in its renderer's owner scope after the host changes the canvas surface.</summary>
    public void SynchronizeEngineViewport(bool invalidateResources)
    {
        ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);
        if (_engineRecording)
            throw new InvalidOperationException("WebGPU.Frame.Active: canvas dimensions cannot change during recording.");
        DiscardEngineViewHistory();
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
        DiscardEngineViewHistory();
        ReclaimAdvancedSceneSlots();
        ReclaimAuthoredIndexedSlots();
        SetField(ref _engineFrameSequence, _engineFrameSequence + 1, publishNotifications: false);
        SetField(ref _engineCommandCount, 0, publishNotifications: false);
        SetField(ref _engineUniformBytes, 0, publishNotifications: false);
        BeginEngineBufferUploads();
        SetField(ref _engineDrawPending, false, publishNotifications: false);
        _engineProducedTextures.Clear();
        ResetAuthorizedShadowReuse();
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

    internal void RecordEngineCommands(int handle, ReadOnlySpan<uint> dynamicOffsets, uint? instanceCount = null,
        BoundingRectangle? viewport = null, BoundingRectangle? scissor = null)
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
        uint flags = 0;
        if (instanceCount.HasValue)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(record[72..], instanceCount.Value);
            flags |= 1;
        }
        if (viewport is { } viewportRegion)
        {
            WriteEngineDrawRectangle(record[80..], viewportRegion, allowEmpty: false);
            flags |= 2;
        }
        if (scissor is { } scissorRegion)
        {
            WriteEngineDrawRectangle(record[96..], scissorRegion, allowEmpty: true);
            flags |= 4;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(record[76..], flags);
        SetField(ref _engineCommandCount, _engineCommandCount + 1, publishNotifications: false);
    }

    private static void WriteEngineDrawRectangle(Span<byte> destination, BoundingRectangle region, bool allowEmpty)
    {
        if (region.X < 0 || region.Y < 0 || region.Width < (allowEmpty ? 0 : 1) ||
            region.Height < (allowEmpty ? 0 : 1))
            throw new ArgumentOutOfRangeException(nameof(region), "An engine draw rectangle requires nonnegative top-left coordinates and extents.");
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)region.X);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], (uint)region.Y);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)region.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], (uint)region.Height);
    }

    internal void RegisterProducedTexture(IWebGpuProducedTexture texture)
    {
        if (!_engineProducedTextures.Contains(texture))
            _engineProducedTextures.Add(texture);
    }

    private void SubmitEngineFrame(in RenderFrameOutputDescription output, ref bool submitted)
    {
        int uploadStart = EngineFrameHeaderBytes + _engineCommandCount * EngineFrameRecordBytes;
        int uploadLength = _engineUploadCount * EngineUploadRecordBytes;
        int length = uploadStart + uploadLength;
        _engineUploadArena.AsSpan(0, uploadLength).CopyTo(_engineCommandArena.AsSpan(uploadStart, uploadLength));
        Span<byte> header = _engineCommandArena.AsSpan(0, EngineFrameHeaderBytes);
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x45475258);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 4);
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
        CountEngineFrameSubmission(length, uniforms.Length, _engineStorageBytes);
        bool presented = WebGpuImports.SubmitEngineFrame(_session, _engineCommandArena.AsSpan(0, length), uniforms,
            _engineStorageArena.AsSpan(0, _engineStorageBytes));
        // Queue ownership survives any failure in subsequent managed bookkeeping.
        submitted = true;
        SetField(ref _engineUploadsSubmitted, true, publishNotifications: false);
        try
        {
            // Once the queue owns the bytes, no later receipt or notification
            // failure may leave a buffer waiting for an already accepted snapshot.
            AcceptSubmittedBufferUploads();
            // The complete-frame gate has passed. Canvas presentation alone is not a
            // color-write receipt: settle only the exact authored and attested views.
            // These CPU metadata slots do not wait for the GPU completion watermark.
            SettleSubmittedEngineViewHistory(presented, in output);
        }
        finally
        {
            // The executor returning confirms queue submission even when no canvas
            // pass was present. Seal GPU ownership even if metadata settlement fails.
            try { EndAdvancedSceneRecording(submitted: true); }
            finally { EndAuthoredIndexedRecording(submitted: true); }
        }
        CountEngineFrameSubmissionResult(presented);
        CommitDirectionalShadowDefaults();
        if (presented && !_engineDrawPending)
        {
            for (int i = 0; i < _engineProducedTextures.Count; i++)
                _engineProducedTextures[i].CommitProducedFrame(_engineFrameSequence);
            SetField(ref _submittedEngineSurfaceGeneration, output.TargetGeneration, publishNotifications: false);
            SetField(ref _submittedEngineOutputProperties, output.Properties, publishNotifications: false);
        }
        SetField(ref _submittedFrame, presented && !_engineDrawPending, publishNotifications: false);
    }
}
