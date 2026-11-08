using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Cutscenes;

/// <summary>
/// Asks the backend draw readiness of a material on a renderer in the passes of a view. It returns false, and the caller
/// keeps its last result, when it cannot ask now (for example a render-thread job drain with no current renderer).
/// <see cref="CutsceneBackendProgramPrewarmer"/> calls it on the render thread. The default is
/// <see cref="XRMeshRenderer.QueryDrawReadiness"/>. Tests use a fake.
/// </summary>
/// <param name="renderer">The renderer of the draw.</param>
/// <param name="material">The material of the draw.</param>
/// <param name="renderOptionsOverride">The render options override of the renderable, or null.</param>
/// <param name="view">The render pipeline instance of the view, or null for no view filter.</param>
/// <param name="state">The state, when the probe asked.</param>
/// <param name="reason">The reason of the state, for diagnostics.</param>
internal delegate bool CutsceneDrawReadinessProbe(
    XRMeshRenderer renderer,
    XRMaterial material,
    RenderingParameters? renderOptionsOverride,
    XRRenderPipelineInstance? view,
    out ERenderDrawReadiness state,
    out string reason);
