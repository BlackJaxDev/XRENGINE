namespace XREngine.ControlPlane;

/// <summary>
/// Represents the various reasons for a control plane operation failure.
/// </summary>
public enum ControlPlaneFailureReason
{
    /// <summary>
    /// Indicates that there is no failure.
    /// </summary>
    None = 0,
    /// <summary>
    /// Indicates that the request is invalid.
    /// </summary>
    InvalidRequest,
    /// <summary>
    /// Indicates that the host could not be found.
    /// </summary>
    HostNotFound,
    /// <summary>
    /// Indicates that there is no capacity on the host.
    /// </summary>
    NoHostCapacity,
    /// <summary>
    /// Indicates that the instance could not be found.
    /// </summary>
    InstanceNotFound,
    /// <summary>
    /// Indicates that the instance is not running.
    /// </summary>
    InstanceNotRunning,
    /// <summary>
    /// Indicates that the instance is full.
    /// </summary>
    InstanceFull,
    /// <summary>
    /// Indicates that the build version does not match.
    /// </summary>
    BuildVersionMismatch,
    /// <summary>
    /// Indicates that the world asset does not match.
    /// </summary>
    WorldAssetMismatch,
    /// <summary>
    /// Indicates that the world package is unavailable.
    /// </summary>
    WorldPackageUnavailable,
    /// <summary>
    /// Indicates that the instance is not ready.
    /// </summary>
    InstanceNotReady,
    /// <summary>
    /// Indicates that the instance is draining.
    /// </summary>
    InstanceDraining,
    /// <summary>
    /// Indicates that the worker generation is stale.
    /// </summary>
    StaleWorkerGeneration,
    /// <summary>
    /// Indicates that the worker report is stale.
    /// </summary>
    StaleWorkerReport,
    /// <summary>
    /// Indicates that the reservation could not be found.
    /// </summary>
    ReservationNotFound,
    /// <summary>
    /// Indicates that the reservation has expired.
    /// </summary>
    ReservationExpired,
    /// <summary>
    /// Indicates that the reservation is pending delivery.
    /// </summary>
    ReservationPendingDelivery,
    /// <summary>
    /// Indicates that the reservation has been revoked.
    /// </summary>
    ReservationRevoked,
    /// <summary>
    /// Indicates that the reservation has been consumed.
    /// </summary>
    ReservationConsumed,
    /// <summary>
    /// Indicates that there is a conflict with the operation.
    /// </summary>
    OperationConflict,
}
