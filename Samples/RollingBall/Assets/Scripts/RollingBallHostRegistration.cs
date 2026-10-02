namespace RollingBall;

/// <summary>Optional host installed by a platform launcher before selecting an application profile.</summary>
public static class RollingBallHostRegistration
{
    private static IRollingBallPlatformHost? _platformHost;

    public static IRollingBallPlatformHost? PlatformHost => Volatile.Read(ref _platformHost);

    public static void Register(IRollingBallPlatformHost platformHost)
    {
        ArgumentNullException.ThrowIfNull(platformHost);
        Interlocked.Exchange(ref _platformHost, platformHost);
    }
}
