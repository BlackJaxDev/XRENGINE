namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free reason that a resize-release handoff cannot advance in the current frame.
/// </summary>
internal enum VulkanResizeReleaseBlocker
{
    None = 0,
    SceneCommandChainIncomplete = 1,
    ScreenSpaceUserInterfaceCommandChainIncomplete = 2,
    ImGuiSnapshotIncomplete = 3,
    SuccessorGenerationMismatch = 4,
    SuccessorExtentMismatch = 5,
    AuthoredTerminalProducerMissing = 6,
}
