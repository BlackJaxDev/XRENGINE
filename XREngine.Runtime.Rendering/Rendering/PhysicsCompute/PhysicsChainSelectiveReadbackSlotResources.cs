namespace XREngine.Rendering.Compute;

/// <summary>
/// Persistent GPU resources paired one-to-one with a readback-service staging slot.
/// </summary>
internal sealed class PhysicsChainSelectiveReadbackSlotResources : IDisposable
{
    public XRDataBuffer<PhysicsChainGpuReadbackGatherItem>? Items;
    public XRDataBuffer<uint>? PackedOutput;
    public XRDataBuffer<uint>? MappedStaging;
    public PhysicsChainMappedReadbackStagingSource StagingSource { get; } = new();
    public PhysicsChainGpuReadbackFence Fence { get; } = new();

    public void Dispose()
    {
        StagingSource.Dispose();
        Fence.Dispose();
        Items?.Dispose();
        PackedOutput?.Dispose();
        MappedStaging?.Dispose();
        Items = null;
        PackedOutput = null;
        MappedStaging = null;
    }
}
