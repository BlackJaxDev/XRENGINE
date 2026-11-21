using XREngine.Data.Runtime.Memory;

namespace XREngine.Data.Runtime.Ecs;

public static class RuntimeSystemRunner
{
    public static void Execute(
        RuntimeEntityWorld world,
        IRuntimeSystem system,
        FrameScratchAllocator scratch,
        ref RuntimeSystemAllocationStats allocationStats)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(scratch);

        long startBytes = GC.GetAllocatedBytesForCurrentThread();
        RuntimeSystemContext context = new(
            world,
            scratch,
            system.GetType().Name,
            system.Phase,
            system.AllocationBudgetBytes);

        system.Execute(ref context);
        allocationStats.AddSample(
            GC.GetAllocatedBytesForCurrentThread() - startBytes,
            system.AllocationBudgetBytes);
    }
}
