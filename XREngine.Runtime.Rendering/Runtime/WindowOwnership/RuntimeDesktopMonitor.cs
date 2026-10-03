namespace XREngine.Rendering;

/// <summary>Cached desktop display bounds and scale supplied by the native window owner.</summary>
public readonly record struct RuntimeDesktopMonitor(
    nint PlatformHandle,
    int X,
    int Y,
    int Width,
    int Height,
    int WorkX,
    int WorkY,
    int WorkWidth,
    int WorkHeight,
    float DpiScale);
