using System.Buffers;
using System.Text;
using System.Text.Json;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Retains one fully keyed engine mesh pipeline and draw command until its resource generation changes.</summary>
internal sealed partial class WebGpuMeshDraw : IDisposable
{
    private readonly WebGpuRendererHost _renderer;
    private readonly WebGpuRenderProgram _program;
    private readonly WebGpuVertexStream[] _streams;
    private readonly WebGpuDataBuffer? _indices;
    private readonly WebGpuOwnedStorageBuffer? _generatedIndices;
    private bool HasIndexBuffer => _indices is not null || _generatedIndices is not null;
    private readonly IndexSize _indexSize;
    private readonly uint _indexCount;
    private readonly WebGpuRasterState _state;
    private readonly RenderFrameOutputDescription _output;
    private readonly WebGpuFrameBuffer? _frameBuffer;
    private readonly Task _preparation;
    private readonly bool _indirectFirstInstanceFeature = true;
    private WebGpuInstanceStorageContract? _instanceStorage;
    private WebGpuDataBuffer? _instanceBuffer;
    private uint _instanceLimit;
    private uint _commandInstanceLimit;
    internal WebGpuMeshDeformation? Deformation { get; }
    private int _pipeline;
    internal int DiagnosticPipelineHandle => _pipeline;
    private readonly Dictionary<WebGpuBindingSet, int> _commands = [];
    private bool _disposed;

    public WebGpuMeshDraw(WebGpuRendererHost renderer, WebGpuRenderProgram program, XRMesh? mesh,
        XRDataBuffer indices, IndexSize indexSize, WebGpuRasterState state, in RenderFrameOutputDescription output,
        WebGpuFrameBuffer? frameBuffer, WebGpuInstanceStorageContract? instanceStorage,
        WebGpuDataBuffer? instanceBuffer, uint instanceLimit, WebGpuMeshDeformation? deformation = null,
        XRMeshRenderer? streamOwner = null, GpuMeshSubmissionSourceBindings? sources = null,
        bool indirectFirstInstanceFeature = true)
    {
        _renderer = renderer;
        _program = program;
        _state = state;
        _output = output;
        _frameBuffer = frameBuffer;
        _instanceStorage = instanceStorage;
        _instanceBuffer = instanceBuffer;
        _instanceLimit = instanceLimit;
        Deformation = deformation;
        _indexSize = indexSize;
        _indirectFirstInstanceFeature = indirectFirstInstanceFeature;
        if (indexSize is not (IndexSize.TwoBytes or IndexSize.FourBytes))
            throw Unsupported("only unsigned 16-bit and 32-bit indices are admitted");
        _indices = (WebGpuDataBuffer)renderer.GetOrCreateAPIRenderObject(indices, generateNow: true)!;
        _indexCount = indices.ElementCount;
        _streams = ResolveStreams(renderer, program.Artifact, mesh, deformation, streamOwner, sources);
        _preparation = PrepareAsync();
    }

    /// <summary>Rasterizes only the GPU-expanded uint32 meshlet stream with the original authored vertex and fragment program.</summary>
    internal WebGpuMeshDraw(WebGpuRendererHost renderer, WebGpuRenderProgram program, XRMesh mesh,
        WebGpuOwnedStorageBuffer indices, WebGpuRasterState state, in RenderFrameOutputDescription output,
        WebGpuFrameBuffer? frameBuffer, WebGpuMeshDeformation? deformation, GpuMeshSubmissionSourceBindings sources)
    {
        if (indices.Owner != renderer || !indices.IsGenerated || indices.IsRetired)
            throw Unsupported("generated indices require live storage owned by the current renderer");
        _renderer = renderer;
        _program = program;
        _state = state;
        _output = output;
        _frameBuffer = frameBuffer;
        _generatedIndices = indices;
        Deformation = deformation;
        _indexSize = IndexSize.FourBytes;
        _indirectFirstInstanceFeature = false;
        _streams = ResolveStreams(renderer, program.Artifact, mesh, deformation, null, sources);
        _preparation = PrepareAsync();
    }

