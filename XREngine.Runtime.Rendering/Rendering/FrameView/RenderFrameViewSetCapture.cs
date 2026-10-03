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

    /// <summary>Freezes a pass's projection choice and uniforms without rereading a captured camera.</summary>
    public static RenderFrameViewSelection SelectForDraw(
        IRuntimeRenderCommandExecutionState? state,
        XRCamera camera,
        bool useUnjitteredProjection,
        Vector2 viewportSize,
        float elapsedTime,
        bool requireCapturedView = true)
    {
        if (!float.IsFinite(viewportSize.X) || !float.IsFinite(viewportSize.Y) ||
            viewportSize.X <= 0 || viewportSize.Y <= 0)
            throw new NotSupportedException("RenderFrameView.InvalidViewport: a frozen draw requires positive finite dimensions.");
        RenderFrameViewDescriptor? selected = state?.ScopedFrameView is { } scoped &&
            scoped.SourceCameraIdentity == camera.RenderIdentity ? scoped : null;
        selected ??= FindCapturedView(state, camera);
        if (!selected.HasValue)
        {
            if (requireCapturedView)
                throw new NotSupportedException("RenderFrameView.CapturedCameraMissing: the draw camera has no matching immutable frame view.");
            // Direct UI and capture camera scopes may have no scene view. Capture
            // them at draw entry, before material or renderer callbacks can run.
            selected = CaptureView(camera, EVrOutputViewKind.DesktopEditor, 0,
                checked((uint)viewportSize.X), checked((uint)viewportSize.Y), MonoHistoryKey);
        }
        return new(selected.Value, useUnjitteredProjection, viewportSize, elapsedTime, state?.ShadowPass == true);
    }

    internal static RenderFrameViewDescriptor CaptureScopedView(
        IRuntimeRenderCommandExecutionState state, XRCamera camera, uint width, uint height)
        => FindCapturedView(state, camera) ??
            CaptureView(camera, EVrOutputViewKind.DesktopEditor, 0, width, height, MonoHistoryKey);

    private static RenderFrameViewDescriptor? FindCapturedView(IRuntimeRenderCommandExecutionState? state, XRCamera camera)
    {
        RenderFrameViewDescriptor? selected = null;
        if ((state?.TemporalAuthoringViewSet ?? state?.FrameViewSet) is not { } views)
            return null;
        for (int i = 0; i < views.ViewCount; i++)
        {
            RenderFrameViewDescriptor view = views.GetView(i);
            if (view.SourceCameraIdentity != camera.RenderIdentity) continue;
            if (selected.HasValue)
                throw new NotSupportedException("RenderFrameView.AmbiguousCamera: multiple captured views require explicit view selection.");
            selected = view;
        }
        return selected;
    }

    public static RenderFrameViewSet Capture(
        IRuntimeRenderCommandExecutionState state)
        => Capture(state, frozenDesktopView: null, frozenHistoryCandidate: default);

    internal static RenderFrameViewSet Capture(
        IRuntimeRenderCommandExecutionState state,
        RenderFrameViewDescriptor? frozenDesktopView,
        RenderFrameViewHistoryCandidateToken frozenHistoryCandidate)
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
            RenderFrameViewDescriptor view = frozenDesktopView ??
                CaptureView(
                    camera,
                    EVrOutputViewKind.DesktopEditor,
                    0u,
                    width,
                    height,
                    MonoHistoryKey);
            if (frozenDesktopView.HasValue &&
                state is XRRenderPipelineInstance.RenderingState frozenState)
            {
                frozenState.SetViewHistoryCaptureResult(
                    frozenHistoryCandidate.IsValid,
                    in frozenHistoryCandidate);
            }
            else if (!state.StereoPass && !state.ShadowPass && state.ViewHistorySequenceId != 0UL &&
                state.WindowViewport is XRViewport viewport)
            {
                view = viewport.CaptureDesktopFrameViewHistory(
                    state.ViewHistorySequenceId,
                    state.ViewHistorySourceFrame,
                    camera,
                    state.ViewHistoryPipelineIdentity,
                    state.ViewHistoryAuthoring,
                    view,
                    out bool accepted,
                    out RenderFrameViewHistoryCandidateToken candidate);
                if (state is XRRenderPipelineInstance.RenderingState renderingState)
                    renderingState.SetViewHistoryCaptureResult(accepted, in candidate);
            }

            builder.Add(view);
            return builder.Build(EVrViewRenderMode.SequentialViews, EVrVisibilityPolicy.PerView, 1, "Desktop frame views");
        }

        builder.Add(CaptureView(camera, EVrOutputViewKind.LeftEye, 0u, width, height, LeftEyeHistoryKey));
        builder.Add(CaptureView(rightCamera, EVrOutputViewKind.RightEye, 1u, width, height, RightEyeHistoryKey));
        return builder.Build(
            RuntimeRenderingHostServices.Presentation.VrViewRenderMode,
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
    internal static RenderFrameViewDescriptor CaptureView(
        IRuntimeRenderCamera camera,
        EVrOutputViewKind kind,
        uint outputLayer,
        uint width,
        uint height,
        ulong historyKey)
    {
        Matrix4x4 view = camera.Transform.InverseRenderMatrix;
        Matrix4x4 projection = camera.ProjectionMatrix;
        Matrix4x4 projectionUnjittered = camera.ProjectionMatrixUnjittered;
        Matrix4x4 viewProjection = view * projection;
        RenderFrameViewDescriptor current = new(
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
            new Vector4(camera.Transform.RenderForward, camera.FarZ),
            ReversedDepth: camera.IsReversedDepth,
            ProjectionMatrixUnjittered: projectionUnjittered,
            CurrentJitter: camera.ProjectionJitter)
        {
            SourceCameraIdentity = (camera as XRCamera)?.RenderIdentity ?? 0UL,
            CameraCullingMask = camera is XRCamera sourceCamera ? unchecked((uint)sourceCamera.CullingMask.Value) : uint.MaxValue,
            CameraOrthographicSize = camera is XRCamera { Parameters: XROrthographicCameraParameters orthographic }
                ? new Vector2(orthographic.Width, orthographic.Height) : null,
        };
        return current;
    }
}
