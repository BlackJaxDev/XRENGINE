namespace XREngine.Rendering.WebGPU;

/// <summary>Retains ordinary color downsample passes for one physical layered texture generation.</summary>
internal sealed class WebGpuTextureMipmapPlan : IDisposable
{
    internal const int MaximumPasses = 512;
    private readonly WebGpuRendererHost _renderer;
    private readonly int _texture;
    private readonly int _mips;
    private readonly int _layers;
    private readonly string _format;
    private readonly int[] _views;
    private readonly int[] _groups;
    private readonly int[] _commands;
    private readonly WebGpuResourceRequest?[] _viewRequests;
    private readonly WebGpuResourceRequest?[] _groupRequests;
    private readonly WebGpuResourceRequest?[] _commandRequests;
    private Task? _preparation;
    private int _shader;
    private int _layout;
    private int _pipeline;
    private bool _disposed;

    public WebGpuTextureMipmapPlan(WebGpuRendererHost renderer, int texture, int mips, int layers, string format)
    {
        if (mips < 2 || layers < 1 || (long)(mips - 1) * layers > MaximumPasses ||
            format is not ("rgba8unorm" or "rgba8unorm-srgb" or "rgba16float"))
            throw new NotSupportedException("WebGPU.Texture.MipmapProfile: ordinary RGBA8/RGBA16F generation requires at most 512 mip/layer passes.");
        _renderer = renderer;
        _texture = texture;
        _mips = mips;
        _layers = layers;
        _format = format;
        _views = new int[mips * layers];
        _groups = new int[(mips - 1) * layers];
        _commands = new int[_groups.Length];
        _viewRequests = new WebGpuResourceRequest?[_views.Length];
        _groupRequests = new WebGpuResourceRequest?[_groups.Length];
        _commandRequests = new WebGpuResourceRequest?[_commands.Length];
    }

