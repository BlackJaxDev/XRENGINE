namespace XREngine.Data.Runtime.Ecs;

public interface IRuntimeSystem
{
    RuntimeSystemPhase Phase { get; }
    long AllocationBudgetBytes { get; }
    void Execute(ref RuntimeSystemContext context);
}
