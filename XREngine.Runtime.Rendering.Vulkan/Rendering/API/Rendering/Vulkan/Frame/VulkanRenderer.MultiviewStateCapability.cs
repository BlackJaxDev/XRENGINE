namespace XREngine.Rendering.Vulkan;

public partial class VulkanRenderer : IRendererMultiviewStateBackendCapability
{
    bool IRendererMultiviewStateBackendCapability.HasActiveMultiviewDrawTarget
        => HasActiveMultiviewDrawTarget;

    uint IRendererMultiviewStateBackendCapability.CurrentDrawViewMask
        => CurrentDrawViewMask;
}
