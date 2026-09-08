namespace XREngine.Rendering;

/// <summary>
/// Opaque, renderer-neutral proof that one output owns one bounded Advanced
/// visibility bank. The incarnation prevents a retired bank from being reused
/// by stale plan or command-buffer work.
/// </summary>
public readonly record struct AdvancedVisibilityFamilyReservation(
    long BackendGeneration,
    ulong OutputId,
    ulong ReservationId,
    long BankIncarnation)
{
    public bool IsValid => BackendGeneration > 0 && OutputId != 0 && ReservationId != 0 &&
        BankIncarnation > 0;

    public bool Matches(long backendGeneration, ulong outputId)
        => IsValid && BackendGeneration == backendGeneration && OutputId == outputId;
}
