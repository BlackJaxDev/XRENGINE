namespace XREngine.Rendering;

/// <summary>
/// Validated lifecycle of one allocated query range.
/// </summary>
public enum ERenderQuerySlotState
{
    Allocated,
    ResetRecorded,
    Recording,
    Ended,
    Submitted,
    Available,
    Recyclable,
    Invalid,
}
