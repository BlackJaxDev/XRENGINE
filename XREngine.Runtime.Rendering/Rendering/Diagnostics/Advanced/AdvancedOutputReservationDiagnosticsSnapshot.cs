namespace XREngine.Rendering;

/// <summary>
/// On-demand backend ownership snapshot. Managed activation bytes count cold
/// allocations on the activating thread, not native VRAM or process memory.
/// </summary>
public sealed record AdvancedOutputReservationDiagnosticsSnapshot(
    long BackendGeneration,
    int Capacity,
    int ActiveCount,
    int RetiringCount,
    int FreeCount,
    int ActivationFailureCount,
    long ManagedActivationBytes,
    int RecordedCommandCapacity,
    int RecordedCommandCount,
    IReadOnlyList<AdvancedOutputReservationBankDiagnostic> Banks);
