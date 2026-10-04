using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMaterial
{
    /// <summary>Rejects incompatible overrides instead of changing authored blend/depth state.</summary>
    private void ValidateCoverageRasterState(in StandardLitColorSurface surface)
        => ValidateCoverageRasterState(surface.TransparencyMode);

    private void ValidateCoverageRasterState(ETransparencyMode mode)
    {
        WebGpuRasterState state = Renderer.RasterState;
        if (_litAuxiliaryPass != EStandardLitColorAuxiliaryPass.None &&
            mode is not (ETransparencyMode.Opaque or ETransparencyMode.Masked))
            throw new NotSupportedException("WebGPU.Material.AuxiliaryCoverageUnsupported: sorted transparent surfaces cannot enter depth or shadow replay.");
        bool blended = _litAuxiliaryPass == EStandardLitColorAuxiliaryPass.None &&
            mode is ETransparencyMode.AlphaBlend or ETransparencyMode.PremultipliedAlpha or ETransparencyMode.Additive;
        if (!state.DepthEnabled || state.DepthWrite == blended || state.BlendEnabled != blended)
            throw new NotSupportedException("WebGPU.Material.CoverageRasterUnsupported: lit coverage requires depth testing, depth writes for opaque/masked replay, and blending without depth writes for sorted surfaces.");
        // The normal replay and following color must both admit the existing
        // surface depth. Other comparisons need a separately defined prepass contract.
        if (state.DepthComparison != Renderer.MapAuthoredDepthComparison(EComparison.Lequal))
            throw new NotSupportedException("WebGPU.Material.CoverageDepthUnsupported: the shared coverage prepass contract requires authored Lequal depth comparison.");
        if (!blended)
            return;
        EBlendingFactor source = mode == ETransparencyMode.PremultipliedAlpha
            ? EBlendingFactor.One : EBlendingFactor.SrcAlpha;
        EBlendingFactor destination = mode == ETransparencyMode.Additive
            ? EBlendingFactor.One : EBlendingFactor.OneMinusSrcAlpha;
        if (state.SourceRgb != source || state.SourceAlpha != source ||
            state.DestinationRgb != destination || state.DestinationAlpha != destination ||
            state.RgbEquation != EBlendEquationMode.FuncAdd || state.AlphaEquation != EBlendEquationMode.FuncAdd)
            throw new NotSupportedException("WebGPU.Material.CoverageBlendUnsupported: the raster state does not match the authored sorted-alpha mode.");
    }
}
