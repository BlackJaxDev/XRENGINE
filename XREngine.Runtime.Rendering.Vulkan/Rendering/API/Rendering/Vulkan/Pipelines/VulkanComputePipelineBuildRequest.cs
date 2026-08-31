using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Native compute input captured while the dependency mutation gate is held.</summary>
internal sealed class VulkanComputePipelineBuildRequest(
    VkRenderProgram program,
    VulkanProgramCreationPort programServices,
    VulkanComputePipelineCompileKey key,
    PipelineLayout pipelineLayout,
    DescriptorSetLayout[] descriptorSetLayouts,
    PipelineShaderStageCreateInfo computeStage,
    DescriptorSetAndBindingMappingEXTNative[] descriptorHeapMappings)
{
    public VkRenderProgram Program { get; } = program;
    public VulkanProgramCreationPort ProgramServices { get; } = programServices;
    public VulkanComputePipelineCompileKey Key { get; } = key;
    public PipelineLayout PipelineLayout { get; } = pipelineLayout;
    public DescriptorSetLayout[] DescriptorSetLayouts { get; } = descriptorSetLayouts;
    public PipelineShaderStageCreateInfo ComputeStage { get; } = computeStage;
    public DescriptorSetAndBindingMappingEXTNative[] DescriptorHeapMappings { get; } = descriptorHeapMappings;
}
