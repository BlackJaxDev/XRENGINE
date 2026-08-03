namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Tracks consecutive non-interactive swapchain acquire unavailability and
/// requests bounded recovery after the configured threshold.
/// </summary>
internal struct VulkanDesktopAcquireAvailabilityTracker
{
    internal const int DefaultRecreateThreshold = 3;

    private int _consecutiveUnavailableCount;

    /// <summary>
    /// Gets the number of consecutive non-interactive unavailable results.
    /// </summary>
    internal readonly int ConsecutiveUnavailableCount
        => _consecutiveUnavailableCount;

    /// <summary>
    /// Records one unavailable acquire result.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the recovery threshold is reached and the
    /// swapchain should be recreated.
    /// </returns>
    internal bool ObserveUnavailable(
        bool interactiveResize,
        out int observedCount)
    {
        if (interactiveResize)
        {
            observedCount = _consecutiveUnavailableCount;
            return false;
        }

        observedCount = checked(++_consecutiveUnavailableCount);
        if (observedCount < DefaultRecreateThreshold)
            return false;

        _consecutiveUnavailableCount = 0;
        return true;
    }

    /// <summary>
    /// Resets the unavailable-result sequence after a successful acquisition.
    /// </summary>
    internal void Reset()
        => _consecutiveUnavailableCount = 0;
}
