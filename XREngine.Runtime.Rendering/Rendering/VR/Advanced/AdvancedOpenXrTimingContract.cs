namespace XREngine.Rendering;

/// <summary>
/// Timing and synchronization contract for OpenXR prediction, late latching, and swapchain presentation.
/// </summary>
public static class AdvancedOpenXrTimingContract
{
    /// <summary>
    /// Checks if a predicted display time is valid and monotonic with respect to current frame time.
    /// </summary>
    public static bool IsPredictedDisplayTimeValid(long predictedDisplayTimeNs, long currentFrameTimeNs)
        => predictedDisplayTimeNs > currentFrameTimeNs;

    /// <summary>
    /// Evaluates whether a late-latch pose update should be accepted prior to command submission.
    /// </summary>
    public static bool ShouldApplyLateLatch(bool isLateLatchSupported, bool isCameraCut)
        => isLateLatchSupported && !isCameraCut;
}
