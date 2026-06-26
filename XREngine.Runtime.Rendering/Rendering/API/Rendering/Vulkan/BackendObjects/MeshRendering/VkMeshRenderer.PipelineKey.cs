using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{

    public partial class VkMeshRenderer
    {
        internal readonly record struct PipelineKey(
            PrimitiveTopology Topology,
            bool UseDynamicRendering,
            ulong RenderPassHandle,
            DynamicRenderingFormatSignature DynamicRenderingFormats,
            ulong ProgramPipelineHash,
            ulong VertexLayoutHash,
            ulong DescriptorLayoutHash,
            ulong MaterialLayoutHash,
            ulong PassMetadataHash,
            ulong FeatureProfileHash,
            SampleCountFlags RasterizationSamples,
            bool DepthTestEnabled,
            bool DepthWriteEnabled,
            CompareOp DepthCompareOp,
            bool StencilTestEnabled,
            StencilOpState FrontStencilState,
            StencilOpState BackStencilState,
            uint StencilWriteMask,
            CullModeFlags CullMode,
            FrontFace FrontFace,
            bool BlendEnabled,
            bool AlphaToCoverageEnabled,
            BlendOp ColorBlendOp,
            BlendOp AlphaBlendOp,
            BlendFactor SrcColorBlendFactor,
            BlendFactor DstColorBlendFactor,
            BlendFactor SrcAlphaBlendFactor,
            BlendFactor DstAlphaBlendFactor,
            ColorComponentFlags ColorWriteMask,
            uint ViewportScissorCount,
            bool NativeNegativeOneToOneDepth);
    }
}

// Remaining VkMeshRenderer implementation lives in partial files:
// - VkMeshRenderer.Buffers.cs
// - VkMeshRenderer.Pipeline.cs
// - VkMeshRenderer.Drawing.cs
// - VkMeshRenderer.Descriptors.cs
// - VkMeshRenderer.Uniforms.cs
// - VkMeshRenderer.Cleanup.cs
