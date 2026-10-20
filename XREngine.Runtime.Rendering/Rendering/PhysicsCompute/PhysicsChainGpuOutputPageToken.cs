namespace XREngine.Rendering.Compute;

/// <summary>Identifies one retained immutable chain output page.</summary>
public readonly record struct PhysicsChainGpuOutputPageToken(
    uint PageIndexPlusOne,
    uint PageGeneration,
    uint ProducerEpoch)
{
    public bool IsValid => PageIndexPlusOne != 0u && PageGeneration != 0u && ProducerEpoch != 0u;
}
