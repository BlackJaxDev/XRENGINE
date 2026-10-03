namespace XREngine.Rendering.WebGPU;

/// <summary>Statically registered WebGPU leaf with independently owned renderer sessions.</summary>
public sealed class WebGpuRendererBackendModule : IRendererBackendModule
{
    private readonly HashSet<WebGpuRendererHost> _renderers = [];
    private bool _registered;
    private bool _disposed;

    public WebGpuRendererBackendModule()
    {
        Factory = new WebGpuRendererBackendFactory(this);
    }

    public RendererBackendMetadata Metadata { get; } = new(
        RendererBackendId.WebGPU, RuntimeGraphicsApiKind.WebGPU, "Browser WebGPU",
        new Version(0, 1, 0), RendererBackendCapabilities.BrowserCanvasPresentation,
        RendererBackendReloadLimitations.RequiresRendererTeardown,
        "Stop all canvas sessions before replacing the WebGPU module.",
        generation: 1, targetFramework: "net10.0",
        entryPointTypeName: "XREngine.Rendering.WebGPU.WebGpuRendererBackendModule");

    public IRendererBackendFactory Factory { get; }

    public void OnRegistered()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registered)
            throw new InvalidOperationException("The WebGPU module is already registered.");
        _registered = true;
    }

    public void OnUnregistered()
    {
        _registered = false;
        DisposeRenderers();
    }

    internal WebGpuRendererHost Create(in RendererBackendCreateContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_registered)
            throw new InvalidOperationException("Register the WebGPU module before creating a canvas renderer.");
        if (context.Target is not BrowserCanvasRenderTarget target || context.LinkRendererToWindow)
            throw new NotSupportedException("The WebGPU module requires an explicit browser canvas target without desktop window ownership.");
        if (context.ModuleGeneration != Metadata.Generation)
            throw new InvalidOperationException("The WebGPU factory received an obsolete module generation.");
        target.Validate();
        WebGpuRendererHost renderer = new(target, context.ModuleGeneration, Remove);
        _renderers.Add(renderer);
        return renderer;
    }

    private void Remove(WebGpuRendererHost renderer) => _renderers.Remove(renderer);

    public ValueTask PrepareForUnloadAsync(RendererModuleUnloadContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_renderers.Count != 0)
            throw new InvalidOperationException("Stop every WebGPU canvas renderer before unloading its module.");
        return ValueTask.CompletedTask;
    }

    private void DisposeRenderers()
    {
        // This cold teardown snapshot permits each renderer to unregister itself.
        WebGpuRendererHost[] active = [.. _renderers];
        List<Exception>? errors = null;
        foreach (WebGpuRendererHost renderer in active)
        {
            try { renderer.Dispose(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        if (errors is not null)
            throw new AggregateException("WebGPU renderer teardown failed.", errors);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _registered = false;
        DisposeRenderers();
    }
}
