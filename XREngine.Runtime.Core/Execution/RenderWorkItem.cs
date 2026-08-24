namespace XREngine.Execution;

/// <summary>
/// Compact renderer-neutral description of one sealed render preparation or
/// recording range. Native backend state is resolved from the executing lane.
/// </summary>
public readonly record struct RenderWorkItem(
    int OperationKind,
    int SourceStart,
    int SourceCount,
    int PrerequisiteCount = 0,
    int DependentStart = 0,
    int DependentCount = 0,
    int PreferredLane = -1,
    int EstimatedCost = 1)
{
    /// <summary>
    /// Marks work as migratable before it acquires a lane-owned native arena.
    /// </summary>
    public const int AnyLane = -1;
}
