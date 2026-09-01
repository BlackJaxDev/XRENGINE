namespace XREngine.Components.Animation;

/// <summary>Numeric Phase 10 gates, expressed in XRENGINE world units and degrees.</summary>
public sealed class HumanoidConformanceTolerances
{
    public float RootTranslationMeters { get; set; } = 0.001f;
    public float RootRotationDegrees { get; set; } = 0.1f;
    public float EndpointMeters { get; set; } = 0.002f;
    public float BoneLocalRotationDegrees { get; set; } = 0.2f;
    public float TenLoopDriftMeters { get; set; } = 0.002f;
    public float TenLoopDriftDegrees { get; set; } = 0.2f;
}
