using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Frozen command/runtime observations required by one late overlay recording.</summary>
internal readonly record struct VulkanDynamicUiBatchTextOverlayRecordingInput(
    CommandBuffer OverlayCommandBuffer,
    CommandBuffer SecondaryCommandBuffer,
    int OperationCount,
    ImageLayout InitialSwapchainLayout,
    CommandBuffer PredecessorCommandBuffer,
    bool PreferKhrDynamicRendering,
    VulkanDynamicUiOverlayTarget Target)
{
    internal bool IsValid => OverlayCommandBuffer.Handle != 0 &&
        SecondaryCommandBuffer.Handle != 0 &&
        OperationCount > 0 &&
        Target.SwapchainImage.Handle != 0 &&
        Target.SwapchainView.Handle != 0;
}
