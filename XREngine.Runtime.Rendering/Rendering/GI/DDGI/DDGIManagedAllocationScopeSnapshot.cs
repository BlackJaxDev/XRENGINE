namespace XREngine.Rendering.GI.DDGI;

/// <summary>Rolling managed-allocation statistics for one DDGI render command.</summary>
public readonly record struct DDGIManagedAllocationScopeSnapshot(
    string Name,
    long LastBytes,
    double AverageBytes,
    long MaxBytes,
    int Samples,
    int Capacity,
    long OverBudgetCount);
