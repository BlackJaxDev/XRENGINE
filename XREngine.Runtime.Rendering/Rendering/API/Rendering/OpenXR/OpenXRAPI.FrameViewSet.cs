using System.Numerics;
using Silk.NET.OpenXR;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private readonly RenderFrameViewDescriptor[] _openXrFrameViewDescriptors = new RenderFrameViewDescriptor[RenderFrameViewSet.MaxViewCount];
    private readonly Matrix4x4[] _openXrPublishedPreviousViewProjection = new Matrix4x4[RenderFrameViewSet.MaxViewCount];

    /// <summary>
    /// Publishes exact runtime-located OpenXR views after pose location and before visibility generation.
    /// </summary>
    private void PublishLocatedOpenXrFrameViewSet()
    {
        int count = checked((int)Math.Min(_viewCount, (uint)RenderFrameViewSet.MaxViewCount));
        if (count == 0)
            return;

        var builder = new RenderFrameViewSetBuilder(_openXrFrameViewDescriptors);
        for (int i = 0; i < count; i++)
        {
            uint viewIndex = (uint)i;
            if (!TryGetOpenXrViewPoseAndFov(viewIndex, OpenXrPoseTiming.Predicted, out Matrix4x4 localPose, out var fov))
                return;

            XRCamera? eyeCamera = GetOpenXrEyeCamera(viewIndex);
            float nearZ = eyeCamera?.NearZ ?? 0.01f;
            float farZ = eyeCamera?.FarZ ?? 1000.0f;
            Matrix4x4 worldMatrix = localPose;
            if (eyeCamera is not null && TryGetAppVrRigLocomotionRenderMatrix(eyeCamera, out Matrix4x4 rootRender))
                worldMatrix *= rootRender;
            if (!Matrix4x4.Invert(worldMatrix, out Matrix4x4 viewMatrix))
                return;

            Matrix4x4 projection = CreateLocatedOpenXrProjection(fov.Left, fov.Right, fov.Down, fov.Up, nearZ, farZ);
            Matrix4x4 viewProjection = viewMatrix * projection;
            Matrix4x4 previous = _openXrPublishedPreviousViewProjection[i];
            if (previous == default)
                previous = viewProjection;

            EVrOutputViewKind kind = ResolveOpenXrRvcViewKind(viewIndex);
            uint parentId = kind switch
            {
                EVrOutputViewKind.LeftInset => 0u,
                EVrOutputViewKind.RightInset => 1u,
                _ => RenderFrameViewDescriptor.InvalidViewId,
            };
            bool parentContains = parentId != RenderFrameViewDescriptor.InvalidViewId &&
                ValidateLocatedOpenXrParentContainment((int)parentId, i, localPose);
            Vector3 position = worldMatrix.Translation;
            Vector3 forward = Vector3.Normalize(new Vector3(-worldMatrix.M31, -worldMatrix.M32, -worldMatrix.M33));
            uint width = Math.Max(1u, _swapchainWidths[i]);
            uint height = Math.Max(1u, _swapchainHeights[i]);
            builder.Add(new RenderFrameViewDescriptor(
                0u,
                kind,
                parentId,
                VisibilityGroupIndex: 0,
                OpenXrViewIndex: i,
                OutputLayer: 0u,
                RenderFrameViewRect.FromSize(width, height),
                viewMatrix,
                projection,
                previous,
                CreateOpenXrEyeFoveationContext(viewIndex),
                GetOpenXrViewDebugName(kind),
                Target: default,
                HistoryKey: GetOpenXrHistoryKey(kind),
                PredictedDisplayTime: _frameState.PredictedDisplayTime,
                CameraPositionAndNear: new Vector4(position, nearZ),
                CameraForwardAndFar: new Vector4(forward, farZ),
                ParentContainsView: parentContains,
                DepthZeroToOne: true));
            _openXrPublishedPreviousViewProjection[i] = viewProjection;
        }

        RenderFrameViewSetPublication.Publish(
            RuntimeEngine.Rendering.State.RenderFrameId,
            builder.Build(
            RuntimeRenderingHostServices.Current.VrViewRenderMode,
            EVrVisibilityPolicy.SharedFrameViewSet,
            visibilityGroupCount: 1,
            "Located OpenXR views"));
    }

    private bool ValidateLocatedOpenXrParentContainment(int parentIndex, int childIndex, in Matrix4x4 childPose)
    {
        if ((uint)parentIndex >= _viewCount || (uint)childIndex >= _viewCount)
            return false;
        if (!TryGetOpenXrViewPoseAndFov((uint)parentIndex, OpenXrPoseTiming.Predicted, out Matrix4x4 parentPose, out var parentFov))
            return false;
        if (!MatricesNearlyEqual(parentPose, childPose, 0.0005f))
            return false;

        View child = _views[childIndex];
        return child.Fov.AngleLeft >= parentFov.Left &&
            child.Fov.AngleRight <= parentFov.Right &&
            child.Fov.AngleDown >= parentFov.Down &&
            child.Fov.AngleUp <= parentFov.Up;
    }

    private static Matrix4x4 CreateLocatedOpenXrProjection(
        float leftAngle,
        float rightAngle,
        float downAngle,
        float upAngle,
        float nearZ,
        float farZ)
    {
        nearZ = Math.Max(nearZ, 0.001f);
        float left = nearZ * MathF.Tan(leftAngle);
        float right = nearZ * MathF.Tan(rightAngle);
        float bottom = nearZ * MathF.Tan(downAngle);
        float top = nearZ * MathF.Tan(upAngle);
        return Matrix4x4.CreatePerspectiveOffCenter(left, right, bottom, top, nearZ, farZ);
    }

    private static bool MatricesNearlyEqual(in Matrix4x4 a, in Matrix4x4 b, float epsilon)
        => MathF.Abs(a.M11 - b.M11) <= epsilon && MathF.Abs(a.M12 - b.M12) <= epsilon &&
           MathF.Abs(a.M13 - b.M13) <= epsilon && MathF.Abs(a.M14 - b.M14) <= epsilon &&
           MathF.Abs(a.M21 - b.M21) <= epsilon && MathF.Abs(a.M22 - b.M22) <= epsilon &&
           MathF.Abs(a.M23 - b.M23) <= epsilon && MathF.Abs(a.M24 - b.M24) <= epsilon &&
           MathF.Abs(a.M31 - b.M31) <= epsilon && MathF.Abs(a.M32 - b.M32) <= epsilon &&
           MathF.Abs(a.M33 - b.M33) <= epsilon && MathF.Abs(a.M34 - b.M34) <= epsilon &&
           MathF.Abs(a.M41 - b.M41) <= epsilon && MathF.Abs(a.M42 - b.M42) <= epsilon &&
           MathF.Abs(a.M43 - b.M43) <= epsilon && MathF.Abs(a.M44 - b.M44) <= epsilon;

    private static string GetOpenXrViewDebugName(EVrOutputViewKind kind)
        => kind switch
        {
            EVrOutputViewKind.LeftEye => "OpenXR LeftEye",
            EVrOutputViewKind.RightEye => "OpenXR RightEye",
            EVrOutputViewKind.LeftWide => "OpenXR LeftWide",
            EVrOutputViewKind.RightWide => "OpenXR RightWide",
            EVrOutputViewKind.LeftInset => "OpenXR LeftInset",
            EVrOutputViewKind.RightInset => "OpenXR RightInset",
            _ => "OpenXR View",
        };
    private static ulong GetOpenXrHistoryKey(EVrOutputViewKind kind)
        => 0x58525F0000000000UL | (uint)kind + 1UL;
}