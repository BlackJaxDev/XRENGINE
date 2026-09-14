namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Bounded backend evidence captured at the end of an OpenXR smoke run.</summary>
public sealed class OpenXrSubmissionValidationSnapshot
{
    public OpenXrSubmissionValidationRequest Request { get; set; }
    public OpenXrSubmissionOwnershipLedgerEntry[] Entries { get; set; } = [];
    public int OverflowCount { get; set; }
    public int AdmissionHighWater { get; set; }
    public int AdmissionCapacity { get; set; }
    public int ReservationDeferralCount { get; set; }
    public int ForcedWaitCount { get; set; }
    public bool HoldArmed { get; set; }
    public bool HoldReleased { get; set; }
    public int AcceptedCount { get; set; }
    public int RejectedCount { get; set; }
    public int PublicationFailureCount { get; set; }
    public int RealCompletionCount { get; set; }
    public int RetiredCount { get; set; }
    public long AbandonedSubmissionCount { get; set; }
    public int ActiveCount { get; set; }
    public int ReservedCount { get; set; }
    public int PendingCommitCount { get; set; }
}
