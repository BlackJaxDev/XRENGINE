namespace XREngine.ControlPlane;

public enum ControlPlaneFailureReason
{
    None = 0,
    InvalidRequest,
    HostNotFound,
    NoHostCapacity,
    InstanceNotFound,
    InstanceNotRunning,
    InstanceFull,
    BuildVersionMismatch,
    WorldAssetMismatch,
    WorldPackageUnavailable,
}
