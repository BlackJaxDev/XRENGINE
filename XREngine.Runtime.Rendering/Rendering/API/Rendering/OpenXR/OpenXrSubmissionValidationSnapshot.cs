namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Bounded backend evidence captured at the end of an OpenXR smoke run.</summary>
public sealed class OpenXrSubmissionValidationSnapshot
{
    /// <summary>Frequency for all timestamp and duration fields in this snapshot.</summary>
    public long StopwatchFrequency { get; set; }
    public int TrackedUploadCapacity { get; set; }
    public int TrackedCommandBufferCapacity { get; set; }
    public int TrackedFrameSlotCapacity { get; set; }
    public int TrackedSwapchainImageCapacity { get; set; }
    /// <summary>Accepted submissions required before allocation sampling begins.</summary>
    public int AllocationMeasurementWarmupAcceptedCount { get; set; }
    /// <summary>True once the accepted-submission warmup completed.</summary>
    public bool AllocationMeasurementMatured { get; set; }
    public long RegisterInvocationCount { get; set; }
    public long RegisterAllocatedBytes { get; set; }
    public long RegisterAllocationHighWaterBytes { get; set; }
    public long PollInvocationCount { get; set; }
    public long PollAllocatedBytes { get; set; }
    public long PollAllocationHighWaterBytes { get; set; }
    public long PollTimelineQueryCount { get; set; }
    public long PollRetirementWorkCount { get; set; }
    public long RetirementInvocationCount { get; set; }
    public long RetirementAllocatedBytes { get; set; }
    public long RetirementAllocationHighWaterBytes { get; set; }
    public OpenXrSubmissionValidationRequest Request { get; set; }
    public OpenXrSubmissionOwnershipLedgerEntry[] Entries { get; set; } = [];
    public int OverflowCount { get; set; }
    public int AdmissionHighWater { get; set; }
    public int AdmissionCapacity { get; set; }
    public int ReservationDeferralCount { get; set; }
    public int ForcedWaitCount { get; set; }
    public bool HoldArmed { get; set; }
    public bool HoldReleased { get; set; }
    public EOpenXrCompletionObservationHoldReleaseReason HoldReleaseReason { get; set; }
    public int AcceptedCount { get; set; }
    public int RejectedCount { get; set; }
    public int PublicationFailureCount { get; set; }
    public int InjectedPreNativeSubmitRejectionCount { get; set; }
    public int InjectedAcceptedPublicationFailureCount { get; set; }
    public int RealCompletionCount { get; set; }
    public int RetiredCount { get; set; }
    public long AbandonedSubmissionCount { get; set; }
    public int ActiveCount { get; set; }
    public int ReservedCount { get; set; }
    public int PendingCommitCount { get; set; }
}
