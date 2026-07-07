using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{

    public partial class VkMeshRenderer
    {
        internal sealed class GraphicsPipelineBuildRequest(
            VulkanRenderer.VkMeshRenderer owner,
            VulkanRenderer.VkRenderProgram program,
            VkMeshRenderer.PipelineKey key,
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
            VulkanRenderer.DynamicRenderingFormatSignature dynamicRenderingFormats,
            PipelineShaderStageCreateInfo[] graphicsStages,
            PipelineShaderStageCreateInfo[] preRasterStages,
            PipelineShaderStageCreateInfo[] fragmentStages)
        {
            public VkMeshRenderer Owner { get; } = owner;
            public VkRenderProgram Program { get; } = program;
            public PipelineKey Key { get; } = key;
            public GraphicsPipelineCompileKey CompileKey { get; } = new GraphicsPipelineCompileKey(
                    key);
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
    }
}

// Remaining VkMeshRenderer implementation lives in partial files:
// - VkMeshRenderer.Buffers.cs
// - VkMeshRenderer.Pipeline.cs
// - VkMeshRenderer.Drawing.cs
// - VkMeshRenderer.Descriptors.cs
// - VkMeshRenderer.Uniforms.cs
// - VkMeshRenderer.Cleanup.cs
