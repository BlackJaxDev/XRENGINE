namespace XREngine;

/// <summary>
/// Derivative strategies used by the engine.
/// </summary>
public enum ERvcDerivativeStrategy
{
    /// <summary>
    /// Analytic derivative from visibility gradients.
    /// </summary>
    AnalyticFromVisibilityGradients,
    /// <summary>
    /// Fine derivative fallback strategy.
    /// </summary>
    FineDerivativeFallback,
    /// <summary>
    /// Material-provided derivative strategy.
    /// </summary>
    MaterialProvided,
}
