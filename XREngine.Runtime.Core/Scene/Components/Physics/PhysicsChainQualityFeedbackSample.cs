namespace XREngine.Components;

/// <summary>
/// Delayed measurement for one automatic-quality chain. The source frame is
/// the world quality frame in which the measured work was submitted.
/// </summary>
public readonly record struct PhysicsChainQualityFeedbackSample(
    long SourceFrame,
    PhysicsChainQualityFeedbackBackend Backend,
    long AutomaticWorkUnits,
    double ElapsedMilliseconds,
    float NormalizedError)
{
    public bool IsValid
        => SourceFrame >= 0L
        && AutomaticWorkUnits > 0L
        && double.IsFinite(ElapsedMilliseconds)
        && ElapsedMilliseconds > 0.0
        && float.IsFinite(NormalizedError)
        && NormalizedError >= 0.0f;
}
