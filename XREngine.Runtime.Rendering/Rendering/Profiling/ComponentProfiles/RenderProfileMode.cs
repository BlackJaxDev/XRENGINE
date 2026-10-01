namespace XREngine.Rendering.Profiling;

/// <summary>Observer policy matching the established whole-frame profiling lanes.</summary>
public enum RenderProfileMode
{
    Diagnostics,
    DevelopmentProfile,
    CleanProfile,
    ReleaseBenchmark,
}
