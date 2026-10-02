namespace XREngine.Scene.Transforms;

/// <summary>Allocation-free cumulative work counters and timings for the most recent passes.</summary>
public readonly record struct TransformHierarchyCounters(
    int Registered, long DirtyLocal, long DirtyWorld, long Propagated,
    long RenderRecordsPublished, long EventsRaised,
    long PropagationAllocatedBytes, long PublicationAllocatedBytes,
    double PropagationMilliseconds, double PublicationMilliseconds);
