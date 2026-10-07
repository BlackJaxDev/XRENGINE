namespace XREngine.Rendering;

/// <summary>Identifies where a backend first failed a fence rental.</summary>
public enum EGpuFenceFailureSite
{
    Unknown,
    BackendUnavailable,
    TimelineQuery,
    NativeSubmitRejected,
    NativeSubmitFailed,
    MissingTimelineSignal,
    CommandBufferReuse,
    RequiredProducerReuse,
    UnsubmittedMarker,
    PlanExcluded,
    PlanUnsubmitted,
    QueueRollback,
    QueueDiscard,
    RequiredProducerMissing,
    OutputCompletionAbandoned,
    RequiredProducerIntervalInvalid,
    OutputCohortIncomplete,
    OutputCompletionReservationRejected,
}
