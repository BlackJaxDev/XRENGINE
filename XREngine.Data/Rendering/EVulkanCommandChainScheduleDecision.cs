namespace XREngine.Data.Rendering;

/// <summary>Outcome of the latest command-chain scheduling attempt, independent of frame-counter tracking.</summary>
public enum EVulkanCommandChainScheduleDecision
{
    NotEvaluated,
    Disabled,
    VolatileQueries,
    ReusedSchedule,
    StabilityGuard,
    NoLoweredPackets,
    EmptySchedule,
    BuiltSchedule,
}
