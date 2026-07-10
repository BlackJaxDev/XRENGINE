using System.Numerics;

namespace XREngine;

public readonly record struct RvcFrameViewProjectionDiagnostics(
    uint ViewId,
    int RuntimeViewIndex,
    int ViewportX,
    int ViewportY,
    int ViewportWidth,
    int ViewportHeight,
    Matrix4x4 ViewMatrix,
    Matrix4x4 ProjectionMatrix,
    Matrix4x4 ViewProjectionMatrix,
    Matrix4x4 PreviousViewProjectionMatrix)
{
    public static RvcFrameViewProjectionDiagnostics Empty => new(
        ViewId: 0u,
        RuntimeViewIndex: -1,
        ViewportX: 0,
        ViewportY: 0,
        ViewportWidth: 0,
        ViewportHeight: 0,
        Matrix4x4.Identity,
        Matrix4x4.Identity,
        Matrix4x4.Identity,
        Matrix4x4.Identity);
}
