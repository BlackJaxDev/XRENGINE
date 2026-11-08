using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// One draw readiness question for <see cref="IRenderPreparationState.QueryDrawReadiness"/>: a material, the render options
/// of the draw, and the view whose render passes must be able to draw it.
/// </summary>
/// <param name="Material">The material of the draw, or null for the material of the renderer.</param>
/// <param name="RenderOptionsOverride">
/// The render options override of the renderable, or null. The draw uses it in place of the render options of the material,
/// so a backend whose pipelines depend on the draw state uses it too.
/// </param>
/// <param name="View">
/// The render pipeline instance of the view that must draw it, or null for no view filter. A backend whose pipelines depend
/// on the pass checks only the passes of this view, so another view or a shadow pass does not decide the result.
/// </param>
/// <param name="Stereo">True when the object is the stereo (headset) version of the renderer, so only stereo passes count.</param>
public readonly record struct RenderDrawReadinessQuery(
    XRMaterial? Material,
    RenderingParameters? RenderOptionsOverride,
    XRRenderPipelineInstance? View,
    bool Stereo);
