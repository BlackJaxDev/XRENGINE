namespace XREngine.Rendering;

/// <summary>
/// Raised when a visibility identifier cannot be represented without truncation.
/// </summary>
public sealed class AdvancedVisibilityPayloadOverflowException(
    EAdvancedVisibilityPayloadOverflow overflow,
    string message)
    : InvalidOperationException(message)
{
    public EAdvancedVisibilityPayloadOverflow Overflow { get; } = overflow;
}
