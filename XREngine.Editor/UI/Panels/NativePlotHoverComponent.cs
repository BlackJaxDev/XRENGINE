using XREngine.Rendering.UI;

namespace XREngine.Editor;

/// <summary>
/// A passive plot input that reports pointer moves and leaves through the base
/// <see cref="UIInteractableComponent.MouseMove"/> and <see cref="UIInteractableComponent.MouseOverlapLeave"/>
/// events. It takes no focus and has no click action.
/// </summary>
internal sealed class NativePlotHoverComponent : UIInteractableComponent
{
    public NativePlotHoverComponent()
    {
        RegisterInputsOnFocus = false;
        IsTabStop = false;
        NeedsMouseMove = true;
        AccessibilityLabel = "Plot";
    }
}
