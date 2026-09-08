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

    private void BindAdvancedTemporalReactiveMask(XRRenderProgram program)
        => program.Sampler("AdvancedReactiveMask",
            GetTexture<XRTexture>(AdvancedTemporalHistoryContract.ReactiveMaskResourceName)
                ?? throw new InvalidOperationException("Advanced temporal resolve requires its published reactive mask."),
            6);
}
