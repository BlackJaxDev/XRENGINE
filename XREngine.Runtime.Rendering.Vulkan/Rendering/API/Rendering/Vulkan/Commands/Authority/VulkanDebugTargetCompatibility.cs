using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Stores the resolved target format state for one debug graphics pass.</summary>
internal struct VulkanDebugTargetCompatibility
{
    public VkFrameBuffer? Target;
    public VulkanRecordedRenderTargetSnapshot NativeTarget;
    public int PassIndex;
    public VulkanFrameOpPlannerStateKey PlannerKey;
    public VulkanCompiledRenderGraph? Graph;
    public long AllocatorOwnershipId;
    public uint FramebufferLayers;
    public bool DynamicRenderingEnabled;
    public bool UseDynamicRendering;
    public RenderPass RenderPass;
    public DynamicRenderingFormatSignature DynamicFormats;
    public bool DepthStencilReadOnly;
}
