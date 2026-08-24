namespace XREngine.Rendering;

public enum EInteractiveResizeDispatchOutcome : byte
{
    Submitted,
    PresentedScaledStale,
    Deferred,
    Faulted,
}
