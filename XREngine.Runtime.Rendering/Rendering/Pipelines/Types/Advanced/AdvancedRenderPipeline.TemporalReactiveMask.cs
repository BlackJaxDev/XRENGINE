using System.IO;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    /// <summary>Includes the canonical opaque/late reactive mask only in Advanced resolves.</summary>
    private static XRShader CreateAdvancedTemporalShader(string fileName)
        => ShaderHelper.CreateDefinedShaderVariant(
            XRShader.EngineShader(Path.Combine(SceneShaderPath, fileName), EShaderType.Fragment),
            "XR_ADVANCED_REACTIVE_MASK")
        ?? throw new InvalidOperationException($"Advanced temporal shader '{fileName}' is unavailable.");

    /// <summary>Resolves jittered Advanced inputs onto a stable TSR output/history grid.</summary>
    private XRShader CreateAdvancedTsrShader(string fileName)
    {
        XRShader? shader = ShaderHelper.CreateDefinedShaderVariant(
            CreateAdvancedTemporalShader(fileName), "XR_TSR_STABLE_OUTPUT");
        shader = ShaderHelper.CreateDefinedShaderVariant(shader, "XR_SCENE_STENCIL_AUTHORED");
        if (EditorHighlightModule is not null)
            shader = ShaderHelper.CreateDefinedShaderVariant(shader, "XR_EDITOR_HIGHLIGHT_MODULE");
        return shader ?? throw new InvalidOperationException($"Advanced TSR shader '{fileName}' is unavailable.");
    }

    /// <summary>
    /// Full TSR resolve of the edge stencil split, with the coverage path. It draws only where the
    /// edge stencil bit is set, and it does not discard, so early stencil rejection stays active.
    /// </summary>
    private XRShader CreateAdvancedTsrSplitShader(string fileName)
        => ShaderHelper.CreateDefinedShaderVariant(CreateAdvancedTsrShader(fileName), "XR_TSR_STENCIL_SPLIT")
        ?? throw new InvalidOperationException($"Advanced TSR split shader '{fileName}' is unavailable.");

    /// <summary>
    /// Lean TSR resolve of the edge stencil split. It draws only where the edge stencil bit is
    /// clear; its coverage resolve returns at once with the result that a failed classification
    /// gives, so the compiler drops the coverage path and the shader needs far fewer registers.
    /// </summary>
    private XRShader CreateAdvancedTsrLeanShader(string fileName)
        => ShaderHelper.CreateDefinedShaderVariant(CreateAdvancedTsrSplitShader(fileName), "XR_TSR_LEAN_RESOLVE")
        ?? throw new InvalidOperationException($"Advanced TSR lean shader '{fileName}' is unavailable.");

    /// <summary>
    /// Edge stencil classify pass: the TSR shader source with <c>XR_TSR_EDGE_CLASSIFY</c>. It maps each
    /// output pixel to its source UV as the resolve does and keeps it only where the edge mask is set.
    /// </summary>
    private XRShader CreateAdvancedTsrEdgeClassifyShader(string fileName)
        => ShaderHelper.CreateDefinedShaderVariant(
            ShaderHelper.CreateDefinedShaderVariant(CreateAdvancedTsrShader(fileName), "XR_TSR_EDGE_MASK"),
            "XR_TSR_EDGE_CLASSIFY")
        ?? throw new InvalidOperationException($"Advanced TSR edge classify shader '{fileName}' is unavailable.");

    /// <summary>
    /// Edge-mask pass: the same TSR shader source with <c>XR_TSR_EDGE_MASK_PASS</c>, so its
    /// declarations, loaders and classification code are identical to the resolve. Its main runs
    /// the current-frame coverage classification once per internal pixel and writes 1 where it
    /// passes.
    /// </summary>
    private XRShader CreateAdvancedTsrEdgeMaskShader(string fileName)
        => ShaderHelper.CreateDefinedShaderVariant(
            CreateAdvancedTsrShader(fileName),
            "XR_TSR_EDGE_MASK_PASS")
        ?? throw new InvalidOperationException($"Advanced TSR edge-mask shader '{fileName}' is unavailable.");

    private void BindAdvancedTemporalReactiveMask(XRRenderProgram program)
        => program.Sampler("AdvancedReactiveMask",
            GetTexture<XRTexture>(AdvancedTemporalHistoryContract.ReactiveMaskResourceName)
                ?? throw new InvalidOperationException("Advanced temporal resolve requires its published reactive mask."),
            6);
}
