namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Describes the ownership of a desktop resize-release presentation handoff.
/// </summary>
internal enum VulkanResizeReleaseHandoffState
{
    /// <summary>No resize-release handoff is active.</summary>
    Inactive,

    /// <summary>The old swapchain image remains visible while the replacement frame becomes ready.</summary>
    AwaitingReadyToRecreate,

    /// <summary>The successor swapchain exists and awaits its first complete presentation.</summary>
    AwaitingSuccessorPresent,
}
