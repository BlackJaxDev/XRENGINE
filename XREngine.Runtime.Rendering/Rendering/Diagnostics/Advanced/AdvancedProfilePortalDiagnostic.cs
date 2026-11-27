namespace XREngine.Rendering;

/// <summary>
/// Per-frame scheduling counters for in-place stencil portal and mirror sub-views.
/// Stored as a value type so frame authoring records diagnostics with zero heap allocations.
/// </summary>
public readonly record struct AdvancedProfilePortalDiagnostic(
    ulong FrameId,
    int PrimaryViewCount,
    int ScheduledCount,
    int MaxScheduledDepth,
    int RejectedBudgetCount,
    int RejectedCulledCount,
    int RejectedCycleCount,
    uint FroxelSlotBudget);
