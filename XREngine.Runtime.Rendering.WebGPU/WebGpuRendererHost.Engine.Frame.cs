using System.Buffers.Binary;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private const int EngineFrameHeaderBytes = 48;
    private const int EngineFrameRecordBytes = 80;
    private const int EngineFrameMaximumRecords = 4097;
    private const int EngineUniformCapacity = 4 * 1024 * 1024;
    private readonly byte[] _engineCommandArena = new byte[EngineFrameHeaderBytes + EngineFrameMaximumRecords * EngineFrameRecordBytes];
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
    private IShaderProgramArtifactResolver? _shaderArtifacts;

    /// <summary>Session-scoped cooked shader identities loaded through the engine asset source.</summary>
    public IShaderProgramArtifactResolver? ShaderArtifacts => _shaderArtifacts;
    public int LastEngineMeshDrawCount => _engineMeshDrawCount;

    /// <summary>Installs the immutable shader catalog before engine program preparation begins.</summary>
    public void BindShaderArtifacts(IShaderProgramArtifactResolver? artifacts)
    {
        ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);
        if (_engineRecording || RenderObjectCache.Count != 0)
            throw new InvalidOperationException("WebGPU.Shaders.AlreadyActive: bind the shader catalog before creating engine API objects.");
        SetField(ref _shaderArtifacts, artifacts);
    }

    /// <summary>Binds a host-owned engine viewport without acquiring desktop window services.</summary>
    public void BindEngineViewport(XRViewport? viewport)
    {
        ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);
        if (_engineRecording)
            throw new InvalidOperationException("WebGPU.Frame.Active: the engine viewport cannot change during recording.");
        SetField(ref _engineViewport, viewport);
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
        SetField(ref _engineDrawPending, false, publishNotifications: false);
        SetField(ref _engineMeshDrawCount, 0, publishNotifications: false);
        SetField(ref _engineRecording, true, publishNotifications: false);
    }

    internal void MarkEngineDrawPending()
        => SetField(ref _engineDrawPending, true, publishNotifications: false);

    internal void CountEngineMeshDraw()
        => SetField(ref _engineMeshDrawCount, _engineMeshDrawCount + 1, publishNotifications: false);

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

    internal void RecordEngineCommands(int handle, ReadOnlySpan<uint> dynamicOffsets)
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
        SetField(ref _engineCommandCount, _engineCommandCount + 1, publishNotifications: false);
    }

    private void SubmitEngineFrame(in RenderFrameOutputDescription output)
    {
        int length = EngineFrameHeaderBytes + _engineCommandCount * EngineFrameRecordBytes;
        Span<byte> header = _engineCommandArena.AsSpan(0, EngineFrameHeaderBytes);
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x45475258);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], length);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], _engineCommandCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], _session);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], checked((uint)output.TargetGeneration));
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], output.Properties.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], output.Properties.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header[32..], _engineUniformBuffer);
        BinaryPrimitives.WriteInt32LittleEndian(header[36..], _engineUniformBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], _engineFrameSequence);
        Span<byte> uniforms = _engineUniformArena is null ? Span<byte>.Empty : _engineUniformArena.AsSpan(0, _engineUniformBytes);
        bool presented = WebGpuImports.SubmitEngineFrame(_session, _engineCommandArena.AsSpan(0, length), uniforms);
        SetField(ref _submittedFrame, presented && !_engineDrawPending);
    }
}
