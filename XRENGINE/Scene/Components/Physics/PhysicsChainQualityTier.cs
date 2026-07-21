namespace XREngine.Components;

/// <summary>
/// Explicit simulation cadence policy for a physics chain.
/// </summary>
public enum PhysicsChainQualityTier
{
    Strict,
    Hz30,
    Hz15,
    Hz7_5,
    Sleep,
    Automatic,
}
