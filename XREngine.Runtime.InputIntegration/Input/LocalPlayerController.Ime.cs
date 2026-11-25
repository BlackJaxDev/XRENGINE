using XREngine.Components;
using XREngine.Input;
using XREngine.Rendering;

namespace XREngine.Runtime.InputIntegration;

public partial class LocalPlayerController
{
    private void DispatchImeSnapshot(ReadOnlySpan<WindowImeEvent> events)
    {
        if (events.IsEmpty || _controlledPawn is not IRuntimeInputControllablePawn pawn)
            return;
        foreach (XRComponent component in pawn.LinkedUiInputComponents)
        {
            if (component is UICanvasInputComponent canvas)
                canvas.DispatchImeEvents(events);
        }
    }
}
