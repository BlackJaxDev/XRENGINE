using System.Numerics;

namespace XREngine;

public readonly record struct ViewRenderContext(
    EVrOutputViewKind Kind,
    uint ViewIndex,
    int VisibilityGroupIndex,
    Matrix4x4 ViewMatrix,
    Matrix4x4 ProjectionMatrix,
    ViewFoveationContext Foveation)
{
    public Matrix4x4 ViewProjectionMatrix => ViewMatrix * ProjectionMatrix;

    public ViewRenderContext WithVisibilityGroup(int visibilityGroupIndex)
        => new(Kind, ViewIndex, visibilityGroupIndex, ViewMatrix, ProjectionMatrix, Foveation);

    public ViewRenderContext WithKindAndIndex(EVrOutputViewKind kind, uint viewIndex)
        => new(kind, viewIndex, VisibilityGroupIndex, ViewMatrix, ProjectionMatrix, Foveation);
}
