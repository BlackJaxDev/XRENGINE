namespace XREngine.Rendering.UI;

/// <summary>
/// Configures analog stick flick thresholds and hold-to-repeat timing for directional UI navigation.
/// </summary>
public struct UINavigationRepeatSettings
{
    /// <summary>
    /// Stick deflection required to engage a new directional navigation step.
    /// </summary>
    public float StickEngageThreshold { get; set; }

    /// <summary>
    /// Stick deflection below which an engaged stick resets to neutral so the next flick can trigger.
    /// </summary>
    public float StickReleaseThreshold { get; set; }

    /// <summary>
    /// When true, holding a D-pad direction or deflecting the stick continuously repeats navigation steps.
    /// </summary>
    public bool AllowHoldRepeat { get; set; }

    /// <summary>
    /// Delay in seconds before the first repeated step fires while a direction is held.
    /// </summary>
    public float InitialRepeatDelaySeconds { get; set; }

    /// <summary>
    /// Interval in seconds between subsequent repeated steps while a direction remains held.
    /// </summary>
    public float RepeatIntervalSeconds { get; set; }

    /// <summary>
    /// When true, analog stick input can resolve diagonal directions allowed by the active flow map.
    /// </summary>
    public bool AllowDiagonalStick { get; set; }

    /// <summary>
    /// Returns the default responsive console-style flick and hold-repeat settings.
    /// </summary>
    public static UINavigationRepeatSettings Default => new()
    {
        StickEngageThreshold = 0.55f,
        StickReleaseThreshold = 0.30f,
        AllowHoldRepeat = true,
        InitialRepeatDelaySeconds = 0.38f,
        RepeatIntervalSeconds = 0.14f,
        AllowDiagonalStick = false,
    };
}
