namespace XREngine.Rendering.WebGPU;

/// <summary>Retains one bounded GPU row reversal for an explicitly opted-in captured atlas layer.</summary>
internal sealed class WebGpuSceneCaptureOriginPlan : IDisposable
{
    private readonly WebGpuRendererHost _renderer;
    private readonly int _texture;
    private readonly int _layer;
    private readonly int _width;
    private readonly int _height;
    private Task? _preparation;
    private WebGpuResourceRequest? _scratchRequest;
    private WebGpuResourceRequest? _sourceViewRequest;
    private WebGpuResourceRequest? _targetViewRequest;
    private WebGpuResourceRequest? _groupRequest;
    private WebGpuResourceRequest? _commandsRequest;
    private int _scratch;
    private int _sourceView;
    private int _targetView;
    private int _shader;
    private int _layout;
    private int _pipeline;
    private int _group;
    private int _commands;
    private uint _recordedFrame;
    private bool _disposed;

    internal WebGpuSceneCaptureOriginPlan(WebGpuRendererHost renderer, int texture, int layer, uint width, uint height)
    {
        if (texture == 0 || layer < 0 || layer >= 26 || width < 1 || width > 1024 || height < 1 || height > 1024)
            throw new NotSupportedException("WebGPU.SceneCapture.OriginProfile: atlas normalization requires one of 26 RGBA16F layers at no more than 1024 by 1024 pixels.");
        _renderer = renderer;
        _texture = texture;
        _layer = layer;
        _width = checked((int)width);
        _height = checked((int)height);
    }

    internal bool Matches(int texture, int layer, uint width, uint height)
        => !_disposed && _texture == texture && _layer == layer && _width == width && _height == height;

