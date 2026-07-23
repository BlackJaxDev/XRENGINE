namespace XREngine.Rendering;

/// <summary>
/// Defines how native query slots project to an engine-level result.
/// </summary>
public enum ERenderQueryAggregation
{
    Scalar,
    AnyNonZero,
    Sum,
    PerView,
    TimestampDelta,
    ProviderDefined,
}
