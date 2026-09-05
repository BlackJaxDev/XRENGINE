using System.Numerics;
using Silk.NET.OpenXR;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private readonly RenderFrameViewDescriptor[] _openXrFrameViewDescriptors = new RenderFrameViewDescriptor[RenderFrameViewSet.MaxViewCount];
    private readonly Matrix4x4[] _openXrCommittedPreviousViewProjection = new Matrix4x4[RenderFrameViewSet.MaxViewCount];
    private readonly Matrix4x4[] _openXrCommittedPreviousViewProjectionUnjittered = new Matrix4x4[RenderFrameViewSet.MaxViewCount];
    private readonly Matrix4x4[] _openXrPendingViewProjection = new Matrix4x4[RenderFrameViewSet.MaxViewCount];
    private readonly Matrix4x4[] _openXrPendingViewProjectionUnjittered = new Matrix4x4[RenderFrameViewSet.MaxViewCount];
    private readonly ulong[] _openXrCommittedCameraHistoryEpochs = new ulong[RenderFrameViewSet.MaxViewCount];
    private readonly ulong[] _openXrPendingCameraHistoryEpochs = new ulong[RenderFrameViewSet.MaxViewCount];
    private readonly XRCamera?[] _openXrCommittedHistoryCameras = new XRCamera?[RenderFrameViewSet.MaxViewCount];
    private readonly XRCamera?[] _openXrPendingHistoryCameras = new XRCamera?[RenderFrameViewSet.MaxViewCount];
    private readonly EVrOutputViewKind[] _openXrCommittedHistoryKinds = new EVrOutputViewKind[RenderFrameViewSet.MaxViewCount];
    private readonly uint[] _openXrCommittedHistoryWidths = new uint[RenderFrameViewSet.MaxViewCount];
    private readonly uint[] _openXrCommittedHistoryHeights = new uint[RenderFrameViewSet.MaxViewCount];
    private readonly Matrix4x4[] _openXrCommittedHistoryProjections = new Matrix4x4[RenderFrameViewSet.MaxViewCount];
    private ulong _openXrViewHistoryEpoch = 1UL;
    private ulong _openXrCommittedViewHistoryEpoch;
    private ulong _openXrPendingViewHistoryEpoch;
    private long _openXrCommittedDisplayTime;
    private long _openXrPendingDisplayTime;
    private int _openXrCommittedFrameNumber;
    private int _openXrPendingViewHistoryFrameNumber;
    private int _openXrCommittedViewCount;
    private int _openXrPendingViewCount;
    private int _openXrHasPendingViewHistory;
    private int _openXrPendingViewHistoryTrackingValid;
    private RenderFrameViewSet _openXrPendingViewSet;

    /// <summary>
    /// Publishes exact runtime-located OpenXR views after pose location and before visibility generation.
    /// </summary>
    private bool TryPublishLocatedOpenXrFrameViewSet()
    {
        int count = checked((int)Math.Min(_viewCount, (uint)RenderFrameViewSet.MaxViewCount));
        if (count == 0)
            return false;
        int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
        long displayTime = _frameState.PredictedDisplayTime;
        if (Volatile.Read(ref _openXrHasPendingViewHistory) != 0 &&
            _openXrPendingViewHistoryFrameNumber == frameNo &&
            _openXrPendingDisplayTime == displayTime)
        {
            RenderFrameViewSetPublication.Publish(
                RuntimeEngine.Rendering.State.RenderFrameId,
                _openXrPendingViewSet);
            return true;
        }

        var builder = new RenderFrameViewSetBuilder(_openXrFrameViewDescriptors);
        bool trackingValid = Volatile.Read(ref _openXrLatestViewTrackingValid) != 0;
        bool committedCompatible = _openXrCommittedViewCount == count &&
            _openXrCommittedFrameNumber + 1 == frameNo &&
            displayTime > _openXrCommittedDisplayTime &&
            _openXrCommittedViewHistoryEpoch == _openXrViewHistoryEpoch;
        for (int i = 0; i < count; i++)
        {
            uint viewIndex = (uint)i;
            if (!TryGetOpenXrViewPoseAndFov(viewIndex, OpenXrPoseTiming.Predicted, out Matrix4x4 localPose, out var fov))
                return FailOpenXrViewPublication(frameNo);

            XRCamera? eyeCamera = GetOpenXrEyeCamera(viewIndex);
            float nearZ = eyeCamera?.NearZ ?? 0.01f;
            float farZ = eyeCamera?.FarZ ?? 1000.0f;
            Matrix4x4 worldMatrix = localPose;
            if (eyeCamera is not null && TryGetAppVrRigLocomotionRenderMatrix(eyeCamera, out Matrix4x4 rootRender))
                worldMatrix *= rootRender;
            if (!Matrix4x4.Invert(worldMatrix, out Matrix4x4 viewMatrix))
                return FailOpenXrViewPublication(frameNo);

            Matrix4x4 projection = CreateLocatedOpenXrProjection(fov.Left, fov.Right, fov.Down, fov.Up, nearZ, farZ);
            Matrix4x4 viewProjection = viewMatrix * projection;
            ulong cameraEpoch = eyeCamera?.TemporalHistoryEpoch ?? 0UL;
            EVrOutputViewKind kind = ResolveOpenXrRvcViewKind(viewIndex);
            uint width = Math.Max(1u, _swapchainWidths[i]);
            uint height = Math.Max(1u, _swapchainHeights[i]);
            bool outputChanged = !committedCompatible ||
                _openXrCommittedHistoryKinds[i] != kind ||
                _openXrCommittedHistoryWidths[i] != width ||
                _openXrCommittedHistoryHeights[i] != height ||
                _openXrCommittedHistoryProjections[i] != projection;
            bool cameraChanged = !ReferenceEquals(
                eyeCamera,
                _openXrCommittedHistoryCameras[i]);
            bool cameraCut = !cameraChanged && cameraEpoch != 0UL &&
                cameraEpoch != _openXrCommittedCameraHistoryEpochs[i];
            bool historyValid = trackingValid && committedCompatible &&
                cameraEpoch != 0UL &&
                !outputChanged && !cameraChanged && !cameraCut &&
                cameraEpoch == _openXrCommittedCameraHistoryEpochs[i] &&
                _openXrCommittedPreviousViewProjection[i] != default &&
                _openXrCommittedPreviousViewProjectionUnjittered[i] != default;
            Matrix4x4 previous = historyValid
                ? _openXrCommittedPreviousViewProjection[i]
                : viewProjection;
            Matrix4x4 previousUnjittered = historyValid
                ? _openXrCommittedPreviousViewProjectionUnjittered[i]
                : viewProjection;

            uint parentId = kind switch
            {
                EVrOutputViewKind.LeftInset => 0u,
                EVrOutputViewKind.RightInset => 1u,
                _ => RenderFrameViewDescriptor.InvalidViewId,
            };
            bool parentContains = parentId != RenderFrameViewDescriptor.InvalidViewId &&
                ValidateLocatedOpenXrParentContainment((int)parentId, i, localPose);
            if (parentId != RenderFrameViewDescriptor.InvalidViewId && !parentContains)
                return FailOpenXrViewPublication(frameNo);
            Vector3 position = worldMatrix.Translation;
            Vector3 forward = Vector3.Normalize(new Vector3(-worldMatrix.M31, -worldMatrix.M32, -worldMatrix.M33));
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
                DepthZeroToOne: true,
                ProjectionMatrixUnjittered: projection,
                HistoryStatus: historyValid
                    ? ERenderFrameViewHistoryStatus.Valid
                    : _openXrCommittedFrameNumber == 0
                        ? ERenderFrameViewHistoryStatus.FirstObservation
                        : !trackingValid
                            ? ERenderFrameViewHistoryStatus.TrackingInvalid
                            : _openXrCommittedFrameNumber + 1 != frameNo
                                ? ERenderFrameViewHistoryStatus.FrameGap
                                : outputChanged
                                    ? ERenderFrameViewHistoryStatus.OutputChanged
                                    : cameraChanged
                                        ? ERenderFrameViewHistoryStatus.CameraChanged
                                        : cameraCut
                                            ? ERenderFrameViewHistoryStatus.CameraCut
                                            : ERenderFrameViewHistoryStatus.FrameGap,
                PreviousViewProjectionMatrixUnjittered: previousUnjittered));
            _openXrPendingViewProjection[i] = viewProjection;
            _openXrPendingViewProjectionUnjittered[i] = viewProjection;
            _openXrPendingCameraHistoryEpochs[i] = cameraEpoch;
            _openXrPendingHistoryCameras[i] = eyeCamera;
        }

        _openXrPendingViewHistoryFrameNumber = frameNo;
        _openXrPendingDisplayTime = displayTime;
        _openXrPendingViewCount = count;
        _openXrPendingViewHistoryEpoch = _openXrViewHistoryEpoch;
        _openXrPendingViewSet = builder.Build(
            RuntimeRenderingHostServices.Presentation.VrViewRenderMode,
            EVrVisibilityPolicy.SharedFrameViewSet,
            visibilityGroupCount: 1,
            "Located OpenXR views");
        Volatile.Write(ref _openXrPendingViewHistoryTrackingValid,
            trackingValid ? 1 : 0);
        Volatile.Write(ref _openXrHasPendingViewHistory, 1);
        RenderFrameViewSetPublication.Publish(
            RuntimeEngine.Rendering.State.RenderFrameId,
            _openXrPendingViewSet);
        return true;
    }

    private bool FailOpenXrViewPublication(int frameNo)
    {
        DiscardPendingOpenXrViewHistory(frameNo);
        RenderFrameViewSetPublication.Clear();
        return false;
    }

    private void CommitOpenXrViewHistory(int frameNo, long displayTime)
    {
        if (Volatile.Read(ref _openXrHasPendingViewHistory) == 0 ||
            _openXrPendingViewHistoryFrameNumber != frameNo ||
            _openXrPendingDisplayTime != displayTime ||
            _openXrPendingViewHistoryEpoch != _openXrViewHistoryEpoch)
            return;
        if (Volatile.Read(ref _openXrPendingViewHistoryTrackingValid) == 0)
        {
            DiscardPendingOpenXrViewHistory(frameNo);
            return;
        }
        int count = _openXrPendingViewCount;
        Array.Copy(_openXrPendingViewProjection, _openXrCommittedPreviousViewProjection, count);
        Array.Copy(_openXrPendingViewProjectionUnjittered, _openXrCommittedPreviousViewProjectionUnjittered, count);
        Array.Copy(_openXrPendingCameraHistoryEpochs, _openXrCommittedCameraHistoryEpochs, count);
        Array.Copy(_openXrPendingHistoryCameras, _openXrCommittedHistoryCameras, count);
        for (int i = 0; i < count; i++)
        {
            RenderFrameViewDescriptor view = _openXrPendingViewSet.GetView(i);
            _openXrCommittedHistoryKinds[i] = view.Kind;
            _openXrCommittedHistoryWidths[i] = view.ViewRect.Width;
            _openXrCommittedHistoryHeights[i] = view.ViewRect.Height;
            _openXrCommittedHistoryProjections[i] = view.ProjectionMatrixUnjittered;
        }
        _openXrCommittedFrameNumber = frameNo;
        _openXrCommittedDisplayTime = displayTime;
        _openXrCommittedViewCount = count;
        _openXrCommittedViewHistoryEpoch = _openXrPendingViewHistoryEpoch;
        Volatile.Write(ref _openXrHasPendingViewHistory, 0);
    }

    private void DiscardPendingOpenXrViewHistory(int frameNo)
    {
        if (Volatile.Read(ref _openXrHasPendingViewHistory) != 0 &&
            _openXrPendingViewHistoryFrameNumber == frameNo)
            Volatile.Write(ref _openXrHasPendingViewHistory, 0);
    }

    private void InvalidateOpenXrViewHistory(bool clearPublication = true)
    {
        unchecked { ++_openXrViewHistoryEpoch; }
        if (_openXrViewHistoryEpoch == 0UL)
            _openXrViewHistoryEpoch = 1UL;
        _openXrCommittedFrameNumber = 0;
        _openXrCommittedDisplayTime = 0;
        _openXrCommittedViewCount = 0;
        _openXrCommittedViewHistoryEpoch = 0UL;
        _openXrPendingViewHistoryFrameNumber = 0;
        _openXrPendingDisplayTime = 0;
        _openXrPendingViewCount = 0;
        _openXrPendingViewHistoryEpoch = 0UL;
        _openXrPendingViewSet = default;
        Array.Clear(_openXrCommittedPreviousViewProjection);
        Array.Clear(_openXrCommittedPreviousViewProjectionUnjittered);
        Array.Clear(_openXrCommittedCameraHistoryEpochs);
        Array.Clear(_openXrCommittedHistoryCameras);
        Array.Clear(_openXrPendingHistoryCameras);
        Array.Clear(_openXrCommittedHistoryKinds);
        Array.Clear(_openXrCommittedHistoryWidths);
        Array.Clear(_openXrCommittedHistoryHeights);
        Array.Clear(_openXrCommittedHistoryProjections);
        Volatile.Write(ref _openXrHasPendingViewHistory, 0);
        Volatile.Write(ref _openXrPendingViewHistoryTrackingValid, 0);
        if (clearPublication)
            RenderFrameViewSetPublication.Clear();
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
