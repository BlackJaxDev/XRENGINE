namespace XREngine.Rendering.Shadows;

/// <summary>How a terminal output's shadow dependency was satisfied.</summary>
public enum EShadowAtlasReadinessSelection
{
    NotRequired = 0,
    ExactCurrentContent = 1,
    DeclaredResidentGpuFallback = 2,
    Failed = 3,
}
