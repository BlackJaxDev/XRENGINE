using System.Numerics;

namespace XREngine;

public readonly record struct ViewVisibilityFrustumContext(
    Matrix4x4 ViewMatrix,
    Matrix4x4 ProjectionMatrix,
    bool IsConservative,
    bool IncludesFoveatedViews)
{
    public Matrix4x4 ViewProjectionMatrix => ViewMatrix * ProjectionMatrix;
}
