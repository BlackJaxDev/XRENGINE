using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

internal sealed class PointerReentryProbe : UIInteractableComponent
{
    public readonly List<Vector2> Before = [];
    public readonly List<Vector2> After = [];
    public Action? AfterPress;
    public bool ThrowAfterPress;

    public override void PointerPressed(Vector2 localPoint, in UIPointerGeometry geometry)
    {
        Before.Add(geometry.LocalSize);
        AfterPress?.Invoke();
        After.Add(geometry.LocalSize);
        if (ThrowAfterPress)
            throw new InvalidOperationException("Reentrant pointer press");
    }
}
