using XREngine.Components;

namespace XREngine.Rendering;

public interface IRuntimeLocalPlayerViewport
{
    WindowInputSnapshot ConsumeInputSnapshot();
    void RequestMouseCapture(bool captured);
    void RefreshControlledPawnCamera(XRComponent? controlledPawnComponent);
}

public interface IRuntimeFocusedInteractable
{
}
