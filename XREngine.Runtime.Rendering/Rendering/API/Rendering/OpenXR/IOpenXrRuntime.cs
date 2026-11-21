using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using XREngine.Input;

namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Native-free OpenXR runtime surface used by engine and editor orchestration.</summary>
public interface IOpenXrRuntime : IOpenXrApplicationLifecycle
{
    XRWindow? Window { get; set; }
    RuntimeOpenXrState RuntimeState { get; }
    bool IsSessionRunning { get; }
    bool IsRvcOpenXrVisibilityMaskExtensionEnabled { get; }
    bool CanUseTrueSinglePassStereo { get; }
    double CurrentRenderDeadlineMs { get; }
    /// <summary>Estimated time until the next XR frame needs the render thread, or a negative value when unknown.</summary>
    double NextFrameSlackMs { get; }
    /// <summary>Timestamp of the last return from <c>xrWaitFrame</c>. Each XR frame has its own value.</summary>
    long LastFrameWakeTicks { get; }
    /// <summary>
    /// True when the collect thread has collected and swapped an XR frame that the next render
    /// frame renders: the eye viewports then render in that render frame. Read it on the render
    /// thread before the eyes render.
    /// </summary>
    bool HasPreparedEyeFrame => false;
    bool PrepareRendererDeviceTeardown(AbstractRenderer renderer, string reason);
    void PrepareRendererDeviceLossAbandonment(AbstractRenderer renderer, string reason, OpenXrDeviceLossSource source);
    XRViewport? StereoViewport { get; }
    XRTexture2D? PreviewLeftEyeTexture { get; }
    XRTexture2D? PreviewRightEyeTexture { get; }
    XRTexture2D? DesktopMirrorTexture { get; }
    ulong PreviewLeftEyeFrameId { get; }
    ulong PreviewRightEyeFrameId { get; }
    bool TryGetLatestIPD(out float ipdMeters);
    bool TryGetEyeFovAngles(bool leftEye, out float angleLeft, out float angleRight, out float angleUp, out float angleDown);
    /// <summary>
    /// Returns the XR_KHR_visibility_mask meshes of one eye as the runtime reported them: vertices in
    /// view space on the plane at z = -1 (x right, y up), and triangle indices. <paramref name="revision"/>
    /// changes each time the engine fetches the meshes again. Returns false when the runtime gives no
    /// hidden-area mesh. The arrays belong to the runtime; callers must not change them.
    /// </summary>
    /// <summary>Returns 0 for the left eye camera, 1 for the right eye camera, and -1 for any other camera.</summary>
    int ResolveEyeIndex(XRCamera? camera) => -1;
    bool TryGetVisibilityMaskMeshes(
        bool leftEye,
        out Vector2[] hiddenVertices,
        out uint[] hiddenIndices,
        out Vector2[] visibleVertices,
        out uint[] visibleIndices,
        out ulong revision)
    {
        hiddenVertices = visibleVertices = [];
        hiddenIndices = visibleIndices = [];
        revision = 0;
        return false;
    }
    bool TryRenderDesktopMirrorComposition(uint targetWidth, uint targetHeight);
    bool TryGetHeadLocalPose(out Matrix4x4 localPose);
    bool TryGetHeadLocalPose(RuntimeOpenXrPoseTiming timing, out Matrix4x4 localPose);
    bool TryGetEyeLocalPose(bool leftEye, out Matrix4x4 localPose);
    string? GetControllerInteractionProfile(bool leftHand) => null;
    bool TryGetControllerLocalPose(bool leftHand, RuntimeOpenXrPoseTiming timing, out Matrix4x4 localPose);
    bool TryGetTrackerLocalPose(string trackerUserPath, RuntimeOpenXrPoseTiming timing, out Matrix4x4 localPose);
    string[] GetKnownTrackerUserPaths();
    RuntimeVrTrackerInfo[] GetKnownTrackers();
    bool TryCopyTrackingSnapshot(Span<RuntimeVrTrackerPose> trackers, out RuntimeVrTrackingSnapshot snapshot, out int trackerCount)
    {
        snapshot = default;
        trackerCount = 0;
        return false;
    }
    bool IsInputActionKnown(string category, string name, RuntimeVrActionValueType valueType);
    bool TryGetBooleanActionState(string category, string name, out bool value, out bool active);
    bool TryGetFloatActionState(string category, string name, out float value, out bool active);
    bool TryGetVector2ActionState(string category, string name, out Vector2 value, out bool active);
    bool TryGetVector3ActionState(string category, string name, out Vector3 value, out bool active);
    bool TryGetControllerPoseState(bool leftHand, RuntimeVrPoseKind poseKind, RuntimeOpenXrPoseTiming timing, out RuntimeVrPoseState pose);
    bool TryGetHandJointState(bool leftHand, RuntimeVrHandJoint joint, out RuntimeVrHandJointState state);
    bool TryGetSkeletonSummary(bool leftHand, out RuntimeVrSkeletonSummary summary);
    bool ApplyHapticAction(string category, string name, double duration, double frequency, double amplitude, double delay);
    bool StopHapticAction(string category, string name);
    bool TryGetControllerRenderModel(bool leftHand, [NotNullWhen(true)] out RuntimeVrRenderModelDescriptor? renderModel);
    string DescribeControllerRenderModelAvailability();
    OpenXrSmokeSummary CreateSmokeSummary(string? logDirectory = null);
    void RequestSmokeSessionExit();
    event Action<long, long, long>? SmokeFrameCompleted;
    long SmokeSubmittedFrameCount { get; }
    long SmokeNoLayerFrameCount { get; }
    long SmokeCompletedFrameCount { get; }
    bool SmokeTeardownCompleted { get; }
    OpenXrSmokeFrameTiming SmokeLastFrameTiming { get; }
    int SmokeLastEndFrameResult { get; }
    uint SmokeLastEndFrameLayerCount { get; }
    ulong SmokeLastRenderedFrameId { get; }
    float? SmokeEffectiveTsrRenderScale { get; }
    long StrictSinglePassStereoSequentialFallbackAttemptCount { get; }
    long GetSmokeEyeAcquireCount(uint viewIndex);
    long GetSmokeEyeWaitCount(uint viewIndex);
    long GetSmokeEyePublishCount(uint viewIndex);
    long GetSmokeEyeReleaseCount(uint viewIndex);
    int GetSmokeEyeLastImageSlot(uint viewIndex);
    OpenXrSmokeCaptureLedgerEntry[] GetStrictSpsBoundaryCaptureLedger();
}
