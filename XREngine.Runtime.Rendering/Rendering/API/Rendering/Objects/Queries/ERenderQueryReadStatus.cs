namespace XREngine.Rendering;

/// <summary>
/// Describes the outcome of a query read without conflating availability and errors.
/// </summary>
public enum ERenderQueryReadStatus
{
    Ready,
    NotReady,
    Unsupported,
    InvalidState,
    StaleTicket,
    BufferTooSmall,
    BudgetExhausted,
    SubsystemUnavailable,
    DeviceLost,
    ApiError,
}
