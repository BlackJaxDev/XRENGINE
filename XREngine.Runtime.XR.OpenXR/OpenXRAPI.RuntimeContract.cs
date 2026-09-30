using System.Numerics;
using XREngine.Input;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI : IOpenXrRuntime
{
    RuntimeOpenXrState IOpenXrRuntime.RuntimeState => (RuntimeOpenXrState)RuntimeState;

    double IOpenXrRuntime.CurrentRenderDeadlineMs => CurrentRenderDeadlineMs;

    bool IOpenXrRuntime.PrepareRendererDeviceTeardown(AbstractRenderer renderer, string reason)
        => PrepareRendererDeviceTeardown(renderer, reason);

    void IOpenXrRuntime.PrepareRendererDeviceLossAbandonment(
        AbstractRenderer renderer, string reason, OpenXrDeviceLossSource source)
        => PrepareRendererDeviceLossAbandonment(renderer, reason, source);

    bool IOpenXrRuntime.TryRenderDesktopMirrorComposition(uint targetWidth, uint targetHeight)
        => TryRenderDesktopMirrorComposition(targetWidth, targetHeight);

    bool IOpenXrRuntime.TryGetHeadLocalPose(RuntimeOpenXrPoseTiming timing, out Matrix4x4 localPose)
        => TryGetHeadLocalPose((OpenXrPoseTiming)timing, out localPose);

    bool IOpenXrRuntime.TryGetControllerLocalPose(bool leftHand, RuntimeOpenXrPoseTiming timing, out Matrix4x4 localPose)
        => TryGetControllerLocalPose(leftHand, (OpenXrPoseTiming)timing, out localPose);

    bool IOpenXrRuntime.TryGetTrackerLocalPose(string trackerUserPath, RuntimeOpenXrPoseTiming timing, out Matrix4x4 localPose)
        => TryGetTrackerLocalPose(trackerUserPath, (OpenXrPoseTiming)timing, out localPose);

    bool IOpenXrRuntime.TryGetControllerPoseState(
        bool leftHand,
        RuntimeVrPoseKind poseKind,
        RuntimeOpenXrPoseTiming timing,
        out RuntimeVrPoseState pose)
        => TryGetControllerPoseState(leftHand, poseKind, (OpenXrPoseTiming)timing, out pose);
}
