namespace XREngine.Components;

public readonly record struct PhysicsChainReadbackLimits(
    int MaximumElementsPerFrame,
    int MaximumBytesPerFrame,
    int MaximumLifetimeFrames)
{
    public static PhysicsChainReadbackLimits Default => new(4_096, 4 * 1024 * 1024, 8);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumElementsPerFrame, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumBytesPerFrame, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumLifetimeFrames, 1);
    }
}
