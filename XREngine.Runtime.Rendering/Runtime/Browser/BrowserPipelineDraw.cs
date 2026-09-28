using System.Numerics;

namespace XREngine.Rendering;

/// <summary>A CPU-direct draw. View depth is positive along camera forward; shadow-only casters bypass color rendering.</summary>
public readonly record struct BrowserPipelineDraw(
    BrowserResourceHandle Mesh, BrowserResourceHandle Material, string AlphaMode,
    int ViewportX, int ViewportY, int ViewportWidth, int ViewportHeight,
    int FirstIndex, int IndexCount, Matrix4x4 Model, Matrix4x4 Mvp,
    float ViewDepth, bool CastShadow = true, bool ShadowOnly = false);
