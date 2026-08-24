using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Frozen resources and attachments required to encode an ImGui overlay.</summary>
internal readonly record struct VulkanImGuiOverlayRecordingInput(
    uint ImageIndex,
    CommandBuffer OverlayCommandBuffer,
    CommandBuffer PredecessorCommandBuffer,
    ImageLayout InitialSwapchainLayout,
    bool PreferKhrDynamicRendering,
    VulkanDynamicUiOverlayTarget Target,
    VulkanImGuiResources Resources,
    IReadOnlyDictionary<nint, DescriptorSet> DescriptorSets,
    bool ClearSwapchain,
    VulkanImGuiFrameSnapshot Snapshot)
{
    internal bool IsValid => OverlayCommandBuffer.Handle != 0 &&
        Target.SwapchainImage.Handle != 0 && Target.SwapchainView.Handle != 0;
}
