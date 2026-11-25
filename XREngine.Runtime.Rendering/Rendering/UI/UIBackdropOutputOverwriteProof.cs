using XREngine.Data.Geometry;

namespace XREngine.Rendering.UI;

/// <summary>Identifies one checked replacement of a window backbuffer region.</summary>
public readonly record struct UIBackdropOutputOverwriteProof(
    AbstractRenderer Renderer,
    XRWindow Window,
    long BackendGeneration,
    long WindowGlContextGeneration,
    ulong DispatchToken,
    BoundingRectangle Region);
