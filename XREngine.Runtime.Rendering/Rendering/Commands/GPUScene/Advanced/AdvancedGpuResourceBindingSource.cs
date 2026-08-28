using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Renderer-neutral source for one canonical material texture slot. Native
/// descriptor indices and backend residency are deliberately excluded.
/// </summary>
public readonly record struct AdvancedGpuResourceBindingSource(
    XRTexture? Texture,
    AdvancedTextureRecord TextureRecord,
    AdvancedSamplerRecord SamplerRecord,
    EAdvancedResourceFallback Fallback)
{
    /// <summary>Creates an unbound slot with an explicit shader fallback.</summary>
    public static AdvancedGpuResourceBindingSource Missing(EAdvancedResourceFallback fallback)
        => new(null, default, default, fallback);
}
