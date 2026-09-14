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
    public int RecordedCommandCount { get; set; }
    public int PreparedInputCount { get; set; }
    public int TemporaryCommandCount { get; set; }
    public int UploadCount { get; set; }
    public int FrameSlotCount { get; set; }
    public int ExternalTargetCount { get; set; }
    public ulong TimelineValue { get; set; }
    public int ReceiptResult { get; set; }
    public bool SubmissionAccepted { get; set; }
    public bool LifetimePinsTransferred { get; set; }
    public bool PostSubmissionPublicationSucceeded { get; set; }
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
