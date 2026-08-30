namespace XREngine.Rendering;

/// <summary>
/// Opaque, renderer-neutral proof that one output owns the single advanced
/// visibility family for a renderer generation. Reservations are sticky and
/// are never reassigned until their renderer generation is retired.
/// </summary>
public readonly record struct AdvancedVisibilityFamilyReservation(
    long BackendGeneration,
    ulong OutputId,
    ulong ReservationId)
{
    public bool IsValid => BackendGeneration > 0 && OutputId != 0 && ReservationId != 0;

    public bool Matches(long backendGeneration, ulong outputId)
        => IsValid && BackendGeneration == backendGeneration && OutputId == outputId;
}
