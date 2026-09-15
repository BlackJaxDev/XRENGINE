namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Captures ownership transitions of one real OpenXR Vulkan submission.</summary>
public sealed class OpenXrSubmissionOwnershipLedgerEntry
{
    public long Serial { get; set; }
    public long RuntimeEpoch { get; set; }
    public int AdmissionSlotIndex { get; set; }
    public ulong TicketGeneration { get; set; }
    public ulong FrameId { get; set; }
    public long PredictedDisplayTime { get; set; }
    /// <summary>Real monotonic timestamps captured around the native queue submission.</summary>
    public long SubmitStartTimestamp { get; set; }
    public long SubmitEndTimestamp { get; set; }
    public long CompletionTimestamp { get; set; }
    public long InFlightWallAgeTicks { get; set; }
    public EOpenXrSubmissionShape Shape { get; set; }
    public EOpenXrSubmissionPayloadKind PayloadKinds { get; set; }
    public uint CommandCount { get; set; }
    public bool AcceptedCommandShapeMatches { get; set; }
    public uint ViewMask { get; set; }
    public uint LeftImageIndex { get; set; }
    public uint RightImageIndex { get; set; }
    public uint FirstViewIndex { get; set; }
    public uint FirstImageIndex { get; set; }
    public uint SecondViewIndex { get; set; }
    public uint SecondImageIndex { get; set; }
    /// <summary>Frames since the last use of each eye image; null means no reliable eye-image age was available.</summary>
    public uint? LeftImageReuseAgeFrames { get; set; }
    public uint? RightImageReuseAgeFrames { get; set; }
    public int RecordedCommandCount { get; set; }
    public int PreparedInputCount { get; set; }
    public int TemporaryCommandCount { get; set; }
    public int UploadCount { get; set; }
    public int FrameSlotCount { get; set; }
    public int ExternalTargetCount { get; set; }
    /// <summary>Native timeline semaphore handle returned by the accepted queue receipt.</summary>
    public ulong CompletionSemaphoreHandle { get; set; }
    public ulong TimelineValue { get; set; }
    public long ForcedWaitStartTimestamp { get; set; }
    public long ForcedWaitEndTimestamp { get; set; }
    public int ForcedWaitResult { get; set; }
    public bool ForcedWaitAttempted { get; set; }
    public bool ObservationPressureAtCapacity { get; set; }
    public bool PreWaitCompletionProven { get; set; }
    public int PreWaitSettlementCount { get; set; }
    public int ReceiptResult { get; set; }
    public bool SubmissionAccepted { get; set; }
    public bool LifetimePinsTransferred { get; set; }
    public bool PostSubmissionPublicationSucceeded { get; set; }
    /// <summary>Smoke-only validation boundary intentionally exercised for this receipt.</summary>
    public EOpenXrSubmissionValidationScenario InjectedValidationScenario { get; set; }
    public EOpenXrSubmissionDisposition Disposition { get; set; }
    public bool AcceptedIncompleteObserved { get; set; }
    public bool OwnershipIntactWhenAcceptedIncomplete { get; set; }
    public bool CompletionProven { get; set; }
    public bool Cancelled { get; set; }
    public bool AbandonedAfterDeviceLoss { get; set; }
    public int UploadSettlementCount { get; set; }
    public int RecordedReleaseCount { get; set; }
    public int PreparedReleaseCount { get; set; }
    public int TemporaryReleaseCount { get; set; }
    public int MappedFrameSlotResetCount { get; set; }
    public int FrameDataSlotResetCount { get; set; }
    public int RetiredCallbackCount { get; set; }
    public bool Retired { get; set; }
    public int EarlySettlementViolationCount { get; set; }
}
