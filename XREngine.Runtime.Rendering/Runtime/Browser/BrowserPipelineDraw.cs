using System.Numerics;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>A retained draw shared by direct and indirect submission. Bounds use the canonical world-space GPU scene record.</summary>
public readonly record struct BrowserPipelineDraw(
    BrowserResourceHandle Mesh, BrowserResourceHandle Material, string AlphaMode,
    int ViewportX, int ViewportY, int ViewportWidth, int ViewportHeight,
    int FirstIndex, int IndexCount, Matrix4x4 Model, Matrix4x4 Mvp,
    float ViewDepth, bool CastShadow = true, bool ShadowOnly = false)
{
    public BoundsGpu WorldBounds { get; init; }
    public Matrix4x4 ViewProjection { get; init; }
    /// <summary>Keep uncertain deformation bounds visible until a conservative envelope is available.</summary>
    public bool DisableCulling { get; init; }
    /// <summary>Explicit static opaque depth contributor, excluded from Hi-Z rejection of candidates.</summary>
    public bool Occluder { get; init; }
}
