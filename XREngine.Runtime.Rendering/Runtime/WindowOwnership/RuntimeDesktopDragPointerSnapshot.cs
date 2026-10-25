using System.Numerics;

namespace XREngine.Rendering;

/// <summary>Copies the native pointer state of one desktop window across threads.</summary>
public readonly record struct RuntimeDesktopDragPointerSnapshot(
    long BackendLifetime,
    nint SourceWindowHandle,
    ulong Sequence,
    long TimestampTicks,
    ulong PressSequence,
    bool LeftButtonDown,
    nint CaptureWindowHandle,
    Vector2 ScreenPosition,
    nint TargetWindowHandle,
    RuntimeDesktopDragTerminal Terminal,
    WindowSurfaceSnapshot Surface,
    Vector2 ClientScreenPosition);
