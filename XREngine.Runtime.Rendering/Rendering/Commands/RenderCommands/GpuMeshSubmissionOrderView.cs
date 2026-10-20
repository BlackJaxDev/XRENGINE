using System.Numerics;

namespace XREngine.Rendering.Commands;

/// <summary>Camera inputs that identify the view used throughout one source-order collection.</summary>
internal readonly record struct GpuMeshSubmissionOrderView(
    ulong CameraIdentity,
    Vector3 CameraPosition,
    Matrix4x4 ViewMatrix,
    Matrix4x4 ProjectionMatrix,
    Matrix4x4 ProjectionMatrixUnjittered,
    uint CullingMask,
    bool ShadowPass)
{
    internal static GpuMeshSubmissionOrderView Capture(XRCamera camera, bool shadowPass)
        => new(camera.RenderIdentity, camera.Transform.RenderTranslation, camera.Transform.InverseRenderMatrix,
            camera.ProjectionMatrix, camera.ProjectionMatrixUnjittered, unchecked((uint)camera.CullingMask.Value), shadowPass);

    internal bool Matches(in RenderFrameViewSelection selection)
    {
        RenderFrameViewDescriptor view = selection.View;
        return CameraIdentity == view.SourceCameraIdentity &&
            CameraPosition == new Vector3(view.CameraPositionAndNear.X, view.CameraPositionAndNear.Y, view.CameraPositionAndNear.Z) &&
            ViewMatrix == view.ViewMatrix && ProjectionMatrix == view.ProjectionMatrix &&
            ProjectionMatrixUnjittered == view.ProjectionMatrixUnjittered && CullingMask == view.CullingLayerMask &&
            ShadowPass == selection.ShadowPass;
    }
}
