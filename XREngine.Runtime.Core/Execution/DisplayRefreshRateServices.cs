namespace XREngine.Execution;

/// <summary>
/// Platform capability that reads the refresh rate of the primary display.
/// </summary>
public static class DisplayRefreshRateServices
{
    /// <summary>
    /// Returns the primary display refresh rate in hertz, or 0 when the rate is unknown.
    /// Null when no platform capability is installed.
    /// </summary>
    public static Func<float>? PrimaryRefreshRateReader { get; set; }
}
