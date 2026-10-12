using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMaterial
{
    private ShaderProgramArtifact ResolveOctahedralImpostorArtifact()
    {
        ValidateOctahedralImpostorMaterial();
        EngineMaterialVariantKey key = EngineOctahedralImpostorShaderContract.Key();
        if (Renderer.MaterialVariants?.TryResolve(key, out ShaderProgramArtifact? artifact) != true || artifact is null)
            throw new NotSupportedException($"WebGPU.Impostor.CookedContractMissing: '{Data.Name}' requires exact '{key}' with the canonical desktop source closure.");
        if (!EngineOctahedralImpostorShaderProvenance.TryValidate(artifact, orderGate: false, out string reason))
            throw new NotSupportedException($"WebGPU.Impostor.CookedContractMismatch: '{Data.Name}': {reason}");
        return artifact;
    }

    private void ValidateOctahedralImpostorMaterial()
    {
        if (Data.Shaders.Count != 0 || Data.Parameters.Length != 0 || Data.SurfaceTextureBindings.Length != 0 ||
            Data.BillboardMode != EMeshBillboardMode.None || Data.RenderPass != (int)EDefaultRenderPass.TransparentForward ||
            Data.GetEffectiveTransparencyMode() != ETransparencyMode.AlphaBlend ||
            Data.AdvancedLatePassMetadata is not { Kind: EAdvancedLatePassKind.SortedAlpha, IsOrderDependent: true,
                RequiresSceneColorSnapshot: false, ParticipatesInMotionVectors: false, WritesDepth: false, UnsupportedReason: null })
            throw new NotSupportedException("WebGPU.Impostor.MaterialProfile: the exact component-owned source-free transparent-forward material is required.");
        if (Data.Textures.Count != 1 || Data.Textures[0] is not XRTexture2DArray
            { Depth: EngineOctahedralImpostorShaderContract.ViewCount, MultiSample: false, Width: > 0, Height: > 0,
                SizedInternalFormat: ESizedInternalFormat.Rgba16f })
            throw new NotSupportedException("WebGPU.Impostor.TextureProfile: exactly 26 single-sample linear RGBA16F views are required.");
    }

    private bool TryPublishOctahedralImpostor()
    {
        if (Data.EngineSemantic != EngineMaterialSemanticIdentity.OctahedralImpostorV1) return false;
        ValidateOctahedralImpostorMaterial();
        WebGpuFrameBuffer? target = Renderer.GetBoundEngineFrameBuffer();
        if (target is null || !target.HasDepth || target.SampleCount is not (1 or 4) ||
            target.ColorFormats.Length != 1 || target.ColorFormats[0] != "rgba16float")
            throw new NotSupportedException("WebGPU.Impostor.OutputProfile: the authored blended color requires one linear RGBA16F target with matching one or four depth samples.");
        WebGpuRasterState state = Renderer.RasterState;
        if (!state.DepthEnabled || state.DepthWrite ||
            state.DepthComparison != Renderer.MapAuthoredDepthComparison(EComparison.Lequal) ||
            state.CullMode != ECullMode.None || state.ColorWriteMask != 15 || !state.BlendEnabled ||
            state.SourceRgb != EBlendingFactor.SrcAlpha || state.DestinationRgb != EBlendingFactor.OneMinusSrcAlpha ||
            state.SourceAlpha != EBlendingFactor.SrcAlpha || state.DestinationAlpha != EBlendingFactor.OneMinusSrcAlpha ||
            state.RgbEquation != EBlendEquationMode.FuncAdd || state.AlphaEquation != EBlendEquationMode.FuncAdd)
            throw new NotSupportedException("WebGPU.Impostor.RasterProfile: canonical impostors require standard transparent blending, authored less-equal depth, no depth writes and no face culling.");
        Program.Data.Sampler("ImposterViews", Data.Textures[0]!, 0);
        return true;
    }
}
