namespace XREngine.Rendering;

/// <summary>
/// Operational metadata for late and post-visibility draw operations.
/// </summary>
public sealed record AdvancedLatePassMetadata
{
    public EAdvancedLatePassKind Kind { get; init; }
    public bool RequiresSceneColorSnapshot { get; init; }
    public bool ParticipatesInMotionVectors { get; init; }
    public bool WritesDepth { get; init; }
    public bool IsOrderDependent { get; init; }
    public string? UnsupportedReason { get; init; }

    public AdvancedLatePassMetadata(
        EAdvancedLatePassKind kind,
        bool requiresSceneColorSnapshot = false,
        bool participatesInMotionVectors = false,
        bool writesDepth = false,
        bool isOrderDependent = false,
        string? unsupportedReason = null)
    {
        Kind = kind;
        RequiresSceneColorSnapshot = requiresSceneColorSnapshot;
        ParticipatesInMotionVectors = participatesInMotionVectors;
        WritesDepth = writesDepth;
        IsOrderDependent = isOrderDependent;
        UnsupportedReason = unsupportedReason;
    }
}