    /// <summary>Uses canonical storage-pulled geometry without inventing CPU mesh or index bindings.</summary>
    public WebGpuMeshDraw(WebGpuRendererHost renderer, WebGpuRenderProgram program,
        WebGpuRasterState state, in RenderFrameOutputDescription output, WebGpuFrameBuffer? frameBuffer)
    {
        if (!program.Artifact.VertexBuffers.IsDefaultOrEmpty)
            throw Unsupported("vertexless indirect raster requires an empty cooked vertex-buffer layout");
        _renderer = renderer;
        _program = program;
        _state = state;
        _output = output;
        _frameBuffer = frameBuffer;
        _streams = [];
        _indirectFirstInstanceFeature = false;
        _preparation = PrepareAsync();
    }

    public bool IsReady
    {
        get
        {
            if (_preparation.IsFaulted || _preparation.IsCanceled)
                _preparation.GetAwaiter().GetResult();
            return !_disposed && _pipeline != 0 && _preparation.IsCompletedSuccessfully;
        }
    }

    public void Record(WebGpuBindingSet bindings, uint instances)
    {
        if (!IsReady)
            throw new InvalidOperationException("WebGPU.Mesh.PipelinePending: defer the draw until asynchronous pipeline creation completes.");
        StageGeometryUploads();
        if (bindings.IsDisposed)
        {
            _renderer.MarkEngineDrawPending("BindingsDisposed", this);
            return;
        }
        ValidateInstanceRange(instances);
        if (_instanceStorage is { } storage && _instanceBuffer is not null &&
            instances > _instanceBuffer.Data.Length / (uint)storage.StrideBytes)
            throw Unsupported("the requested instance count exceeds the published logical storage length");
        if (_instanceStorage is { } uiStorage)
            ValidateUIStorageExtent(_program, uiStorage.Name, instances);
        uint limit = DirectInstanceLimit();
        if (_commandInstanceLimit != limit)
        {
            foreach (int handle in _commands.Values) _renderer.RetireEngineResourceAfterFrame(handle);
            _commands.Clear();
            _commandInstanceLimit = limit;
        }
        (BoundingRectangle? viewport, BoundingRectangle? scissor) = _renderer.ResolveEngineDrawArea();
        bool croppedOut = scissor is { Width: 0 } or { Height: 0 };
        if (!_commands.TryGetValue(bindings, out int commands))
        {
            if (_commands.Count >= 64)
                throw Unsupported("the draw exceeds 64 retained resource binding variants for its current pipeline");
            commands = _renderer.PrepareEngineCommands(this, DescribeDraw(bindings));
            _commands.Add(bindings, commands);
        }
        Span<uint> offsets = stackalloc uint[16];
        int count = _program.SnapshotUniforms(offsets);
        _renderer.RecordEngineCommands(commands, offsets[..count], instances,
            viewport, scissor);
        _renderer.MarkEngineViewHistoryDrawWrite(_frameBuffer, in _output,
            _program.Artifact.FragmentEntryPoint is not null, _state.ColorWriteMask,
            _indices is null ? 0 : _indexCount, instances, scissor);
        bindings.MarkRecorded();
        if (!croppedOut)
        {
            _frameBuffer?.MarkRecorded();
            _renderer.CountEngineMeshDraw();
        }
    }

    /// <summary>Checks authored instance addressing without inspecting any GPU-produced visibility or count.</summary>
    internal void ValidateInstanceRange(uint instances)
    {
        if (_instanceLimit != 0 && instances > _instanceLimit)
            throw Unsupported("the requested instance count exceeds the cooked storage binding range");
        foreach (WebGpuVertexStream stream in _streams)
            if (stream.StepMode == "instance" &&
                (stream.Buffer.Data.InstanceDivisor != 1 || stream.Buffer.Data.ElementSize != stream.Stride ||
                 instances > stream.Buffer.Data.ElementCount || (ulong)instances * (uint)stream.Stride > stream.Buffer.Data.Length))
                throw Unsupported("the requested instance count exceeds an authored instance-step stream");
    }

