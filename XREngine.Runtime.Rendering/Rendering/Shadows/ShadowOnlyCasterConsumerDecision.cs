namespace XREngine.Rendering.Shadows;

/// <summary>
/// The last decision of the directional shadow atlas planning on whether shadow-only casters make the primary
/// directional shadow views collect.
/// </summary>
/// <remarks>
/// The planning records the decision only when the directional shadow atlas is enabled. When
/// <paramref name="ZeroReadbackStrategy"/> is false, the decision is false, and the planning does not read the census.
/// </remarks>
/// <param name="HasDecision">Whether the planning recorded a decision. When false, the other values are default.</param>
/// <param name="PlanFrameId">The shadow atlas frame ID of the planning.</param>
/// <param name="ShadowOnlyCasters">Whether the primary directional shadow views must collect for shadow-only casters.</param>
/// <param name="CameraSetChanged">Whether the main camera set or a camera layer mask changed since the previous planning.</param>
/// <param name="ZeroReadbackStrategy">Whether the mesh submission strategy was a zero-readback GPU strategy.</param>
/// <param name="SceneMayHaveShadowOnlyCasters">The scene census result that the planning read.</param>
public readonly record struct ShadowOnlyCasterConsumerDecision(
    bool HasDecision,
    ulong PlanFrameId,
    bool ShadowOnlyCasters,
    bool CameraSetChanged,
    bool ZeroReadbackStrategy,
    bool SceneMayHaveShadowOnlyCasters);
