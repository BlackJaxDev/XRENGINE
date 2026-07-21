using System.Numerics;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

/// <summary>
/// Captures mutable camera and target state once into the immutable logical view contract.
/// </summary>
public static class RenderFrameViewSetCapture
{
    [InlineArray(RenderFrameViewSet.MaxViewCount)]
    private struct FrameViewBuffer
    {
        private RenderFrameViewDescriptor _element0;
    }
    public const ulong MonoHistoryKey = 0x5845525F4D4F4E4FUL;
    public const ulong LeftEyeHistoryKey = 0x5845525F4C454654UL;
    public const ulong RightEyeHistoryKey = 0x5845525F52474854UL;

    public static RenderFrameViewSet Capture(IRuntimeRenderCommandExecutionState state)
    {
        IRuntimeRenderCamera camera = state.RenderingCamera ?? state.SceneCamera
            ?? throw new InvalidOperationException("A frame view set requires an active rendering camera.");
        IRuntimeRenderCamera? rightCamera = state.StereoPass ? state.StereoRightEyeCamera : null;
        uint width = (uint)Math.Max(1, state.WindowViewport?.InternalWidth ?? state.WindowViewport?.Width ?? 1);
        uint height = (uint)Math.Max(1, state.WindowViewport?.InternalHeight ?? state.WindowViewport?.Height ?? 1);

        FrameViewBuffer storage = default;
        var builder = new RenderFrameViewSetBuilder(storage);
        if (rightCamera is null)
        {
            builder.Add(CaptureView(camera, EVrOutputViewKind.DesktopEditor, 0u, width, height, MonoHistoryKey));
            return builder.Build(EVrViewRenderMode.SequentialViews, EVrVisibilityPolicy.PerView, 1, "Desktop frame views");
        }

        builder.Add(CaptureView(camera, EVrOutputViewKind.LeftEye, 0u, width, height, LeftEyeHistoryKey));
        builder.Add(CaptureView(rightCamera, EVrOutputViewKind.RightEye, 1u, width, height, RightEyeHistoryKey));
        return builder.Build(
            RuntimeRenderingHostServices.Current.VrViewRenderMode,
            EVrVisibilityPolicy.SharedFrameViewSet,
            1,
            "Stereo frame views");
    }

    private static string GetViewDebugName(EVrOutputViewKind kind)
        => kind switch
        {
            EVrOutputViewKind.LeftEye => "Left eye",
            EVrOutputViewKind.RightEye => "Right eye",
            EVrOutputViewKind.DesktopEditor => "Desktop editor",
            _ => "Frame view",
        };
    private static RenderFrameViewDescriptor CaptureView(
        IRuntimeRenderCamera camera,
        EVrOutputViewKind kind,
        uint outputLayer,
        uint width,
        uint height,
        ulong historyKey)
    {
        Matrix4x4 view = camera.Transform.InverseRenderMatrix;
        Matrix4x4 projection = camera.ProjectionMatrix;
        Matrix4x4 viewProjection = view * projection;
        return new RenderFrameViewDescriptor(
            0u,
            kind,
            RenderFrameViewDescriptor.InvalidViewId,
            0,
            -1,
            outputLayer,
            RenderFrameViewRect.FromSize(width, height),
            view,
            projection,
            viewProjection,
            ViewFoveationContext.Off(),
            GetViewDebugName(kind),
            historyKey,
            0,
            new Vector4(camera.Transform.RenderTranslation, camera.NearZ),
            new Vector4(camera.Transform.RenderForward, camera.FarZ));
    }
}