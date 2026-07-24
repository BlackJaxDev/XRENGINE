namespace XREngine.Rendering.Compute;

/// <summary>
/// Dependency-correct physics-chain solver families addressed by GPU-authored work lists.
/// </summary>
public enum PhysicsChainKernelBucket
{
    ShortLinear = 0,
    BranchedOrLong = 1,
    Count = 2,
}
