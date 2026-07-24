using XREngine.Data.Rendering;

namespace XREngine;

/// <summary>
/// Captures the effective renderer selection and fallback policy.
/// </summary>
public readonly record struct EffectiveCommonRenderSettings(
    ERenderLibrary PreferredBackend,
    RenderBackendFallbackPolicy BackendFallbackPolicy);