    private uint DirectInstanceLimit()
    {
        uint limit = _instanceLimit == 0 ? uint.MaxValue : _instanceLimit;
        foreach (WebGpuVertexStream stream in _streams)
            if (stream.StepMode == "instance")
                limit = Math.Min(limit, Math.Min(stream.Buffer.Data.ElementCount, stream.Buffer.Data.Length / checked((uint)stream.Stride)));
        if (limit == 0) throw Unsupported("an authored instance-step stream has no complete row");
        return limit;
    }

    internal static void ValidateUIStorageExtent(WebGpuRenderProgram program, string sourceName, uint instances)
    {
        if (sourceName == "QuadTransformBuffer")
        {
            RequireStorageExtent(program, "QuadColorBuffer", instances, 16);
            RequireStorageExtent(program, "QuadBoundsBuffer", instances, 16);
            return;
        }
        if (sourceName != "GlyphTransformsBuffer") return;

        RequireStorageExtent(program, "GlyphTexCoordsBuffer", instances, 16);
        WebGpuDataBuffer indexBuffer = RequireStorageExtent(program, "GlyphTextIndexBuffer", instances, 4);
        WebGpuDataBuffer textBuffer = RequireStorageExtent(program, "TextInstanceBuffer", 1, 128);
        if (indexBuffer.Data is not XRDataBuffer<uint> indices)
            throw Unsupported("bitmap text requires a CPU-backed uint glyph-to-text index buffer");
        Span<uint> indexValues = indices.GetCpuMirrorSpan();
        if (indexValues.Length < instances)
            throw Unsupported("bitmap text glyph indices have no complete CPU mirror");
        uint textCount = textBuffer.Data.Length / 128;
        for (int index = 0; index < instances; index++)
            if (indexValues[index] >= textCount)
                throw Unsupported("bitmap text glyph index exceeds the published text metadata range");
    }

    /// <summary>Replays uploads abandoned while this retained pipeline was preparing asynchronously.</summary>
    private void StageGeometryUploads()
    {
        if (_indices is { } indices)
        {
            indices.Generate();
            indices.StagePendingUpload();
        }
        foreach (WebGpuVertexStream stream in _streams)
        {
            stream.Buffer.Generate();
            stream.Buffer.StagePendingUpload();
        }
    }

    internal static WebGpuDataBuffer RequireStorageExtent(WebGpuRenderProgram program, string name, uint instances, uint stride,
        bool requirePackedLength = true)
    {
        if (!program.TryGetStorageBinding(name, out WebGpuDataBuffer? buffer) || buffer is null ||
            requirePackedLength && buffer.Data.Length % stride != 0 || buffer.Data.Length / stride < instances ||
            buffer.BackendAllocatedByteSize < buffer.Data.Length)
            throw Unsupported($"'{name}' does not cover the requested authored instance range");
        return buffer;
    }

    internal bool DependsOn(AbstractRenderAPIObject resource)
    {
        if (IndirectDependsOn(resource) || DirectDependsOn(resource)) return true;
        if (ReferenceEquals(_program, resource) || ReferenceEquals(_indices, resource) || ReferenceEquals(_generatedIndices, resource))
            return true;
        if (_frameBuffer?.DependsOn(resource) == true)
            return true;
        foreach (WebGpuBindingSet bindings in _commands.Keys)
            if (bindings.DependsOn(resource)) return true;
        if (ReferenceEquals(_instanceBuffer, resource)) return true;
        foreach (WebGpuVertexStream stream in _streams)
            if (ReferenceEquals(stream.Buffer, resource)) return true;
        return false;
    }

    internal void UpdateInstanceSource(WebGpuInstanceStorageContract? storage, WebGpuDataBuffer? buffer, uint limit)
    {
        if (_instanceLimit == limit && _instanceStorage == storage && ReferenceEquals(_instanceBuffer, buffer)) return;
        _instanceStorage = storage;
        _instanceBuffer = buffer;
        _instanceLimit = limit;
    }

