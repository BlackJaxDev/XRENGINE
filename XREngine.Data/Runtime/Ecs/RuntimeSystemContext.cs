using XREngine.Data.Runtime.Memory;

namespace XREngine.Data.Runtime.Ecs;

public ref struct RuntimeSystemContext
{
    public RuntimeSystemContext(RuntimeEntityWorld world, FrameScratchAllocator scratch)
        : this(world, scratch, string.Empty, default, 0L)
    {
    }

    public RuntimeSystemContext(
        RuntimeEntityWorld world,
        FrameScratchAllocator scratch,
        string systemName,
        RuntimeSystemPhase phase,
        long allocationBudgetBytes)
    {
        World = world;
        Scratch = scratch;
        SystemName = systemName;
        Phase = phase;
        AllocationBudgetBytes = allocationBudgetBytes;
    }

    public RuntimeEntityWorld World { get; }
    public FrameScratchAllocator Scratch { get; }
    public string SystemName { get; }
    public RuntimeSystemPhase Phase { get; }
    public long AllocationBudgetBytes { get; }
}
