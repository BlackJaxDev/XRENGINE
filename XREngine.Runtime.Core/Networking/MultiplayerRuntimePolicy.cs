namespace XREngine.Networking;

public static class MultiplayerRuntimePolicy
{
    public static TimeSpan PlayerHeartbeatTimeout { get; } = TimeSpan.FromSeconds(15.0);
    public static TimeSpan PlayerHeartbeatGracePeriod { get; } = TimeSpan.FromSeconds(5.0);
    public static TimeSpan SessionResumeWindow { get; } = TimeSpan.FromSeconds(30.0);
    public static TimeSpan AuthorityLeaseDuration { get; } = TimeSpan.FromSeconds(10.0);
    public static TimeSpan InputBufferWindow { get; } = TimeSpan.FromMilliseconds(250.0);
    public static TimeSpan MaxLagCompensationWindow { get; } = TimeSpan.FromMilliseconds(150.0);
    public static int MaxBufferedInputsPerPlayer { get; } = 32;
    public static int DefaultReplicationBytesPerSecond { get; } = 48 * 1024;
    public static float DefaultAreaOfInterestRadius { get; } = 80.0f;
}
