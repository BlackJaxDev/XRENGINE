using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable native graphics-pipeline build input. Pointer-bearing shader stages are
/// captured while shader-module and layout generations are pinned by the caller.
/// </summary>
internal sealed class VulkanGraphicsPipelineBuildRequest(
    long ownerId,
    VkRenderProgram program,
    VulkanProgramBackendServices programServices,
    bool useGraphicsPipelineLibraries,
    long dependencyGeneration,
    VulkanGraphicsPipelineKey key,
    string pipelineName,
    uint colorAttachmentCount,
    PipelineLayout pipelineLayout,
    VertexInputBindingDescription[] vertexBindings,
    VertexInputAttributeDescription[] vertexAttributes,
    PipelineInputAssemblyStateCreateInfo inputAssembly,
    uint viewportScissorCount,
    bool nativeNegativeOneToOneDepth,
    PipelineRasterizationStateCreateInfo rasterizer,
    PipelineMultisampleStateCreateInfo multisampling,
    PipelineDepthStencilStateCreateInfo depthStencil,
    PipelineColorBlendAttachmentState[] blendAttachments,
    DynamicState[] dynamicStates,
    RenderPass renderPass,
    DynamicRenderingFormatSignature dynamicRenderingFormats,
    PipelineShaderStageCreateInfo[] graphicsStages,
    PipelineShaderStageCreateInfo[] preRasterStages,
    PipelineShaderStageCreateInfo[] fragmentStages)
{
        public long OwnerId { get; } = ownerId;
        public VkRenderProgram Program { get; } = program;
        public VulkanProgramBackendServices ProgramServices { get; } = programServices;
        public bool UseGraphicsPipelineLibraries { get; } = useGraphicsPipelineLibraries;
        public long DependencyGeneration { get; } = dependencyGeneration;
        public VulkanGraphicsPipelineKey Key { get; } = key;
        public VulkanGraphicsPipelineCompileKey CompileKey { get; } = new(key);
        public string PipelineName { get; } = pipelineName;
        public uint ColorAttachmentCount { get; } = colorAttachmentCount;
        public PipelineLayout PipelineLayout { get; } = pipelineLayout;
        public VertexInputBindingDescription[] VertexBindings { get; } = vertexBindings;
        public VertexInputAttributeDescription[] VertexAttributes { get; } = vertexAttributes;
        public PipelineInputAssemblyStateCreateInfo InputAssembly { get; } = inputAssembly;
        public uint ViewportScissorCount { get; } = viewportScissorCount;
        public bool NativeNegativeOneToOneDepth { get; } = nativeNegativeOneToOneDepth;
        public PipelineRasterizationStateCreateInfo Rasterizer { get; } = rasterizer;
        public PipelineMultisampleStateCreateInfo Multisampling { get; } = multisampling;
        public PipelineDepthStencilStateCreateInfo DepthStencil { get; } = depthStencil;
        public PipelineColorBlendAttachmentState[] BlendAttachments { get; } = blendAttachments;
        public DynamicState[] DynamicStates { get; } = dynamicStates;
        public RenderPass RenderPass { get; } = renderPass;
        public DynamicRenderingFormatSignature DynamicRenderingFormats { get; } = dynamicRenderingFormats;
        public PipelineShaderStageCreateInfo[] GraphicsStages { get; } = graphicsStages;
        public PipelineShaderStageCreateInfo[] PreRasterStages { get; } = preRasterStages;
        public PipelineShaderStageCreateInfo[] FragmentStages { get; } = fragmentStages;
}
