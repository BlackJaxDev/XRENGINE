namespace XREngine.Rendering;

/// <summary>
/// Defines legal query-range lifecycle transitions.
/// </summary>
public static class RenderQuerySlotStateMachine
{
    public static bool IsTransitionValid(ERenderQuerySlotState from, ERenderQuerySlotState to)
        => to == ERenderQuerySlotState.Invalid || (from, to) switch
        {
            (ERenderQuerySlotState.Allocated, ERenderQuerySlotState.ResetRecorded) => true,
            (ERenderQuerySlotState.ResetRecorded, ERenderQuerySlotState.Recording) => true,
            (ERenderQuerySlotState.ResetRecorded, ERenderQuerySlotState.Ended) => true,
            (ERenderQuerySlotState.Recording, ERenderQuerySlotState.Ended) => true,
            (ERenderQuerySlotState.Ended, ERenderQuerySlotState.Submitted) => true,
            (ERenderQuerySlotState.Submitted, ERenderQuerySlotState.Available) => true,
            (ERenderQuerySlotState.Available, ERenderQuerySlotState.Recyclable) => true,
            (ERenderQuerySlotState.Recyclable, ERenderQuerySlotState.ResetRecorded) => true,
            _ => false,
        };
}
