namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    public async Task<int> CreateShaderModuleAsync(string wgsl, string debugName = "")
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(wgsl);
        ArgumentNullException.ThrowIfNull(debugName);
        int session = _session;
        return TrackPreparedResource(session, await WebGpuImports.CreateShaderModuleAsync(session, wgsl, debugName));
    }

    public int CreateBindingLayout(string descriptorJson)
    {
        RequireCommandDescription(descriptorJson);
        return Track(WebGpuImports.CreateBindingLayout(_session, descriptorJson));
    }

    public int CreateBindingGroup(string descriptorJson)
    {
        RequireCommandDescription(descriptorJson);
        return Track(WebGpuImports.CreateBindingGroup(_session, descriptorJson));
    }

    public async Task<int> CreateRenderPipelineAsync(string descriptorJson)
    {
        RequireCommandDescription(descriptorJson);
        int session = _session;
        return TrackPreparedResource(session, await WebGpuImports.CreateRenderPipelineAsync(session, descriptorJson));
    }

    public async Task<int> CreateComputePipelineAsync(string descriptorJson)
    {
        RequireCommandDescription(descriptorJson);
        int session = _session;
        return TrackPreparedResource(session, await WebGpuImports.CreateComputePipelineAsync(session, descriptorJson));
    }

    public int PrepareCommands(string descriptorJson)
    {
        RequireCommandDescription(descriptorJson);
        return Track(WebGpuImports.PrepareCommands(_session, descriptorJson));
    }

    public void SubmitPreparedCommands(int commandsHandle)
    {
        RequireReady();
        if (!_resources.Contains(commandsHandle))
            throw new InvalidOperationException("Commands must belong to this WebGPU renderer.");
        RequireStandaloneSubmissionBoundary();
        bool presentsCanvas = WebGpuImports.SubmitPreparedCommands(_session, commandsHandle);
        if (presentsCanvas)
            SetField(ref _submittedFrame, true);
    }

    private void RequireCommandDescription(string descriptorJson)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(descriptorJson);
        if (descriptorJson.Length is 0 or > 262144)
            throw new ArgumentOutOfRangeException(nameof(descriptorJson), "GPU descriptions must contain 1 to 262144 characters.");
    }

    private int TrackPreparedResource(int session, int handle)
    {
        RequireReady();
        if (_session != session)
            throw new InvalidOperationException("Renderer changed while the GPU resource was being prepared.");
        return Track(handle);
    }
}