    internal void ReleaseCommandsUsingHandle(AbstractRenderAPIObject resource, int handle)
    {
        if (ReferenceEquals(_indices, resource) || ReferenceEquals(_generatedIndices, resource))
        {
            ClearCommands();
            return;
        }
        foreach (WebGpuVertexStream stream in _streams)
            if (ReferenceEquals(stream.Buffer, resource))
            {
                ClearCommands();
                return;
            }
        ReleaseIndirectCommandsUsing(resource, handle);
        ReleaseDirectCommandsUsing(resource, handle);
        List<WebGpuBindingSet>? removed = null;
        foreach (KeyValuePair<WebGpuBindingSet, int> item in _commands)
            if (item.Key.UsesHandle(resource, handle))
            {
                _renderer.RetireEngineResourceAfterFrame(item.Value);
                (removed ??= []).Add(item.Key);
            }
        if (removed is not null)
            foreach (WebGpuBindingSet bindings in removed) _commands.Remove(bindings);
    }

    /// <summary>Releases one retired descriptor variant without rebuilding its mesh pipeline.</summary>
    internal void ReleaseCommandUsing(WebGpuRenderProgram program, WebGpuBindingSet bindings)
    {
        if (!ReferenceEquals(_program, program)) return;
        ReleaseIndirectCommandsUsing(bindings: bindings);
        ReleaseDirectCommandsUsing(bindings: bindings);
        if (_commands.Remove(bindings, out int command))
            _renderer.RetireEngineResourceAfterFrame(command);
    }

    private void ClearCommands()
    {
        ReleaseIndirectCommandsUsing();
        ReleaseDirectCommandsUsing();
        foreach (int commands in _commands.Values) _renderer.RetireEngineResourceAfterFrame(commands);
        _commands.Clear();
    }

    private async Task PrepareAsync()
    {
        int pipeline = await _renderer.CreateRenderPipelineAsync(DescribePipeline());
        if (_disposed || !_renderer.AcceptsBackendWork)
        {
            if (_renderer.State == BrowserRendererState.Ready) _renderer.RetireEngineResource(pipeline);
            return;
        }
        _pipeline = pipeline;
    }

    private static WebGpuVertexStream[] ResolveStreams(WebGpuRendererHost renderer, ShaderProgramArtifact artifact, XRMesh? mesh,
        WebGpuMeshDeformation? deformation, XRMeshRenderer? streamOwner, GpuMeshSubmissionSourceBindings? sources = null)
    {
        List<WebGpuVertexStream> streams = [];
        foreach (ShaderVertexBufferLayout authored in artifact.VertexBuffers)
        {
            foreach (ShaderVertexAttribute attribute in authored.Attributes)
            {
                (XRDataBuffer buffer, int offset, string format) = ResolveAttribute(mesh, attribute.Semantic, deformation, streamOwner, sources, renderer);
                if (attribute.Format != format || buffer.InstanceDivisor > 1)
                    throw Unsupported($"vertex semantic '{attribute.Semantic}' has an incompatible format or instance divisor");
                string stepMode = buffer.InstanceDivisor == 0 ? "vertex" : "instance";
                if (authored.StepMode != stepMode)
                    throw Unsupported($"vertex semantic '{attribute.Semantic}' has an incompatible step mode");
                WebGpuDataBuffer api = (WebGpuDataBuffer)renderer.GetOrCreateAPIRenderObject(buffer, generateNow: true)!;
                WebGpuVertexStream? stream = null;
                for (int i = 0; i < streams.Count; i++)
                    if (ReferenceEquals(streams[i].Buffer, api)) { stream = streams[i]; break; }
                if (stream is null)
                {
                    stream = new WebGpuVertexStream(api, renderer.IsAuthoredConstantVertex(buffer) ? 0 : checked((int)buffer.ElementSize), stepMode);
                    streams.Add(stream);
                }
                if (artifact.Pass == "screen-ui" && stream.Stride != authored.Stride)
                    throw Unsupported($"vertex semantic '{attribute.Semantic}' has an incompatible declared stream stride");
                stream.Attributes.Add(attribute with { Offset = offset });
            }
        }
        return streams.ToArray();
    }

