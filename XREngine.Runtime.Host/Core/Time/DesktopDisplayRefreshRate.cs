using XREngine.Execution;

namespace XREngine.Timers;

/// <summary>
/// Gets the refresh rate of the primary display. The engine uses it as the default
/// desktop render limit when VSync is off and no frame-rate target is set.
/// </summary>
internal static class DesktopDisplayRefreshRate
{
    /// <summary>The rate used when no platform capability can read the display rate.</summary>
    public const float FallbackHz = 60.0f;

    private const float MinimumHz = 30.0f;
    private const float MaximumHz = 500.0f;

    private static float _cachedHz;

    /// <summary>
    /// Gets the primary display refresh rate in hertz, or <see cref="FallbackHz"/>.
    /// The first read is cached, so later calls do not call into the platform.
    /// </summary>
    public static float Get()
    {
        float cached = Volatile.Read(ref _cachedHz);
        if (cached > 0.0f)
            return cached;

        float hz = DisplayRefreshRateServices.PrimaryRefreshRateReader?.Invoke() ?? 0.0f;
        hz = hz >= 1.0f ? Math.Clamp(hz, MinimumHz, MaximumHz) : FallbackHz;
        Volatile.Write(ref _cachedHz, hz);
        return hz;
    }
}
