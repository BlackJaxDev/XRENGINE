using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Resolves the fixed-function state of one mesh draw: the render options of the material over the state of the pass that
/// produces the draw. The draw enqueue and the off-frame readiness prewarm use the same rules, so a prewarmed pipeline has
/// the key of the real draw.
/// </summary>
internal static class VulkanMeshDrawFixedState
{
    /// <summary>Resolves the draw state. An option that the material leaves unchanged keeps the value of the pass.</summary>
    /// <param name="materialOptions">The render options of the draw, or null to use the pass state.</param>
    /// <param name="passState">The fixed-function state of the pass.</param>
    /// <param name="rasterizationSamples">The sample count of the target. Alpha to coverage needs more than one sample.</param>
    public static VulkanFixedFunctionStateSnapshot Resolve(
        RenderingParameters? materialOptions,
        in VulkanFixedFunctionStateSnapshot passState,
        SampleCountFlags rasterizationSamples)
    {
        RenderingParameters? matOpts = materialOptions;

        CullModeFlags cullMode = matOpts is not null
            ? VulkanMeshRenderingConventions.ToVulkanCullMode(VulkanMeshRenderingConventions.ResolveCullMode(matOpts.CullMode))
            : passState.CullMode;

        FrontFace frontFace = matOpts is not null
            ? VulkanMeshRenderingConventions.ToVulkanFrontFace(VulkanMeshRenderingConventions.ResolveWinding(matOpts.Winding))
            : passState.FrontFace;

        bool depthTestEnabled;
        bool depthWriteEnabled;
        CompareOp depthCompareOp;
        if (matOpts?.DepthTest is { } dt && dt.Enabled != ERenderParamUsage.Unchanged)
        {
            depthTestEnabled = dt.Enabled == ERenderParamUsage.Enabled;
            depthWriteEnabled = depthTestEnabled && dt.UpdateDepth;
            depthCompareOp = depthTestEnabled
                ? VulkanMeshRenderingConventions.ToVulkanCompareOp(RuntimeEngine.Rendering.State.MapDepthComparison(dt.Function))
                : CompareOp.Always;
        }
        else
        {
            depthTestEnabled = passState.DepthTestEnabled;
            depthWriteEnabled = passState.DepthWriteEnabled;
            depthCompareOp = passState.DepthCompareOp;
        }

        bool blendEnabled;
        bool alphaToCoverageEnabled;
        BlendOp colorBlendOp, alphaBlendOp;
        BlendFactor srcColor, dstColor, srcAlpha, dstAlpha;
        BlendMode? matBlend = matOpts is not null ? VulkanMeshRenderingConventions.ResolveBlendMode(matOpts) : null;
        bool requestedAlphaToCoverage = matOpts?.AlphaToCoverage == ERenderParamUsage.Enabled;
        if (matBlend is not null && matBlend.Enabled == ERenderParamUsage.Enabled)
        {
            blendEnabled = true;
            alphaToCoverageEnabled = requestedAlphaToCoverage && rasterizationSamples != SampleCountFlags.Count1Bit;
            colorBlendOp = VulkanMeshRenderingConventions.ToVulkanBlendOp(matBlend.RgbEquation);
            alphaBlendOp = VulkanMeshRenderingConventions.ToVulkanBlendOp(matBlend.AlphaEquation);
            srcColor = VulkanMeshRenderingConventions.ToVulkanBlendFactor(matBlend.RgbSrcFactor);
            dstColor = VulkanMeshRenderingConventions.ToVulkanBlendFactor(matBlend.RgbDstFactor);
            srcAlpha = VulkanMeshRenderingConventions.ToVulkanBlendFactor(matBlend.AlphaSrcFactor);
            dstAlpha = VulkanMeshRenderingConventions.ToVulkanBlendFactor(matBlend.AlphaDstFactor);
        }
        else if (matOpts is not null && (matBlend is null || matBlend.Enabled == ERenderParamUsage.Disabled))
        {
            blendEnabled = false;
            alphaToCoverageEnabled = requestedAlphaToCoverage && rasterizationSamples != SampleCountFlags.Count1Bit;
            colorBlendOp = BlendOp.Add;
            alphaBlendOp = BlendOp.Add;
            srcColor = BlendFactor.One;
            dstColor = BlendFactor.Zero;
            srcAlpha = BlendFactor.One;
            dstAlpha = BlendFactor.Zero;
        }
        else
        {
            blendEnabled = passState.BlendEnabled;
            alphaToCoverageEnabled = passState.AlphaToCoverageEnabled && rasterizationSamples != SampleCountFlags.Count1Bit;
            colorBlendOp = passState.ColorBlendOp;
            alphaBlendOp = passState.AlphaBlendOp;
            srcColor = passState.SrcColorBlendFactor;
            dstColor = passState.DstColorBlendFactor;
            srcAlpha = passState.SrcAlphaBlendFactor;
            dstAlpha = passState.DstAlphaBlendFactor;
        }

        bool stencilTestEnabled;
        StencilOpState frontStencilState;
        StencilOpState backStencilState;
        uint stencilWriteMask;
        if (matOpts?.StencilTest is { } stencilTest && stencilTest.Enabled != ERenderParamUsage.Unchanged)
        {
            if (stencilTest.Enabled == ERenderParamUsage.Enabled)
            {
                stencilTestEnabled = true;
                frontStencilState = VulkanMeshRenderingConventions.ToVulkanStencilState(stencilTest.FrontFace);
                backStencilState = VulkanMeshRenderingConventions.ToVulkanStencilState(stencilTest.BackFace);
                stencilWriteMask = stencilTest.FrontFace.WriteMask;
            }
            else
            {
                stencilTestEnabled = false;
                frontStencilState = default;
                backStencilState = default;
                stencilWriteMask = 0u;
            }
        }
        else
        {
            stencilTestEnabled = passState.StencilTestEnabled;
            frontStencilState = passState.FrontStencilState;
            backStencilState = passState.BackStencilState;
            stencilWriteMask = passState.StencilWriteMask;
        }

        ColorComponentFlags colorWriteMask = matOpts is not null
            ? VulkanMeshRenderingConventions.ToVulkanColorWriteMask(matOpts)
            : passState.ColorWriteMask;

        return new VulkanFixedFunctionStateSnapshot(
            depthTestEnabled,
            depthWriteEnabled,
            depthCompareOp,
            stencilTestEnabled,
            frontStencilState,
            backStencilState,
            stencilWriteMask,
            colorWriteMask,
            cullMode,
            frontFace,
            blendEnabled,
            alphaToCoverageEnabled,
            colorBlendOp,
            alphaBlendOp,
            srcColor,
            dstColor,
            srcAlpha,
            dstAlpha);
    }
}