    internal static (XRDataBuffer Buffer, int Offset, string Format) ResolveAttribute(XRMesh? mesh, string semantic,
        WebGpuMeshDeformation? deformation, XRMeshRenderer? streamOwner = null, GpuMeshSubmissionSourceBindings? sources = null,
        WebGpuRendererHost? renderer = null)
    {
        if (semantic is "uv0-or-zero" or "uv1-or-zero" or "uv2-or-zero" or "uv3-or-zero" or "color0-or-default")
        {
            bool color = semantic == "color0-or-default";
            int uv = color ? 0 : semantic[2] - '0';
            string stream = color ? "Color0" : uv switch { 0 => "TexCoord0", 1 => "TexCoord1", 2 => "TexCoord2", _ => "TexCoord3" };
            bool optionalPublished = sources is not null ? sources.TryGetRendererBuffer(stream, out _)
                : streamOwner is not null && streamOwner.Buffers.ContainsKey(stream);
            bool present = optionalPublished || (color ? mesh?.ColorCount > 0 : mesh?.TexCoordCount > uv);
            if (!present)
                return (renderer is not null ? color ? renderer.RequireUberBaseDefaultColor() : renderer.RequireUberBaseZeroUv()
                    : throw Unsupported("canonical Uber attribute defaults require the owning renderer"), 0, color ? "float32x4" : "float32x2");
            semantic = color ? "color0" : uv switch { 0 => "uv0", 1 => "uv1", 2 => "uv2", _ => "uv3" };
        }
        if (semantic is "tangent-or-zero" or "tangent-presence")
        {
            bool publishedTangent = sources is not null ? sources.TryGetRendererBuffer("Tangent", out _)
                : streamOwner is not null && streamOwner.Buffers.ContainsKey("Tangent");
            bool present = publishedTangent || deformation?.HasTangents == true || mesh?.HasTangents == true;
            if (semantic == "tangent-presence")
                return (renderer?.RequireAuthoredTangentPresence() ??
                    throw Unsupported("the tangent-presence stream requires its owning renderer"), present ? 4 : 0, "float32");
            if (!present)
                return (renderer?.RequireAuthoredZeroTangent() ??
                    throw Unsupported("the optional tangent sentinel requires its owning renderer"), 0, "float32x4");
            semantic = "tangent";
        }
        string format = semantic switch
        {
            "position" or "normal" => "float32x3",
            "tangent" or "color0" => "float32x4",
            "uv0" or "uv1" or "uv2" or "uv3" => "float32x2",
            _ => throw Unsupported($"vertex semantic '{semantic}' has no engine stream mapping"),
        };
        if (deformation is not null)
        {
            if (semantic == "position") return (deformation.Positions, 0, format);
            if (semantic == "normal" && deformation.HasNormals) return (deformation.Attributes, 0, format);
            if (semantic == "tangent" && deformation.HasTangents) return (deformation.Attributes, 16, format);
        }
        string streamName = semantic switch
        {
            "position" => "Position", "normal" => "Normal", "tangent" => "Tangent",
            "color0" => "Color0", "uv0" => "TexCoord0", "uv1" => "TexCoord1",
            "uv2" => "TexCoord2", "uv3" => "TexCoord3",
            _ => throw Unsupported($"vertex semantic '{semantic}' has no canonical stream mapping"),
        };
        XRDataBuffer? published = null;
        bool hasPublished = sources is not null ? sources.TryGetRendererBuffer(streamName, out published)
            : streamOwner is not null && streamOwner.Buffers.TryGetValue(streamName, out published);
        if (hasPublished && published is not null)
        {
            if (published.ComponentType != EComponentType.Float || published.IsDestroyed)
                throw Unsupported($"published vertex semantic '{semantic}' requires a live float stream");
            return (published, 0, format);
        }
        if (mesh is null)
            throw Unsupported($"required canonical vertex stream '{streamName}' is missing");
        if (mesh.Interleaved)
        {
            uint? offset = semantic switch
            {
                "position" => mesh.PositionOffset,
                "normal" => mesh.NormalOffset,
                "tangent" => mesh.TangentOffset,
                "color0" => mesh.ColorCount > 0 ? mesh.ColorOffset : null,
                "uv0" => mesh.TexCoordCount > 0 ? mesh.TexCoordOffset : null,
                "uv1" => mesh.TexCoordCount > 1 ? mesh.TexCoordOffset + 8u : null,
                "uv2" => mesh.TexCoordCount > 2 ? mesh.TexCoordOffset + 16u : null,
                "uv3" => mesh.TexCoordCount > 3 ? mesh.TexCoordOffset + 24u : null,
                _ => null,
            };
            XRDataBuffer? interleaved = sources is not null
                ? sources.TryGetMeshBuffer("InterleavedVertex", out XRDataBuffer? frozenInterleaved) ? frozenInterleaved : null
                : mesh.InterleavedVertexBuffer;
            if (offset is null || interleaved is null)
                throw Unsupported($"required interleaved vertex semantic '{semantic}' is missing");
            return (interleaved, checked((int)offset.Value), format);
        }
        XRDataBuffer? buffer = sources is not null
            ? sources.TryGetMeshBuffer(streamName, out XRDataBuffer? frozenBuffer) ? frozenBuffer : null
            : semantic switch
        {
            "position" => mesh.PositionsBuffer,
            "normal" => mesh.NormalsBuffer,
            "tangent" => mesh.TangentsBuffer,
            "color0" => mesh.ColorBuffers is { Length: > 0 } ? mesh.ColorBuffers[0] : null,
            "uv0" => mesh.TexCoordBuffers is { Length: > 0 } ? mesh.TexCoordBuffers[0] : null,
            "uv1" => mesh.TexCoordBuffers is { Length: > 1 } ? mesh.TexCoordBuffers[1] : null,
            "uv2" => mesh.TexCoordBuffers is { Length: > 2 } ? mesh.TexCoordBuffers[2] : null,
            "uv3" => mesh.TexCoordBuffers is { Length: > 3 } ? mesh.TexCoordBuffers[3] : null,
            _ => null,
        };
        if (buffer is null || buffer.ComponentType != EComponentType.Float)
            throw Unsupported($"required float vertex semantic '{semantic}' is missing");
        return (buffer, 0, format);
    }