    public void Prepare()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _preparation ??= PreparePipelineAsync();
        if (!_preparation.IsCompleted)
            throw new WebGpuResourcePreparationPendingException("WebGPU.Texture.MipmapPipelinePending: the ordinary color downsample pipeline is being prepared.");
        _preparation.GetAwaiter().GetResult();
        // Queue whole preparation stages before requiring their receipts. A large
        // layer array therefore needs a bounded number of acceptance rounds.
        for (int layer = 0; layer < _layers; layer++)
            for (int mip = 0; mip < _mips; mip++)
            {
                int index = layer * _mips + mip;
                if (_views[index] == 0 && _viewRequests[index] is null)
                    _viewRequests[index] = _renderer.RequestEngineResource(this, 3,
                        new BrowserTextureViewDescription(_texture, mip, 1, "all", "Ordinary mip subresource", layer));
            }
        RequireStage(_views, _viewRequests);
        for (int layer = 0; layer < _layers; layer++)
            for (int mip = 1; mip < _mips; mip++)
            {
                int index = layer * (_mips - 1) + mip - 1;
                if (_groups[index] == 0 && _groupRequests[index] is null)
                    _groupRequests[index] = _renderer.RequestEngineResource(this, 6, FormattableString.Invariant(
                        $"{{\"label\":\"Ordinary mip source\",\"layout\":{_layout},\"entries\":[{{\"binding\":0,\"resource\":{_views[layer * _mips + mip - 1]}}}]}}"));
            }
        RequireStage(_groups, _groupRequests);
        for (int layer = 0; layer < _layers; layer++)
            for (int mip = 1; mip < _mips; mip++)
            {
                int index = layer * (_mips - 1) + mip - 1;
                if (_commands[index] == 0 && _commandRequests[index] is null)
                    _commandRequests[index] = _renderer.RequestEngineResource(this, 7, FormattableString.Invariant(
                        $"{{\"label\":\"Ordinary color mip\",\"commands\":[{{\"type\":\"render\",\"pass\":{{\"colors\":[{{\"viewHandle\":{_views[layer * _mips + mip]},\"loadOp\":\"clear\",\"storeOp\":\"store\",\"clearValue\":[0,0,0,0]}}]}},\"pipeline\":{_pipeline},\"bindings\":[{{\"index\":0,\"group\":{_groups[index]},\"dynamicOffsets\":[]}}],\"vertexBuffers\":[],\"draws\":[{{\"type\":\"draw\",\"vertexCount\":3}}]}}]}}"));
            }
        RequireStage(_commands, _commandRequests);
    }

    private void RequireStage(int[] handles, WebGpuResourceRequest?[] requests)
    {
        for (int index = 0; index < handles.Length; index++)
        {
            if (handles[index] != 0) continue;
            handles[index] = _renderer.RequireEngineResource(requests[index]!);
            requests[index] = null;
        }
    }

    private async Task PreparePipelineAsync()
    {
        // These immutable shader/pipeline preparations use the same asynchronous
        // compilation boundary as cooked programs. Resource creation and pass
        // execution retain the normal engine request and frame journals.
        _shader = KeepPrepared(await _renderer.CreateShaderModuleAsync(ShaderSource, "Ordinary color mip downsample"));
        _layout = KeepPrepared(await _renderer.CreateEngineBindingLayoutAsync(this,
            "{\"label\":\"Ordinary mip layout\",\"entries\":[{\"binding\":0,\"visibility\":2,\"texture\":{\"sampleType\":\"float\",\"viewDimension\":\"2d\",\"multisampled\":false}}]}"));
        _pipeline = KeepPrepared(await _renderer.CreateRenderPipelineAsync(FormattableString.Invariant(
            $"{{\"label\":\"Ordinary color mip pipeline\",\"layouts\":[{_layout}],\"vertex\":{{\"shader\":{_shader},\"entryPoint\":\"vertexMain\",\"buffers\":[]}},\"fragment\":{{\"shader\":{_shader},\"entryPoint\":\"fragmentMain\",\"targets\":[{{\"format\":\"{_format}\"}}]}},\"primitive\":{{\"topology\":\"triangle-list\",\"frontFace\":\"ccw\",\"cullMode\":\"none\"}},\"multisample\":{{\"count\":1}}}}")));
    }

    private int KeepPrepared(int handle)
    {
        if (!_disposed && _renderer.AcceptsBackendWork) return handle;
        _renderer.RetireEngineResourceAfterFrame(handle);
        throw new InvalidOperationException("WebGPU.Texture.MipmapOwnerRetired: the downsample owner retired during preparation.");
    }

    public void Record()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_renderer.IsRecordingEngineFrame || _commands.Length == 0 || _commands[^1] == 0)
            throw new WebGpuResourcePreparationPendingException("WebGPU.Texture.MipmapFrameRequired: a complete mip plan requires normal frame recording.");
        foreach (int command in _commands) _renderer.RecordEngineCommands(command, []);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderer.CancelEngineResourceRequests(this);
        foreach (int command in _commands) _renderer.RetireEngineResourceAfterFrame(command);
        foreach (int group in _groups) _renderer.RetireEngineResourceAfterFrame(group);
        foreach (int view in _views) _renderer.RetireEngineResourceAfterFrame(view);
        _renderer.RetireEngineResourceAfterFrame(_pipeline);
        _renderer.RetireEngineResourceAfterFrame(_layout);
        _renderer.RetireEngineResourceAfterFrame(_shader);
    }

    // Texture loads decode an sRGB source; the destination attachment encodes
    // sRGB on store. HDR remains floating-point, with no clamp or tone mapping.
    internal const string ShaderSource = """
        @group(0) @binding(0) var source: texture_2d<f32>;

        @vertex fn vertexMain(@builtin(vertex_index) index: u32) -> @builtin(position) vec4f {
            let positions = array<vec2f, 3>(vec2f(-1.0, -1.0), vec2f(3.0, -1.0), vec2f(-1.0, 3.0));
            return vec4f(positions[index], 0.0, 1.0);
        }

        @fragment fn fragmentMain(@builtin(position) position: vec4f) -> @location(0) vec4f {
            let sourceSize = textureDimensions(source);
            let destinationSize = max(vec2u(1u), sourceSize / 2u);
            let coordinate = vec2u(position.xy);
            let first = coordinate * sourceSize / destinationSize;
            let last = (coordinate + vec2u(1u)) * sourceSize / destinationSize;
            var color = vec4f(0.0);
            var count = 0u;
            for (var y = first.y; y < max(first.y + 1u, last.y); y++) {
                for (var x = first.x; x < max(first.x + 1u, last.x); x++) {
                    color += textureLoad(source, vec2i(x, y), 0);
                    count++;
                }
            }
            return color / f32(count);
        }
        """;
}
