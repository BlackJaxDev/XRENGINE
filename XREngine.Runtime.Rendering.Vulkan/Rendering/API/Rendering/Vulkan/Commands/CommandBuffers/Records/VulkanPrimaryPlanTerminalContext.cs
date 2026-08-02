namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable terminal-state requirements that complete a primary command plan
/// after its ordered frame-operation nodes.
/// </summary>
internal readonly record struct VulkanPrimaryPlanTerminalContext(
    bool PreserveSwapchainForOverlay,
    bool TransitionSwapchainToPresent,
    bool ReleaseExternalImageOwnership)
{
    internal bool RequiresPreparePresent =>
        TransitionSwapchainToPresent &&
        !PreserveSwapchainForOverlay;
}
