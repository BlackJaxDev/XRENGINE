using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Immutable key for one graphics-pipeline-library subset.</summary>
internal readonly record struct VulkanGraphicsPipelineLibraryKey(
        VulkanGraphicsPipelineLibrarySubset Subset,
        bool UseDynamicRendering,
        ulong RenderPassHandle,
        DynamicRenderingFormatSignature DynamicRenderingFormats,
        PrimitiveTopology Topology,
        ulong ProgramPipelineHash,
        ulong ProgramLinkGeneration,
        ulong VertexLayoutHash,
        ulong DescriptorLayoutHash,
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
