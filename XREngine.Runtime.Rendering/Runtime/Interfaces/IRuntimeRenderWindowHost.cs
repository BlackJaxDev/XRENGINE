using Silk.NET.Maths;

namespace XREngine.Rendering;

/// <summary>
/// Host window surface exposed to runtime rendering code.
/// </summary>
public interface IRuntimeRenderWindowHost
{
    int NativeWindowThreadId { get; }
    int RenderOwnerThreadId { get; }
    RuntimeWindowBackendKind WindowBackendKind { get; }
    RuntimeWindowBackendOwnershipInfo WindowBackendOwnership { get; }
    WindowSurfaceSnapshot LatestWindowSurfaceSnapshot { get; }
    WindowEventSnapshot LatestWindowEventSnapshot { get; }
    WindowInputSnapshot LatestWindowInputSnapshot { get; }
    WindowResizeExtents ResizeExtents { get; }
    Vector2D<int> EffectiveFramebufferSize { get; }
    Vector2D<int> EffectiveWindowSize { get; }
}
