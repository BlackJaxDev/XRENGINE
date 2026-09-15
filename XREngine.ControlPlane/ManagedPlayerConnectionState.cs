namespace XREngine.ControlPlane;

public enum ManagedPlayerConnectionState
{
    PendingDelivery = 0,
    Installed,
    Connected,
    Synchronized,
    ResumeHeld,
}
