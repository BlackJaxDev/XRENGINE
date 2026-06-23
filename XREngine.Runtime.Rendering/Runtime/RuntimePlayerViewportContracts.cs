using XREngine.Components;

namespace XREngine.Rendering;

public interface IRuntimeLocalPlayerViewport
{
    WindowInputSnapshot InputSnapshot { get; }
    object? GetThreadAffinedDeviceSourceForBinding();
    void RefreshControlledPawnCamera(XRComponent? controlledPawnComponent);
}

public interface IRuntimeFocusedInteractable
{
}
