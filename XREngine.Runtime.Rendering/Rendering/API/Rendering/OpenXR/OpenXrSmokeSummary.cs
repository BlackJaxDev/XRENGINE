namespace XREngine.Rendering.API.Rendering.OpenXR;

public sealed class OpenXrSmokeSummary
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? LogDirectory { get; set; }
    public string? RuntimeManifestPath { get; set; }
    public string? RuntimeName { get; set; }
    public string? RuntimeVersion { get; set; }
    public string RendererBackend { get; set; } = string.Empty;
    public string RuntimeState { get; set; } = string.Empty;
    public string SessionState { get; set; } = string.Empty;
    public string ReferenceSpaceType { get; set; } = string.Empty;
    public string ViewRenderModeRequested { get; set; } = string.Empty;
    public string ViewRenderModeEffective { get; set; } = string.Empty;
    public string ViewRenderImplementationPath { get; set; } = string.Empty;
    public string ViewRenderTemporalHistoryPolicy { get; set; } = string.Empty;
    public bool ViewRenderModeSupported { get; set; }
    public string? ViewRenderModeDiagnostic { get; set; }
    public string FoveationRequestedMode { get; set; } = string.Empty;
    public string FoveationEffectiveMode { get; set; } = string.Empty;
    public string FoveationQualityPreset { get; set; } = string.Empty;
    public string FoveationCapabilityPath { get; set; } = string.Empty;
    public bool FoveationSupported { get; set; }
    public string? FoveationDiagnostic { get; set; }
    public string[] FoveationBackendCapabilities { get; set; } = [];
    public string[] EnabledExtensions { get; set; } = [];
    public bool InstanceCreated { get; set; }
    public bool SystemFound { get; set; }
    public bool SessionCreated { get; set; }
    public bool ReferenceSpaceCreated { get; set; }
    public bool SwapchainsCreated { get; set; }
    public bool SessionRunning { get; set; }
    public bool TeardownCompleted { get; set; }
    public long SubmittedFrameCount { get; set; }
    public long NoLayerFrameCount { get; set; }
    public long EndFrameFailureCount { get; set; }
    public uint LocatedViewCount { get; set; }
    public bool PredictedViewPoseCached { get; set; }
    public bool LateViewPoseCached { get; set; }
    public bool PredictedActionPoseCacheUpdated { get; set; }
    public bool LateActionPoseCacheUpdated { get; set; }
    public bool LeftControllerGripPoseAvailable { get; set; }
    public bool RightControllerGripPoseAvailable { get; set; }
    public bool LeftControllerAimPoseAvailable { get; set; }
    public bool RightControllerAimPoseAvailable { get; set; }
    public bool TrackerPoseAvailable { get; set; }
    public string[] KnownTrackerUserPaths { get; set; } = [];
    public bool LeftHandJointsActive { get; set; }
    public bool RightHandJointsActive { get; set; }
    public bool DesktopMirrorComposed { get; set; }
    public long MissedDeadlineCount { get; set; }
    public long[] PerEyeAcquireCounts { get; set; } = [];
    public long[] PerEyeWaitCounts { get; set; } = [];
    public long[] PerEyePublishCounts { get; set; } = [];
    public long[] PerEyeReleaseCounts { get; set; } = [];
    public long PerFrameAllocationsBytes { get; set; }
    public int WarmupFrameCount { get; set; }
    public int RetainedFrameCount { get; set; }
    public OpenXrSmokeFrameLedgerEntry[] FrameLedger { get; set; } = [];
    public OpenXrSmokeOcclusionViewLedgerEntry[] OcclusionViewLedger { get; set; } = [];
    public bool OcclusionViewLedgerOverflow { get; set; }
    public OpenXrSmokeOutputLedgerEntry[] OutputLedger { get; set; } = [];
    public bool OutputLedgerOverflow { get; set; }
    public OpenXrSmokeSwapchainSummary[] Swapchains { get; set; } = [];
    public string[] RuntimeStateTransitions { get; set; } = [];
    public string[] SessionStateTransitions { get; set; } = [];
    public string[] Warnings { get; set; } = [];
    public string[] Failures { get; set; } = [];
    public long StrictSinglePassStereoSequentialFallbackAttemptCount { get; set; }
}