    private string DescribePipeline()
    {
        ArrayBufferWriter<byte> bytes = new();
        using (Utf8JsonWriter writer = new(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("label", _program.Artifact.Name);
            writer.WriteStartArray("layouts");
            foreach (int layout in _program.LayoutHandles) writer.WriteNumberValue(layout);
            writer.WriteEndArray();
            writer.WriteStartObject("vertex");
            writer.WriteNumber("shader", _program.ShaderHandle);
            writer.WriteString("entryPoint", _program.Artifact.VertexEntryPoint);
            writer.WriteStartArray("buffers");
            foreach (WebGpuVertexStream stream in _streams)
            {
                writer.WriteStartObject();
                writer.WriteNumber("arrayStride", stream.Stride);
                writer.WriteString("stepMode", stream.StepMode);
                writer.WriteStartArray("attributes");
                foreach (ShaderVertexAttribute attribute in stream.Attributes)
                {
                    writer.WriteStartObject();
                    writer.WriteString("format", attribute.Format);
                    writer.WriteNumber("offset", attribute.Offset);
                    writer.WriteNumber("shaderLocation", attribute.Location);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            if (_program.Artifact.FragmentEntryPoint is { } fragment)
            {
                writer.WriteStartObject("fragment");
                writer.WriteNumber("shader", _program.ShaderHandle);
                writer.WriteString("entryPoint", fragment);
                writer.WriteStartArray("targets");
                if (_frameBuffer is { } framebuffer)
                {
                    foreach (string? format in framebuffer.ColorFormats)
                        WriteColorTarget(writer, format);
                }
                else WriteColorTarget(writer, _output.Properties.ColorEncoding);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            else if (_frameBuffer?.HasColor == true)
                throw Unsupported("a color framebuffer requires a fragment-stage output");
            writer.WriteStartObject("primitive");
            writer.WriteString("topology", "triangle-list");
            writer.WriteString("frontFace", _state.Winding == EWinding.CounterClockwise ? "ccw" : "cw");
            writer.WriteString("cullMode", _state.CullMode switch { ECullMode.Back => "back", ECullMode.Front => "front", _ => "none" });
            writer.WriteEndObject();
            string? depthFormat = _frameBuffer is { } depthTarget ? depthTarget.DepthFormat : _output.Properties.DepthEncoding;
            if (depthFormat is not null)
            {
                writer.WriteStartObject("depthStencil");
                writer.WriteString("format", depthFormat);
                writer.WriteBoolean("depthWriteEnabled", _state.DepthEnabled && _state.DepthWrite);
                writer.WriteString("depthCompare", _state.DepthEnabled ? Compare(_state.DepthComparison) : "always");
                writer.WriteEndObject();
            }
            else if (_state.DepthEnabled)
                throw Unsupported("depth testing requires a bound depth attachment");
            writer.WriteStartObject("multisample");
            writer.WriteNumber("count", _frameBuffer?.SampleCount ?? _output.Properties.SampleCount);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(bytes.WrittenSpan);
    }

    private string DescribeDraw(WebGpuBindingSet bindings, int indirectBuffer = 0,
        uint indirectCount = 0, uint stride = 20, uint byteOffset = 0,
        uint directVertices = 0, uint directFirstVertex = 0, uint directInstances = 1,
        WebGpuAuthoredRasterSnapshot? rasterSnapshot = null)
    {
        ArrayBufferWriter<byte> bytes = new();
        using (Utf8JsonWriter writer = new(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("label", _program.Artifact.Name);
            writer.WriteStartArray("commands");
            writer.WriteStartObject();
            writer.WriteString("type", "render");
            writer.WritePropertyName("pass");
            if (_frameBuffer is { } framebuffer) framebuffer.Plan.WriteTo(writer);
            else
            {
                BrowserColorAttachmentPlan?[] colors = _program.Artifact.FragmentEntryPoint is null
                    ? [] : [new BrowserColorAttachmentPlan(0, false, true, default)];
                new BrowserFrameBufferPlan(colors,
                    new BrowserDepthStencilAttachmentPlan(-1, clearDepth: false)).WriteTo(writer);
            }
            writer.WriteNumber("pipeline", _pipeline);
            writer.WriteStartArray("bindings");
            for (int group = 0; group < bindings.GroupHandles.Length; group++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", group);
                writer.WriteNumber("group", bindings.GroupHandles[group]);
                writer.WriteStartArray("dynamicOffsets");
                foreach (ShaderStageResourceLayout resource in _program.Artifact.Resources)
                    if (resource.Contract.Set == group && resource.DynamicOffset) writer.WriteNumberValue(0);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("vertexBuffers");
            foreach (WebGpuVertexStream stream in _streams)
            {
                writer.WriteStartObject();
                writer.WriteNumber("buffer", rasterSnapshot?.ResolveBuffer(stream.Buffer.ResourceHandle) ?? stream.Buffer.ResourceHandle);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (HasIndexBuffer)
            {
                writer.WriteStartObject("indexBuffer");
                writer.WriteNumber("buffer", _generatedIndices?.ResourceHandle ??
                    (rasterSnapshot?.ResolveBuffer(_indices!.ResourceHandle) ?? _indices!.ResourceHandle));
                writer.WriteString("format", _indexSize == IndexSize.TwoBytes ? "uint16" : "uint32");
                writer.WriteEndObject();
            }
            if (indirectBuffer == 0 && _instanceStorage is { } storage && _instanceBuffer is not null)
            {
                bool found = false;
                foreach (ShaderStageResourceLayout resource in _program.Artifact.Resources)
                {
                    if (resource.Contract.Name != storage.Name ||
                        resource.Contract.Kind != ShaderAbiResourceKind.StorageBuffer) continue;
                    writer.WriteStartObject("engineInstanceStorage");
                    writer.WriteNumber("group", resource.Contract.Set);
                    writer.WriteNumber("binding", resource.Contract.Binding);
                    writer.WriteNumber("buffer", _instanceBuffer.ResourceHandle);
                    writer.WriteNumber("stride", storage.StrideBytes);
                    writer.WriteNumber("limit", _instanceLimit);
                    writer.WriteEndObject();
                    found = true;
                    break;
                }
                if (!found) throw Unsupported("the instance storage contract has no cooked program binding");
            }
            else if (indirectBuffer == 0 && HasIndexBuffer)
                writer.WriteNumber("engineInstanceCountLimit", DirectInstanceLimit());
            writer.WriteStartArray("draws");
            writer.WriteStartObject();
            if (indirectBuffer != 0)
            {
                writer.WriteString("type", HasIndexBuffer ? "drawIndexedIndirect" : "drawIndirect");
                writer.WriteNumber("buffer", indirectBuffer);
                writer.WriteNumber("offset", byteOffset);
                writer.WriteNumber("drawCount", indirectCount);
                writer.WriteNumber("stride", stride);
                writer.WriteString("firstInstancePolicy", _indirectFirstInstanceFeature ? "feature" : "zero");
            }
            else if (!HasIndexBuffer)
            {
                writer.WriteString("type", "draw");
                writer.WriteNumber("vertexCount", directVertices);
                writer.WriteNumber("firstVertex", directFirstVertex);
                writer.WriteNumber("instanceCount", directInstances);
                writer.WriteNumber("firstInstance", 0);
            }
            else
            {
                writer.WriteNumber("indexCount", _indexCount);
                writer.WriteNumber("instanceCount", 1);
            }
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(bytes.WrittenSpan);
    }

    private void WriteColorTarget(Utf8JsonWriter writer, string? format)
    {
        if (format is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("format", format);
        writer.WriteNumber("writeMask", _state.ColorWriteMask);
        if (_state.BlendEnabled)
        {
            writer.WriteStartObject("blend");
            WriteBlend(writer, "color", _state.SourceRgb, _state.DestinationRgb, _state.RgbEquation);
            WriteBlend(writer, "alpha", _state.SourceAlpha, _state.DestinationAlpha, _state.AlphaEquation);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteBlend(Utf8JsonWriter writer, string name, EBlendingFactor source, EBlendingFactor destination, EBlendEquationMode equation)
    {
        writer.WriteStartObject(name);
        writer.WriteString("operation", equation switch
        {
            EBlendEquationMode.FuncAdd => "add", EBlendEquationMode.FuncSubtract => "subtract",
            EBlendEquationMode.FuncReverseSubtract => "reverse-subtract", EBlendEquationMode.Min => "min",
            EBlendEquationMode.Max => "max", _ => throw Unsupported("unknown blend equation"),
        });
        writer.WriteString("srcFactor", BlendFactor(source));
        writer.WriteString("dstFactor", BlendFactor(destination));
        writer.WriteEndObject();
    }

    private static string BlendFactor(EBlendingFactor factor) => factor switch
    {
        EBlendingFactor.Zero => "zero", EBlendingFactor.One => "one",
        EBlendingFactor.SrcColor => "src", EBlendingFactor.OneMinusSrcColor => "one-minus-src",
        EBlendingFactor.SrcAlpha => "src-alpha", EBlendingFactor.OneMinusSrcAlpha => "one-minus-src-alpha",
        EBlendingFactor.DstColor => "dst", EBlendingFactor.OneMinusDstColor => "one-minus-dst",
        EBlendingFactor.DstAlpha => "dst-alpha", EBlendingFactor.OneMinusDstAlpha => "one-minus-dst-alpha",
        EBlendingFactor.SrcAlphaSaturate => "src-alpha-saturated",
        _ => throw Unsupported($"blend factor '{factor}' is outside the baseline profile"),
    };

    private static string Compare(EComparison comparison) => comparison switch
    {
        EComparison.Never => "never", EComparison.Less => "less", EComparison.Lequal => "less-equal",
        EComparison.Equal => "equal", EComparison.Nequal => "not-equal", EComparison.Greater => "greater",
        EComparison.Gequal => "greater-equal", EComparison.Always => "always",
        _ => throw Unsupported("unknown depth comparison"),
    };

    public void Dispose()
    {
        _renderer.CancelEngineResourceRequests(this);
        if (_disposed) return;
        _disposed = true;
        if (_renderer.State != BrowserRendererState.Disposed)
        {
            ClearCommands();
            if (_pipeline != 0) _renderer.RetireEngineResourceAfterFrame(_pipeline);
        }
        _pipeline = 0;
    }

    private static NotSupportedException Unsupported(string reason)
        => new($"WebGPU.Mesh.LayoutUnsupported: {reason}.");
}
