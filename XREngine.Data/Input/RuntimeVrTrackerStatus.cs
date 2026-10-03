namespace XREngine.Input;

/// <summary>Current tracker transport status without inferring SteamVR configuration from missing data.</summary>
public enum RuntimeVrTrackerStatus
{
    NotDiscovered,
    ProviderUnavailable,
    DisabledOrNotReported,
    DiscoveredUnbound,
    BoundInactive,
    Stale,
    TrackingLost,
    Usable,
}
