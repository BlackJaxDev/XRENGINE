namespace XREngine.Execution;

/// <summary>
/// Reason for an asynchronous render-work admission decision. Expected capacity
/// rejections use these values instead of exceptions.
/// </summary>
public enum ERenderWorkAdmissionReason : byte
{
    /// <summary>The request was admitted.</summary>
    Admitted = 0,

    /// <summary>The pool is too small to admit any asynchronous rental.</summary>
    AsynchronousCapacityDisabled = 1,

    /// <summary>The domain already holds its maximum number of asynchronous rentals.</summary>
    AsynchronousCapacityExhausted = 2,

    /// <summary>The executor already holds an asynchronous rental.</summary>
    ExecutorBusy = 3,

    /// <summary>The rental would take the last free pooled batch.</summary>
    LastFreeBatch = 4,

    /// <summary>The domain is shutting down, disposed or poisoned.</summary>
    DomainUnavailable = 5,

    /// <summary>Another asynchronous batch already reserves a requested lane.</summary>
    LaneReserved = 6,

    /// <summary>Synchronous work holds a lane-plan claim on a requested lane.</summary>
    LaneClaimedBySynchronousWork = 7,
}
