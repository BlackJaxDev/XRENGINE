using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanFixedFunctionStateSnapshot(
    bool DepthTestEnabled,
    bool DepthWriteEnabled,
    CompareOp DepthCompareOp,
    bool StencilTestEnabled,
    StencilOpState FrontStencilState,
    StencilOpState BackStencilState,
    uint StencilWriteMask,
    ColorComponentFlags ColorWriteMask,
    CullModeFlags CullMode,
    FrontFace FrontFace,
    bool BlendEnabled,
    bool AlphaToCoverageEnabled,
    BlendOp ColorBlendOp,
    BlendOp AlphaBlendOp,
    BlendFactor SrcColorBlendFactor,
    BlendFactor DstColorBlendFactor,
    BlendFactor SrcAlphaBlendFactor,
    BlendFactor DstAlphaBlendFactor);
