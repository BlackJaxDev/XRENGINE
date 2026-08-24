namespace XREngine.Rendering;

/// <summary>
/// Small per-caster envelope paired with a shared layered-shadow pass snapshot.
/// Directional bits address compact cascade slots; point bits address logical
/// cubemap faces so legacy and atlas shaders use the same mask contract.
/// </summary>
public readonly record struct LayeredShadowCasterRelevance(
    int DirectionalCascadeTargetMask,
    int PointLightShadowFaceMask)
{
    public static LayeredShadowCasterRelevance FromPassState(
        in LayeredShadowUniformState state)
        => new(
            state.DirectionalCascadeTargetMask,
            state.PointLightShadowFaceMask);
}
