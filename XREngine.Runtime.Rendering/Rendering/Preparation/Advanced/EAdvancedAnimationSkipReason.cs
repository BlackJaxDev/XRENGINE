namespace XREngine.Rendering;

/// <summary>
/// Diagnostic reason a render-only pose was not evaluated this frame.
/// </summary>
public enum EAdvancedAnimationSkipReason : uint
{
    None = 0u,
    Cadence = 1u,
    OutsideVisibilityGrace = 2u,
    NoRenderConsumers = 3u,
    SchedulerCapacity = 4u,
}
