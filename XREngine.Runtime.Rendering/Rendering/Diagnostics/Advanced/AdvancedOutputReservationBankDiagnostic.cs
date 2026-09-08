namespace XREngine.Rendering;

/// <summary>
/// Detached diagnostic observation of an output bank. Lease counts describe
/// ownership; they do not independently certify GPU completion.
/// </summary>
public readonly record struct AdvancedOutputReservationBankDiagnostic(
    ulong OutputId,
    ulong ReservationId,
    long BankIncarnation,
    EAdvancedOutputReservationBankState State,
    int PlanLeaseCount,
    int RecordedCommandBufferLeaseCount,
    int PendingQueueDomainCount,
    long ManagedActivationBytes,
    string? LastActivationFailure);