    internal void Prepare()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _preparation ??= PreparePipelineAsync();
        if (_scratch == 0)
        {
            _scratchRequest ??= _renderer.RequestEngineResource(this, 2,
                new BrowserTextureDescription(_width, _height, "rgba16float",
                    BrowserTextureUsage.CopyDestination | BrowserTextureUsage.TextureBinding,
                    Label: "Capture origin scratch"));
            _scratch = _renderer.RequireEngineResource(_scratchRequest);
            _scratchRequest = null;
        }
        if (_sourceView == 0)
            _sourceViewRequest ??= _renderer.RequestEngineResource(this, 3,
                new BrowserTextureViewDescription(_scratch, Label: "Capture origin source"));
        if (_targetView == 0)
            _targetViewRequest ??= _renderer.RequestEngineResource(this, 3,
                new BrowserTextureViewDescription(_texture, BaseArrayLayer: _layer, Label: "Capture origin target"));
        RequireResource(ref _sourceView, ref _sourceViewRequest);
        RequireResource(ref _targetView, ref _targetViewRequest);
        if (!_preparation.IsCompleted)
            throw new WebGpuResourcePreparationPendingException("WebGPU.SceneCapture.OriginPipelinePending: capture row normalization is being prepared.");
        _preparation.GetAwaiter().GetResult();
        if (_group == 0)
            _groupRequest ??= _renderer.RequestEngineResource(this, 6, FormattableString.Invariant(
                $"{{\"label\":\"Capture origin source\",\"layout\":{_layout},\"entries\":[{{\"binding\":0,\"resource\":{_sourceView}}}]}}"));
        RequireResource(ref _group, ref _groupRequest);
        if (_commands == 0)
            _commandsRequest ??= _renderer.RequestEngineResource(this, 7, FormattableString.Invariant(
                $"{{\"label\":\"Capture atlas origin\",\"commands\":[{{\"type\":\"copyTexture\",\"source\":{_texture},\"destination\":{_scratch},\"sourceMip\":0,\"destinationMip\":0,\"sourceLayer\":{_layer},\"destinationLayer\":0,\"width\":{_width},\"height\":{_height}}},{{\"type\":\"render\",\"pass\":{{\"colors\":[{{\"viewHandle\":{_targetView},\"loadOp\":\"clear\",\"storeOp\":\"store\",\"clearValue\":[0,0,0,0]}}]}},\"pipeline\":{_pipeline},\"bindings\":[{{\"index\":0,\"group\":{_group},\"dynamicOffsets\":[]}}],\"vertexBuffers\":[],\"draws\":[{{\"type\":\"draw\",\"vertexCount\":3}}]}}]}}"));
        RequireResource(ref _commands, ref _commandsRequest);
    }

    private void RequireResource(ref int handle, ref WebGpuResourceRequest? request)
    {
        if (handle != 0) return;
        handle = _renderer.RequireEngineResource(request!);
        request = null;
    }

    private async Task PreparePipelineAsync()
    {
        _shader = KeepPrepared(await _renderer.CreateShaderModuleAsync(ShaderSource, "Capture origin row reversal"));
        _layout = KeepPrepared(await _renderer.CreateEngineBindingLayoutAsync(this,
            "{\"label\":\"Capture origin layout\",\"entries\":[{\"binding\":0,\"visibility\":2,\"texture\":{\"sampleType\":\"float\",\"viewDimension\":\"2d\",\"multisampled\":false}}]}"));
        _pipeline = KeepPrepared(await _renderer.CreateRenderPipelineAsync(FormattableString.Invariant(
            $"{{\"label\":\"Capture origin pipeline\",\"layouts\":[{_layout}],\"vertex\":{{\"shader\":{_shader},\"entryPoint\":\"vertexMain\",\"buffers\":[]}},\"fragment\":{{\"shader\":{_shader},\"entryPoint\":\"fragmentMain\",\"targets\":[{{\"format\":\"rgba16float\"}}]}},\"primitive\":{{\"topology\":\"triangle-list\",\"frontFace\":\"ccw\",\"cullMode\":\"none\"}},\"multisample\":{{\"count\":1}}}}")));
    }

    private int KeepPrepared(int handle)
    {
        if (!_disposed && _renderer.AcceptsBackendWork) return handle;
        _renderer.RetireEngineResourceAfterFrame(handle);
        throw new InvalidOperationException("WebGPU.SceneCapture.OriginOwnerRetired: the capture retired during row normalization preparation.");
    }

    /// <summary>Appends copy and reversal after the layer producer; its owner must then mark that texture produced.</summary>
    internal void Record()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_renderer.IsRecordingEngineFrame || _commands == 0)
            throw new WebGpuResourcePreparationPendingException("WebGPU.SceneCapture.OriginFrameRequired: row normalization requires a prepared normal engine frame.");
        // A repeated recording callback in one frame must never reverse twice.
        // An aborted frame can retry after its scene layer has been rendered again.
        if (_recordedFrame == _renderer.EngineFrameSequence) return;
        _renderer.RecordEngineCommands(_commands, []);
        _renderer.MarkEngineTextureRecorded(_texture);
        _renderer.MarkEngineTextureRecorded(_scratch);
        _recordedFrame = _renderer.EngineFrameSequence;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Exception? failure = null;
        try { _renderer.CancelEngineResourceRequests(this); }
        catch (Exception error) { failure = error; }
        Retire(ref _commands, ref failure);
        Retire(ref _group, ref failure);
        Retire(ref _sourceView, ref failure);
        Retire(ref _targetView, ref failure);
        Retire(ref _pipeline, ref failure);
        Retire(ref _layout, ref failure);
        Retire(ref _shader, ref failure);
        Retire(ref _scratch, ref failure);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void Retire(ref int handle, ref Exception? failure)
    {
        int retired = handle;
        handle = 0;
        try { _renderer.RetireEngineResourceAfterFrame(retired); }
        catch (Exception error) { failure ??= error; }
    }

    // Integer texel loads keep X, every RGBA channel, HDR values and alpha intact.
    // Only this opted-in producer changes row convention; generic readback stays native.
    internal const string ShaderSource = """
        @group(0) @binding(0) var source: texture_2d<f32>;

        @vertex fn vertexMain(@builtin(vertex_index) index: u32) -> @builtin(position) vec4f {
            let positions = array<vec2f, 3>(vec2f(-1.0, -1.0), vec2f(3.0, -1.0), vec2f(-1.0, 3.0));
            return vec4f(positions[index], 0.0, 1.0);
        }

        @fragment fn fragmentMain(@builtin(position) position: vec4f) -> @location(0) vec4f {
            let coordinate = vec2u(position.xy);
            let height = textureDimensions(source).y;
            return textureLoad(source, vec2i(vec2u(coordinate.x, height - 1u - coordinate.y)), 0);
        }
        """;
}
