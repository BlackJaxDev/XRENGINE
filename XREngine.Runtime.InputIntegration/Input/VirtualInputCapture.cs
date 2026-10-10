using System.Numerics;
using XREngine.Components;

namespace XREngine.Runtime.InputIntegration;

/// <summary>One controller-owned contact; negative identity is reserved for the physical mouse.</summary>
internal struct VirtualInputCapture
{
    public int PointerId;
    public UICanvasInputComponent? Canvas;
    public UIVirtualInputComponent? Control;
    public Vector2 Position;
    public ushort BeginButtonMask;
    public bool BeganThisFrame;
}
