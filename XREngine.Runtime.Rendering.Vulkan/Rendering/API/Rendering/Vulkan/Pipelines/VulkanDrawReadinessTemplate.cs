using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Builds the draw that the readiness prewarm compiles for a T0 renderer from a draw of the same pass (the template). The
/// fixed-function state comes from the pass state of the template (<see cref="PendingMeshDraw.PassState"/>) and the render
/// options of the T0 draw, with the same rules as the draw enqueue (<see cref="VulkanMeshDrawFixedState"/>). Nothing comes
/// from the material of the template, so the pipeline key matches the real T0 draw in that pass.
/// </summary>
internal static class VulkanDrawReadinessTemplate
{
    /// <summary>Builds the prewarm draw.</summary>
    /// <param name="template">A draw of the pass and target, from the frame plan.</param>
    /// <param name="renderer">The T0 renderer.</param>
    /// <param name="material">The material of the T0 draw.</param>
    /// <param name="renderOptionsOverride">The render options override of the T0 renderable, or null.</param>
    public static PendingMeshDraw Build(
        in PendingMeshDraw template,
        VkMeshRenderer renderer,
        XRMaterial material,
        RenderingParameters? renderOptionsOverride)
    {
        VulkanFixedFunctionStateSnapshot drawState = VulkanMeshDrawFixedState.Resolve(
            renderOptionsOverride ?? material.RenderOptions,
            template.PassState,
            template.RasterizationSamples);
        return template with
        {
            Renderer = renderer,
            DepthTestEnabled = drawState.DepthTestEnabled,
            DepthWriteEnabled = drawState.DepthWriteEnabled,
            DepthCompareOp = drawState.DepthCompareOp,
            StencilTestEnabled = drawState.StencilTestEnabled,
            FrontStencilState = drawState.FrontStencilState,
            BackStencilState = drawState.BackStencilState,
            StencilWriteMask = drawState.StencilWriteMask,
            ColorWriteMask = drawState.ColorWriteMask,
            CullMode = drawState.CullMode,
            FrontFace = drawState.FrontFace,
            BlendEnabled = drawState.BlendEnabled,
            AlphaToCoverageEnabled = drawState.AlphaToCoverageEnabled,
            ColorBlendOp = drawState.ColorBlendOp,
            AlphaBlendOp = drawState.AlphaBlendOp,
            SrcColorBlendFactor = drawState.SrcColorBlendFactor,
            DstColorBlendFactor = drawState.DstColorBlendFactor,
            SrcAlphaBlendFactor = drawState.SrcAlphaBlendFactor,
            DstAlphaBlendFactor = drawState.DstAlphaBlendFactor,
            MaterialOverride = material,
            Instances = 1u,
            PreparedProgram = null,
            PreparedProgramIdentity = null,
            PreparedProgramLinkGeneration = 0UL,
            ProgramBindingSnapshot = null,
        };
    }

    /// <summary>Gives the fixed-function state of a draw, in the shape of <see cref="VulkanFixedFunctionStateSnapshot"/>.</summary>
    public static VulkanFixedFunctionStateSnapshot GetDrawState(in PendingMeshDraw draw)
        => new(
            draw.DepthTestEnabled,
            draw.DepthWriteEnabled,
            draw.DepthCompareOp,
            draw.StencilTestEnabled,
            draw.FrontStencilState,
            draw.BackStencilState,
            draw.StencilWriteMask,
            draw.ColorWriteMask,
            draw.CullMode,
            draw.FrontFace,
            draw.BlendEnabled,
            draw.AlphaToCoverageEnabled,
            draw.ColorBlendOp,
            draw.AlphaBlendOp,
            draw.SrcColorBlendFactor,
            draw.DstColorBlendFactor,
            draw.SrcAlphaBlendFactor,
            draw.DstAlphaBlendFactor);
}
