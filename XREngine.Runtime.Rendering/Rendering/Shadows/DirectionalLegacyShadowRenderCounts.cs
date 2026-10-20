namespace XREngine.Rendering.Shadows;

/// <summary>
/// Cumulative counts of the legacy directional shadow renders, which render into the dedicated shadow map and the
/// cascade texture arrays instead of the shadow atlas.
/// </summary>
/// <remarks>
/// <paramref name="RenderCalls"/> and <paramref name="PrimaryRenders"/> mirror the per-light
/// <see cref="XREngine.Components.Lights.DirectionalLightComponent.StandaloneShadowRenderRequestCount"/> and
/// <see cref="XREngine.Components.Lights.DirectionalLightComponent.StandaloneShadowRenderPassCount"/>
/// as sums for all directional lights.
/// </remarks>
/// <param name="RenderCalls">The legacy render requests that passed the active and shadow-caster checks.</param>
/// <param name="CollectNow">The legacy renders that collected and swapped the light again on the render thread.</param>
/// <param name="PrimaryRenderAttempts">The legacy primary renders that started.</param>
/// <param name="PrimaryRenders">The legacy primary renders that succeeded.</param>
/// <param name="CascadeRenderCalls">The legacy cascade renders of one cascade source with at least one cascade.</param>
public readonly record struct DirectionalLegacyShadowRenderCounts(
    long RenderCalls,
    long CollectNow,
    long PrimaryRenderAttempts,
    long PrimaryRenders,
    long CascadeRenderCalls);
