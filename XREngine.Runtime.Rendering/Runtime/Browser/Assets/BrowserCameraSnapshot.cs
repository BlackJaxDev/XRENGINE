using System.Numerics;

namespace XREngine.Rendering;

/// <summary>Row-vector render-space view and WebGPU zero-to-one depth projection.</summary>
public readonly partial record struct BrowserCameraSnapshot(Matrix4x4 View, Matrix4x4 Projection)
{
    public Matrix4x4 ViewProjection => View * Projection;
}
