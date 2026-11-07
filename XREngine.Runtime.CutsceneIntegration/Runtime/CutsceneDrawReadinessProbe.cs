using XREngine.Rendering;

namespace XREngine.Cutscenes;

/// <summary>
/// Gives the backend draw readiness of a material on a renderer in the passes of a view.
/// <see cref="CutsceneBackendProgramPrewarmer"/> calls it on the render thread. The default is
/// <see cref="XRMeshRenderer.QueryDrawReadiness"/>. Tests use a fake.
/// </summary>
/// <param name="renderer">The renderer of the draw.</param>
/// <param name="material">The material of the draw.</param>
/// <param name="view">The render pipeline instance of the view, or null for no view filter.</param>
/// <param name="reason">The reason of the state, for diagnostics.</param>
internal delegate ERenderDrawReadiness CutsceneDrawReadinessProbe(
    XRMeshRenderer renderer,
    XRMaterial material,
    XRRenderPipelineInstance? view,
    out string reason);
