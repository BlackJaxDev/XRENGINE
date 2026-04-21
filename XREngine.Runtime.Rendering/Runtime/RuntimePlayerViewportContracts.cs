using XREngine.Components;

namespace XREngine.Rendering;

public interface IRuntimeLocalPlayerViewport
{
    object? InputContext { get; }
    void RefreshControlledPawnCamera(XRComponent? controlledPawnComponent);
}

public interface IRuntimeFocusedInteractable
{
}