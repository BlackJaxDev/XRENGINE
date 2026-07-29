using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns Vulkan handles and reusable draw buffers for the ImGui backend.
/// </summary>
internal sealed class VulkanImGuiResources
{
    internal ShaderModule VertShader;
    internal ShaderModule FragShader;
    internal PipelineLayout PipelineLayout;
    internal Pipeline Pipeline;
    internal ulong PipelineSignature;

    internal DescriptorSetLayout DescriptorSetLayout;
    internal DescriptorPool DescriptorPool;
    internal DescriptorSet FontDescriptorSet;

    internal Image FontImage;
    internal DeviceMemory FontImageMemory;
    internal ImageView FontImageView;
    internal Sampler FontSampler;
    internal bool FontReady;

    internal VulkanImGuiDrawBufferSet[] DrawBuffers = [];
    internal CommandBuffer[]? OverlayCommandBuffers;
}
