namespace XREngine.Rendering;

/// <summary>
/// Cumulative times of the light collection in the collect event.
/// </summary>
/// <param name="Calls">The number of light collections.</param>
/// <param name="PlanningMilliseconds">The shadow planning before the lights collect their views.</param>
/// <param name="ShadowCollectMilliseconds">
/// The shadow view collection of each light. The primary shadow, cascade and local shadow view times are part of this time.
/// </param>
public readonly record struct CollectVisibleLightStageTimings(
    long Calls,
    double PlanningMilliseconds,
    double ShadowCollectMilliseconds);
