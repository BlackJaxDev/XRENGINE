namespace XREngine.Rendering.Compute;

/// <summary>Owns independent native staging resources for one world's logical slots.</summary>
internal sealed class PhysicsChainSelectiveReadbackWorldResources : IDisposable
{
    internal PhysicsChainSelectiveReadbackWorldResources(int slotCount)
    {
        Slots = new PhysicsChainSelectiveReadbackSlotResources[slotCount];
        for (int index = 0; index < Slots.Length; ++index)
            Slots[index] = new PhysicsChainSelectiveReadbackSlotResources();
    }

    internal PhysicsChainSelectiveReadbackSlotResources[] Slots { get; }

    internal bool CanRetire()
    {
        for (int index = 0; index < Slots.Length; ++index)
            if (!Slots[index].CanRetire())
                return false;
        return true;
    }

    internal void RejectLogicalTransfers()
    {
        for (int index = 0; index < Slots.Length; ++index)
            Slots[index].Fence.Reject();
    }

    public void Dispose()
    {
        for (int index = 0; index < Slots.Length; ++index)
            Slots[index].Dispose();
    }
}
