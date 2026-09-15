namespace XREngine.ControlPlane;

/// <summary>State authority used by the service and its worker-report endpoint.</summary>
public interface IManagedInstanceRegistry
{
    ControlPlaneResult<MultiplayerInstanceInfo> CreateManagedInstance(CreateMultiplayerInstanceRequest request);
    ControlPlaneResult<ManagedWorkerLaunch> ConfigureManagedWorkerLaunch(ManagedWorkerLaunch launch);
    ControlPlaneResult<ManagedWorkerLaunch> GetManagedWorkerLaunch(string instanceId, Guid generation);
    ControlPlaneResult<ManagedWorkerDirective> RecordWorkerReport(ManagedWorkerReport report);
    ControlPlaneResult<ReserveManagedAdmissionResult> ReserveAdmission(ReserveManagedAdmissionRequest request);
    ControlPlaneResult<ManagedAdmissionReservation> GetReservation(string instanceId, string reservationId);
    ControlPlaneResult<ReserveManagedAdmissionResult> GetDeliveredAdmission(string instanceId, string reservationId);
    bool RevokeAdmission(string instanceId, string reservationId, bool kick = false);
    bool SetRequestedWorkerState(string instanceId, ManagedWorkerState requestedState);
    bool RecordLaunchProgress(string instanceId, Guid generation, ManagedWorkerState state);
    bool RecordOwnedProcessExit(string instanceId, Guid generation, int? exitCode, ManagedWorkerFailure? failure = null);
    bool HeartbeatHost(string hostId);
}
