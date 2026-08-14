namespace XREngine.Rendering;

public enum EInteractiveResizeDispatchReason : byte
{
    None,
    RuntimeStopped,
    WrongThread,
    FrameAlreadyActive,
    RenderCadenceNotDue,
    VisibilityUnavailable,
    PresentationPackageUnavailable,
    PresentationPackageIncompatible,
    BackendBusy,
    SurfaceUnavailable,
    FrameDidNotAdvance,
    Exception,
}
