namespace XREngine.Rendering.WebGPU;

/// <summary>Returns a pending host; browser startup supplies readiness asynchronously.</summary>
public sealed class WebGpuRendererBackendFactory : IRendererBackendFactory
{
    private readonly WebGpuRendererBackendModule _module;

    internal WebGpuRendererBackendFactory(WebGpuRendererBackendModule module) => _module = module;

    public IRuntimeRendererHost Create(in RendererBackendCreateContext context) => _module.Create(context);
}
