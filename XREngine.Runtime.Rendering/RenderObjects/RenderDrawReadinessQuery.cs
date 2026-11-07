namespace XREngine.Rendering;

/// <summary>
/// One draw readiness question for <see cref="IRenderPreparationState.QueryDrawReadiness"/>: a material and the view whose
/// render passes must be able to draw it.
/// </summary>
/// <param name="Material">The material of the draw, or null for the material of the renderer.</param>
/// <param name="View">
/// The render pipeline instance of the view that must draw it, or null for no view filter. A backend whose pipelines depend
/// on the pass checks only the passes of this view, so another view or a shadow pass does not decide the result.
/// </param>
/// <param name="Stereo">True when the object is the stereo (headset) version of the renderer, so only stereo passes count.</param>
public readonly record struct RenderDrawReadinessQuery(XRMaterial? Material, XRRenderPipelineInstance? View, bool Stereo);
