namespace XREngine.Rendering.WebGPU;

/// <summary>Lowers a GPU-written command count to a bounded stream of zero-padded indexed arguments.</summary>
internal sealed class WebGpuIndirectCountKernel : IDisposable
{
    private readonly WebGpuRendererHost _renderer;
    private readonly Task _preparation;
    private int _shader;
    private int _layout;
    private int _pipeline;
    private bool _disposed;

    public WebGpuIndirectCountKernel(WebGpuRendererHost renderer)
    {
        _renderer = renderer;
        _preparation = PrepareAsync();
    }

    public int Layout => _layout;
    public int Pipeline => _pipeline;
    public Task Preparation => _preparation;

    private async Task PrepareAsync()
    {
        // Desktop indirect counts have no WebGPU equivalent. The count stays on the GPU;
        // inactive slots are fully zeroed on every execution, including stale prior frames.
        const string source = """
            struct Parameters { sourceWord: u32, strideWords: u32, countWord: u32, maximum: u32 }
            @group(0) @binding(0) var<storage, read> source: array<u32>;
            @group(0) @binding(1) var<storage, read> count: array<u32>;
            @group(0) @binding(2) var<storage, read_write> destination: array<u32>;
            @group(0) @binding(3) var<uniform> parameters: Parameters;
            @compute @workgroup_size(64)
            fn mask(@builtin(global_invocation_id) id: vec3<u32>) {
                if (id.x >= parameters.maximum) { return; }
                let outputWord = id.x * 5u;
                let inputWord = parameters.sourceWord + id.x * parameters.strideWords;
                let visible = id.x < min(count[parameters.countWord], parameters.maximum);
                for (var word = 0u; word < 5u; word++) {
                    destination[outputWord + word] = select(0u, source[inputWord + word], visible);
                }
            }
            """;
        int shader = await _renderer.CreateShaderModuleAsync(source, "Indirect count masking");
        if (!Accept(shader)) return;
        _shader = shader;
        _layout = _renderer.CreateBindingLayout("""
            {"label":"Indirect count masking","entries":[
            {"binding":0,"visibility":4,"buffer":{"type":"read-only-storage","minBindingSize":20}},
            {"binding":1,"visibility":4,"buffer":{"type":"read-only-storage","minBindingSize":4}},
            {"binding":2,"visibility":4,"buffer":{"type":"storage","minBindingSize":20}},
            {"binding":3,"visibility":4,"buffer":{"type":"uniform","minBindingSize":16}}]}
            """);
        int pipeline = await _renderer.CreateComputePipelineAsync(
            $$$"""{"label":"Indirect count masking","layouts":[{{{_layout}}}],"compute":{"shader":{{{_shader}}},"entryPoint":"mask","workgroupSize":[64,1,1]}}""");
        if (Accept(pipeline)) _pipeline = pipeline;
    }

    private bool Accept(int handle)
    {
        if (!_disposed && _renderer.AcceptsBackendWork) return true;
        _renderer.RetireEngineResourceAfterFrame(handle);
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderer.RetireEngineResourceAfterFrame(_pipeline);
        _renderer.RetireEngineResourceAfterFrame(_layout);
        _renderer.RetireEngineResourceAfterFrame(_shader);
        _pipeline = _layout = _shader = 0;
    }
}
