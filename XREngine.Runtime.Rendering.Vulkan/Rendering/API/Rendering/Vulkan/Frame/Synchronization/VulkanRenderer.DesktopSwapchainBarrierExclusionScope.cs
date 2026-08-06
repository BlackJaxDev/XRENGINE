namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Temporarily suppresses desktop-swapchain image barriers while recording
/// work for an external or otherwise independently owned presentation path.
/// </summary>
internal readonly ref struct DesktopSwapchainBarrierExclusionScope
{
    private readonly VulkanSynchronizationThreadState _state;
    private readonly bool _previous;

    /// <summary>
    /// Captures the current exclusion flag and optionally enables exclusion
    /// for the lifetime of this scope.
    /// </summary>
    /// <param name="state">The calling thread's synchronization state.</param>
    /// <param name="exclude">
    /// Whether desktop-swapchain barriers should be excluded.
    /// </param>
    public DesktopSwapchainBarrierExclusionScope(
        VulkanSynchronizationThreadState state,
        bool exclude)
    {
        _state = state;
        _previous = state.ExcludeDesktopSwapchainBarriers;
        if (exclude)
            state.ExcludeDesktopSwapchainBarriers = true;
    }

    /// <summary>
    /// Restores the exclusion state that was active before this scope.
    /// </summary>
    public void Dispose()
        => _state.ExcludeDesktopSwapchainBarriers = _previous;
}
