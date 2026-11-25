using System.Numerics;

namespace XREngine.Rendering;

/// <summary>Retains the event-time result of one native left-button press.</summary>
public readonly record struct RuntimeDesktopDragTerminal(
    ulong PressSequence,
    RuntimeDesktopDragTerminalKind Kind,
    Vector2 ScreenPosition,
    nint TargetWindowHandle,
    long TimestampTicks,
    Vector2 TargetClientScreenPosition = default,
    Vector2 TargetClientSize = default);
