using XREngine.Rendering;

namespace XREngine.Cutscenes;

/// <summary>
/// Gives the backend draw readiness of a material on a renderer. <see cref="CutsceneBackendProgramPrewarmer"/> calls it on
/// the render thread. The default is <see cref="XRMeshRenderer.QueryDrawReadiness"/>. Tests use a fake.
/// </summary>
/// <param name="renderer">The renderer of the draw.</param>
/// <param name="material">The material of the draw.</param>
/// <param name="reason">The reason of the state, for diagnostics.</param>
internal delegate ERenderDrawReadiness CutsceneDrawReadinessProbe(XRMeshRenderer renderer, XRMaterial material, out string reason);
