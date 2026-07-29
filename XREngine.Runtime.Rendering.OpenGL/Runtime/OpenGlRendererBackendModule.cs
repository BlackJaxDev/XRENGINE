namespace XREngine.Rendering.OpenGL;

using XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Statically registers the OpenGL renderer backend without requiring the stable rendering
/// kernel to reference the concrete backend assembly.
/// </summary>
public static class OpenGlRendererBackendModule
{
    /// <summary>
    /// Registers the statically linked OpenGL renderer and returns its catalog lease.
    /// </summary>
    public static IDisposable Register(IRendererBackendCatalog catalog, Version? version = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        OpenGlRendererBackendModuleEntry module = new(version);
        try
        {
            return catalog.Register(module);
        }
        catch
        {
            module.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates the OpenGL backend registration for composition roots that aggregate leases.
    /// </summary>
    public static RendererBackendRegistration CreateRegistration(Version? version = null)
        => new(new OpenGlRendererBackendModuleEntry(version));
}
