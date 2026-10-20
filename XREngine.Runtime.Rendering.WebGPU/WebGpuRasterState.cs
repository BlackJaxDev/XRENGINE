using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.WebGPU;

/// <summary>Immutable raster state used in engine graphics pipeline identity.</summary>
internal readonly record struct WebGpuRasterState(
    bool DepthEnabled,
    bool DepthWrite,
    EComparison DepthComparison,
    ECullMode CullMode,
    EWinding Winding,
    int ColorWriteMask,
    bool BlendEnabled,
    EBlendingFactor SourceRgb,
    EBlendingFactor DestinationRgb,
    EBlendingFactor SourceAlpha,
    EBlendingFactor DestinationAlpha,
    EBlendEquationMode RgbEquation,
    EBlendEquationMode AlphaEquation)
{
    public static WebGpuRasterState Default => new(true, true, EComparison.Lequal,
        ECullMode.Back, EWinding.CounterClockwise, 15, false,
        EBlendingFactor.One, EBlendingFactor.Zero, EBlendingFactor.One, EBlendingFactor.Zero,
        EBlendEquationMode.FuncAdd, EBlendEquationMode.FuncAdd);
}
