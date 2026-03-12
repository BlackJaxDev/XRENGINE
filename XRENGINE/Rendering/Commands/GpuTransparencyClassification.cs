using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

public static class GpuTransparencyClassification
{
    public static EGpuTransparencyDomain ResolveDomain(ETransparencyMode mode)
        => mode switch
        {
            ETransparencyMode.Masked or ETransparencyMode.AlphaToCoverage => EGpuTransparencyDomain.Masked,
            ETransparencyMode.AlphaBlend or
            ETransparencyMode.PremultipliedAlpha or
            ETransparencyMode.Additive or
            ETransparencyMode.WeightedBlendedOit => EGpuTransparencyDomain.TransparentApproximate,
            ETransparencyMode.PerPixelLinkedList or
            ETransparencyMode.DepthPeeling or
            ETransparencyMode.Stochastic or
            ETransparencyMode.TriangleSorted => EGpuTransparencyDomain.TransparentExact,
            _ => EGpuTransparencyDomain.OpaqueOrOther,
        };

    public static bool IsTransparentLike(ETransparencyMode mode)
        => ResolveDomain(mode) is EGpuTransparencyDomain.TransparentApproximate or EGpuTransparencyDomain.TransparentExact;
}