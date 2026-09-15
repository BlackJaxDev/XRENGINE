namespace XREngine.ControlPlane;

public sealed class ReserveManagedAdmissionResult
{
    public ManagedAdmissionReservation Reservation { get; set; } = new();
    public ManagedAdmissionGrant? Grant { get; set; }
    public bool DeliveredToWorker { get; set; }
}
