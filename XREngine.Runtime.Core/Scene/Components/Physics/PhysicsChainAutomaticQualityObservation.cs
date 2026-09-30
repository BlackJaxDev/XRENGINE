namespace XREngine.Components;

/// <summary>
/// Runtime view relevance sampled for automatic quality selection. Distance is
/// measured in world units and projected size is a normalized viewport-height
/// fraction.
/// </summary>
public readonly record struct PhysicsChainAutomaticQualityObservation(
    float Distance,
    float ProjectedSize,
    bool Visible)
{
    public bool IsValid
        => float.IsFinite(Distance)
            && Distance >= 0.0f
            && float.IsFinite(ProjectedSize)
            && ProjectedSize >= 0.0f;
}
