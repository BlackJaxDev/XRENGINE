namespace XREngine.Rendering.Vulkan;

/// <summary>Runtime-owned ready-entry staging lists reused by serialized retirement drains.</summary>
/// <remarks>The frame loop serializes retirement drain invocations; each caller clears its list before use.</remarks>
internal sealed class VulkanRetirementDrainScratch
{
    internal List<RetiredBuffer> Buffers { get; } = new(256);
    internal List<RetiredFramebuffer> Framebuffers { get; } = new(64);
    internal List<RetiredDescriptorPool> DescriptorPools { get; } = new(16);
    internal List<RetiredDescriptorSet> DescriptorSets { get; } = new(64);
    internal List<RetiredPipeline> Pipelines { get; } = new(16);
    internal List<VulkanRetiredPipelineLayout> PipelineLayouts { get; } = new(16);
    internal List<VulkanRetiredDescriptorSetLayout> DescriptorSetLayouts { get; } = new(16);
    internal List<RetiredQueryPool> QueryPools { get; } = new(32);
    internal List<RetiredCommandBuffer> CommandBuffers { get; } = new(128);
    internal List<RetiredCommandPool> CommandPools { get; } = new(16);
    internal List<RetiredBufferView> BufferViews { get; } = new(32);
    internal List<RetiredImageResourceEntry> Images { get; } = new(64);
}
