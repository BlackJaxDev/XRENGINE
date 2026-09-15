namespace XREngine.Networking;

/// <summary>Read-only, credential-free transport rejection evidence for runtime fault validation.</summary>
public readonly record struct RealtimeTransportRejectionSnapshot(
    long BadMac,
    long BadSource,
    long Replay,
    long Unauthorized,
    long RateLimited,
    int CurrentPeerCount);
