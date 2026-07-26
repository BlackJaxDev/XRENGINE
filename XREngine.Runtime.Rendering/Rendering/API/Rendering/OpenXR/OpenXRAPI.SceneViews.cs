using Silk.NET.OpenXR;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private const string VrEyeTransformFullName = "XREngine.Scene.Transforms.VREyeTransform";
    private static RuntimeTypeHandle _vrEyeTransformTypeHandle;
    private static bool _hasVrEyeTransformTypeHandle;

    private void EnsureOpenXrViewports(uint width, uint height)
        => EnsureOpenXrViewports(width, height, width, height);

    private void EnsureOpenXrViewports(
        uint leftWidth,
        uint leftHeight,
        uint rightWidth,
        uint rightHeight)
    {
        _openXrLeftViewport ??= CreateOpenXrViewport();
        _openXrRightViewport ??= CreateOpenXrViewport();

        _openXrLeftViewport.Camera = _openXrLeftEyeCamera;
        _openXrRightViewport.Camera = _openXrRightEyeCamera;
        RuntimeEngine.VRState.LeftEyeViewport = _openXrLeftViewport;
        RuntimeEngine.VRState.RightEyeViewport = _openXrRightViewport;

        _openXrLeftViewport.CullWithFrustum = RuntimeEngine.Rendering.Settings.OpenXrCullWithFrustum;
        _openXrRightViewport.CullWithFrustum = RuntimeEngine.Rendering.Settings.OpenXrCullWithFrustum;

        EnsureOpenXrViewportExtent(_openXrLeftViewport, leftWidth, leftHeight);
        EnsureOpenXrViewportExtent(_openXrRightViewport, rightWidth, rightHeight);
    }

    private void EnsureOpenXrStereoViewport(uint width, uint height)
    {
        _openXrStereoViewport ??= new XRViewport(null)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            AllowUIRender = false,
            SetRenderPipelineFromCamera = false,
            AllowAutomaticInternalResolution = true,
            RendersToExternalSwapchainTarget = true
        };

        _openXrStereoViewport.RendersToExternalSwapchainTarget = true;
        _openXrStereoViewport.AllowAutomaticInternalResolution = true;
        _openXrStereoViewport.CullWithFrustum = RuntimeEngine.Rendering.Settings.OpenXrCullWithFrustum;
        _openXrStereoViewport.SetFullScreen();
        if (_openXrStereoViewport.Width == (int)width &&
            _openXrStereoViewport.Height == (int)height)
        {
            return;
        }

        _openXrStereoViewport.Resize(width, height, setInternalResolution: false);
        if (_openXrStereoViewport.InternalWidth <= 0 || _openXrStereoViewport.InternalHeight <= 0)
            _openXrStereoViewport.SetInternalResolution((int)width, (int)height, correctAspect: false);
    }

    private static XRViewport CreateOpenXrViewport()
        => new(null)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            AllowUIRender = false,
            SetRenderPipelineFromCamera = false,
            AllowAutomaticInternalResolution = false,
            RendersToExternalSwapchainTarget = true
        };

    private static void EnsureOpenXrViewportExtent(XRViewport viewport, uint width, uint height)
    {
        if (width > int.MaxValue || height > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"OpenXR viewport extent {width}x{height} exceeds supported dimensions.");
        }

        int widthInt = (int)width;
        int heightInt = (int)height;
        viewport.Window = null;
        viewport.AllowAutomaticInternalResolution = false;
        viewport.SetFullScreen();

        if (viewport.Width != widthInt || viewport.Height != heightInt)
        {
            viewport.Resize(width, height, setInternalResolution: false);
            viewport.SetInternalResolution(widthInt, heightInt, correctAspect: false);
        }
        else if (viewport.InternalWidth != widthInt || viewport.InternalHeight != heightInt)
        {
            viewport.SetInternalResolution(widthInt, heightInt, correctAspect: false);
        }
    }

    private bool EnsureOpenXrEyeCameras(XRCamera baseCamera)
    {
        if (!TryResolveRequiredOpenXrVrRig(
                out XRCamera? leftEyeCamera,
                out XRCamera? rightEyeCamera,
                out _,
                out _,
                out string reason))
        {
            _openXrLeftEyeCamera = null;
            _openXrRightEyeCamera = null;
            UpdateOpenXrEyeSettingsSubscriptions(null, null);
            Debug.RenderingWarningEvery(
                "OpenXR.EyeCameras.NoRequiredVrRig",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Eye cameras unavailable: {0}. No fallback eye cameras are created.",
                reason);
            return false;
        }

        _openXrLeftEyeCamera = leftEyeCamera;
        _openXrRightEyeCamera = rightEyeCamera;
        EnsureOpenXrEyeSettingsOwnership(leftEyeCamera!, rightEyeCamera!);
        CopyCameraCommon(baseCamera, leftEyeCamera!);
        CopyCameraCommon(baseCamera, rightEyeCamera!);
        return true;
    }

    private static void CopyCameraCommon(XRCamera source, XRCamera destination)
    {
        destination.CullingMask = source.CullingMask;
        destination.ShadowCollectMaxDistance = source.ShadowCollectMaxDistance;

        float nearZ = source.Parameters.NearZ;
        float farZ = source.Parameters.FarZ;
        if (destination.Parameters is XROVRCameraParameters vrParameters)
        {
            vrParameters.NearZ = nearZ;
            vrParameters.FarZ = farZ;
            return;
        }

        if (destination.Parameters is not XROpenXRFovCameraParameters openXrParameters)
        {
            destination.Parameters = new XROpenXRFovCameraParameters(nearZ, farZ);
            return;
        }

        openXrParameters.NearZ = nearZ;
        openXrParameters.FarZ = farZ;
    }

    private float UpdateOpenXrEyeCameraFromView(XRCamera camera, uint viewIndex)
    {
        if (!TryGetOpenXrViewPoseAndFov(
                viewIndex,
                OpenXrPoseTiming.Predicted,
                out Matrix4x4 localPose,
                out var fov))
        {
            return 0.0f;
        }

        if (!IsAppVrRigEyeTransform(camera.Transform))
        {
            Debug.RenderingWarningEvery(
                $"OpenXR.CollectPose.NonRigEye.{viewIndex}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Skipping predicted eye pose for view {0}: camera transform is not a VREyeTransform.",
                viewIndex);
            return 0.0f;
        }

        float paddingDegrees = 0.0f;
        if (OpenXrCollectPosePolicy == OpenXrCollectVisiblePosePolicy.PaddedFrustum)
        {
            paddingDegrees = MathF.Max(0.0f, OpenXrCollectFrustumPaddingDegrees);
            float paddingRadians = paddingDegrees * (MathF.PI / 180.0f);
            fov.Left -= paddingRadians;
            fov.Right += paddingRadians;
            fov.Down -= paddingRadians;
            fov.Up += paddingRadians;
        }

        if (camera.Parameters is XROpenXRFovCameraParameters parameters)
            parameters.SetAngles(fov.Left, fov.Right, fov.Up, fov.Down);

        if (TryGetAppVrRigLocomotionRenderMatrix(camera, out Matrix4x4 rootRender))
            camera.Transform.SetRenderMatrix(localPose * rootRender, recalcAllChildRenderMatrices: false);

        return paddingDegrees;
    }

    private static bool IsAppVrRigEyeTransform(TransformBase transform)
    {
        Type transformType = transform.GetType();
        if (_hasVrEyeTransformTypeHandle)
            return transformType.TypeHandle.Equals(_vrEyeTransformTypeHandle);

        for (Type? type = transformType; type is not null; type = type.BaseType)
        {
            if (type.FullName != VrEyeTransformFullName)
                continue;

            _vrEyeTransformTypeHandle = transformType.TypeHandle;
            _hasVrEyeTransformTypeHandle = true;
            return true;
        }

        return false;
    }

    private static bool TryGetAppVrRigLocomotionRenderMatrix(
        XRCamera camera,
        out Matrix4x4 renderMatrix)
    {
        renderMatrix = Matrix4x4.Identity;
        TransformBase transform = camera.Transform;
        if (!IsAppVrRigEyeTransform(transform))
            return false;

        renderMatrix = transform.Parent?.ParentRenderMatrix ?? Matrix4x4.Identity;
        return true;
    }

    private bool TryResolveRequiredOpenXrVrRig(
        out XRCamera? leftEyeCamera,
        out XRCamera? rightEyeCamera,
        out IRuntimeRenderWorld? world,
        out TransformBase? locomotionRoot,
        out string reason)
    {
        var vrInfo = RuntimeEngine.VRState.ViewInformation;
        leftEyeCamera = vrInfo.LeftEyeCamera;
        rightEyeCamera = vrInfo.RightEyeCamera;
        world = vrInfo.World;
        locomotionRoot = vrInfo.HMDNode?.Transform.Parent;

        if (vrInfo.HMDNode is null)
        {
            reason = "VRState has no HMD node";
            return false;
        }

        if (leftEyeCamera is null || rightEyeCamera is null)
        {
            reason =
                $"VRState eye cameras are incomplete (left={leftEyeCamera is not null}, right={rightEyeCamera is not null})";
            return false;
        }

        if (world is null)
        {
            reason = "VRState has no render world";
            return false;
        }

        if (!IsAppVrRigEyeTransform(leftEyeCamera.Transform) ||
            !IsAppVrRigEyeTransform(rightEyeCamera.Transform))
        {
            reason = "VRState eye cameras are not scene-rig VREyeTransforms";
            return false;
        }

        if (!ReferenceEquals(leftEyeCamera.Transform.Parent, vrInfo.HMDNode.Transform) ||
            !ReferenceEquals(rightEyeCamera.Transform.Parent, vrInfo.HMDNode.Transform))
        {
            reason = "VRState eye camera transforms are not parented directly to the HMD transform";
            return false;
        }

        LogResolvedOpenXrVrRig(vrInfo.HMDNode, leftEyeCamera, rightEyeCamera, world);
        reason = string.Empty;
        return true;
    }

    private static void LogResolvedOpenXrVrRig(
        XREngine.Scene.SceneNode hmdNode,
        XRCamera leftEyeCamera,
        XRCamera rightEyeCamera,
        IRuntimeRenderWorld world)
    {
        if (!VulkanCaptureEyeOutputs && !OpenXrDebugLifecycle)
            return;

        Debug.RenderingEvery(
            "OpenXR.Rig.Resolved",
            TimeSpan.FromSeconds(2),
            "[OpenXR] Resolved scene VR rig: hmd='{0}' left={1} right={2} world=0x{3:X8}",
            hmdNode.Name ?? "<unnamed>",
            leftEyeCamera.Transform.GetType().FullName ?? "<unknown>",
            rightEyeCamera.Transform.GetType().FullName ?? "<unknown>",
            world.GetHashCode());
    }

    private void ApplyOpenXrEyePoseForRenderThread(uint viewIndex)
    {
        XRCamera? camera = GetOpenXrEyeCamera(viewIndex);
        if (camera is null)
            return;

        if (!TryGetOpenXrViewPoseAndFov(
                viewIndex,
                OpenXrPoseTiming.Late,
                out Matrix4x4 localPose,
                out var fov))
        {
            return;
        }

        if (camera.Parameters is XROpenXRFovCameraParameters parameters)
            parameters.SetAngles(fov.Left, fov.Right, fov.Up, fov.Down);

        if (!TryGetAppVrRigLocomotionRenderMatrix(camera, out Matrix4x4 rootRender))
        {
            Debug.RenderingWarningEvery(
                $"OpenXR.RenderPose.NonRigEye.{viewIndex}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Skipping late eye pose for view {0}: camera transform is not a VREyeTransform.",
                viewIndex);
            return;
        }

        camera.Transform.SetRenderMatrix(
            localPose * rootRender,
            recalcAllChildRenderMatrices: false);
    }

    private bool TryGetOpenXrViewPoseAndFov(
        uint viewIndex,
        OpenXrPoseTiming timing,
        out Matrix4x4 localPose,
        out (float Left, float Right, float Up, float Down) fov)
    {
        if (TryGetPhase524bFrozenViewPoseAndFov(viewIndex, out localPose, out fov))
            return true;

        bool allowPredictedFallback = timing == OpenXrPoseTiming.Late;
        if (TryGetCachedOpenXrViewForTiming(
                viewIndex,
                timing,
                allowPredictedFallback,
                out View view))
        {
            return CreateOpenXrViewPoseAndFov(view, out localPose, out fov);
        }

        bool leftEye = IsLeftEyeLikeOpenXrView(viewIndex);
        lock (_openXrPoseLock)
        {
            if (timing == OpenXrPoseTiming.Late)
            {
                localPose = leftEye ? _openXrLateLeftEyeLocalPose : _openXrLateRightEyeLocalPose;
                var cachedFov = leftEye ? _openXrLateLeftEyeFov : _openXrLateRightEyeFov;
                fov = (cachedFov.Left, cachedFov.Right, cachedFov.Up, cachedFov.Down);
            }
            else
            {
                localPose = leftEye ? _openXrPredLeftEyeLocalPose : _openXrPredRightEyeLocalPose;
                var cachedFov = leftEye ? _openXrPredLeftEyeFov : _openXrPredRightEyeFov;
                fov = (cachedFov.Left, cachedFov.Right, cachedFov.Up, cachedFov.Down);
            }
        }

        return true;
    }

    private bool TryGetPhase524bFrozenViewPoseAndFov(
        uint viewIndex,
        out Matrix4x4 localPose,
        out (float Left, float Right, float Up, float Down) fov)
    {
        if (!Phase524bTemporalStateDiagnostics.Enabled)
        {
            localPose = default;
            fov = default;
            return false;
        }

        bool leftEye = IsLeftEyeLikeOpenXrView(viewIndex);
        lock (_openXrPoseLock)
        {
            if (!_phase524bFrozenRuntimePoseInitialized)
            {
                localPose = default;
                fov = default;
                return false;
            }

            localPose = leftEye
                ? _phase524bFrozenLeftEyeLocalPose
                : _phase524bFrozenRightEyeLocalPose;
            fov = leftEye
                ? _phase524bFrozenLeftEyeFov
                : _phase524bFrozenRightEyeFov;
            return true;
        }
    }

    private bool TryGetOpenXrProjectionLayerView(uint viewIndex, out View view)
    {
        if (TryGetCachedOpenXrViewForTiming(
                viewIndex,
                OpenXrPoseTiming.Late,
                allowPredictedFallback: true,
                out view))
        {
            return true;
        }

        if (viewIndex < _viewCount && viewIndex < _views.Length)
        {
            view = _views[viewIndex];
            return true;
        }

        view = default;
        return false;
    }

    private bool TryGetCachedOpenXrViewForTiming(
        uint viewIndex,
        OpenXrPoseTiming timing,
        bool allowPredictedFallback,
        out View view)
    {
        int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
        int index = checked((int)viewIndex);

        lock (_openXrPoseLock)
        {
            if (TryGetCachedOpenXrViewForTimingNoLock(index, timing, frameNo, out view))
                return true;

            if (allowPredictedFallback &&
                timing == OpenXrPoseTiming.Late &&
                TryGetCachedOpenXrViewForTimingNoLock(
                    index,
                    OpenXrPoseTiming.Predicted,
                    frameNo,
                    out view))
            {
                return true;
            }
        }

        view = default;
        return false;
    }

    private bool TryGetCachedOpenXrViewForTimingNoLock(
        int viewIndex,
        OpenXrPoseTiming timing,
        int frameNo,
        out View view)
    {
        View[] views = timing == OpenXrPoseTiming.Late
            ? _openXrLateViews
            : _openXrPredictedViews;
        int count = timing == OpenXrPoseTiming.Late
            ? _openXrLateViewCount
            : _openXrPredictedViewCount;
        int cacheFrameNo = timing == OpenXrPoseTiming.Late
            ? _openXrLateViewFrameNumber
            : _openXrPredictedViewFrameNumber;

        if (viewIndex >= 0 &&
            viewIndex < count &&
            viewIndex < views.Length &&
            cacheFrameNo == frameNo)
        {
            view = views[viewIndex];
            return true;
        }

        view = default;
        return false;
    }

    private static bool CreateOpenXrViewPoseAndFov(
        View view,
        out Matrix4x4 localPose,
        out (float Left, float Right, float Up, float Down) fov)
    {
        localPose = CreateOpenXrViewLocalPoseMatrix(view.Pose);
        fov = (
            view.Fov.AngleLeft,
            view.Fov.AngleRight,
            view.Fov.AngleUp,
            view.Fov.AngleDown);
        return true;
    }

    private static Matrix4x4 CreateOpenXrViewLocalPoseMatrix(Posef pose)
    {
        Quaternion rotation = new(
            pose.Orientation.X,
            pose.Orientation.Y,
            pose.Orientation.Z,
            pose.Orientation.W);
        rotation = rotation.LengthSquared() > float.Epsilon
            ? Quaternion.Normalize(rotation)
            : Quaternion.Identity;

        Matrix4x4 matrix = Matrix4x4.CreateFromQuaternion(rotation);
        matrix.Translation = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
        return matrix;
    }

}
