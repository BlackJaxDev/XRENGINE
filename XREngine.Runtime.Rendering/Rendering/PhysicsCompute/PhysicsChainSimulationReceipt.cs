namespace XREngine.Rendering.Compute;

/// <summary>Retains a bounded native outcome independently of output publication.</summary>
internal sealed class PhysicsChainSimulationReceipt
{
    internal readonly List<PhysicsChainSimulationReceiptEntry> Entries = [];
    internal XRGpuFence? Fence;
    internal PhysicsChainGpuOutputPageLease OutputPage;
    internal uint PriorProducerEpoch;
    internal bool OwnsFence;
    internal bool InUse;
}
