using XREngine.Rendering;

namespace XREngine.Browser;

/// <summary>Installs the WebGPU renderer module for all browser canvas sessions.</summary>
internal static class BrowserRendererComposition
{
    private static readonly RendererBackendCatalog Catalog = new();
    private static readonly IDisposable Registration = Catalog.Register(
        BrowserStaticRegistrations.CreateRendererModule(BrowserStaticRegistrations.WebGpuModuleId));

    internal static void Initialize()
    {
        BrowserStaticRegistrations.Initialize();
        _ = Registration;
    }

    /// <summary>Creates a canvas renderer through the shared backend catalog.</summary>
    internal static IBrowserRendererHost CreateRequired(BrowserCanvasRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Initialize();
        IRuntimeRendererHost renderer = Catalog.CreateRequired(RuntimeGraphicsApiKind.WebGPU,
            new RendererBackendCreateContext(target));
        if (renderer.TryGetBackendCapability<IBrowserRendererHost>(out IBrowserRendererHost? browserRenderer) &&
            browserRenderer is not null)
            return browserRenderer;
        (renderer as IDisposable)?.Dispose();
        throw new InvalidOperationException("The registered WebGPU renderer lacks browser canvas resources and packet submission.");
    }
}
