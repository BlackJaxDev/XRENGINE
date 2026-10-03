using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>Captures unsupported authored state before native visibility replaces the live raster state.</summary>
public static class WebGpuAdvancedRasterStateContract
{
    public static EAdvancedNativeRasterStateFlags Capture(RenderingParameters? options)
    {
        if (options is null) return EAdvancedNativeRasterStateFlags.None;
        EAdvancedNativeRasterStateFlags flags = EAdvancedNativeRasterStateFlags.Captured;
        if (options.Winding != EWinding.CounterClockwise)
            flags |= EAdvancedNativeRasterStateFlags.WindingUnsupported;
        if (options.CullMode is not (ECullMode.None or ECullMode.Back))
            flags |= EAdvancedNativeRasterStateFlags.CullUnsupported;
        if (options.DepthTest is not { Enabled: ERenderParamUsage.Enabled, UpdateDepth: true, Function: EComparison.Lequal })
            flags |= EAdvancedNativeRasterStateFlags.DepthUnsupported;
        if (!options.WriteRed || !options.WriteGreen || !options.WriteBlue || !options.WriteAlpha)
            flags |= EAdvancedNativeRasterStateFlags.ColorMaskUnsupported;
        if (options.BlendModeAllDrawBuffers is { } globalBlend)
        {
            if (globalBlend.Enabled != ERenderParamUsage.Disabled)
                flags |= EAdvancedNativeRasterStateFlags.BlendUnsupported;
        }
        else if (options.BlendModesPerDrawBuffer is { } perBuffer)
            foreach (KeyValuePair<uint, BlendMode> entry in perBuffer)
                if (entry.Value is not { Enabled: ERenderParamUsage.Disabled })
                    flags |= EAdvancedNativeRasterStateFlags.BlendUnsupported;
        if (options.StencilTest is null || options.StencilTest.Enabled is not (ERenderParamUsage.Disabled or ERenderParamUsage.Unchanged))
            flags |= EAdvancedNativeRasterStateFlags.StencilUnsupported;
        if (options.AlphaToCoverage != ERenderParamUsage.Disabled)
            flags |= EAdvancedNativeRasterStateFlags.AlphaToCoverageUnsupported;
        return flags;
    }

    public static string? GetRejection(EAdvancedNativeRasterStateFlags flags)
    {
        if ((flags & EAdvancedNativeRasterStateFlags.Captured) == 0)
            return "Native visibility requires the exact retained authored raster-state admission.";
        if ((flags & EAdvancedNativeRasterStateFlags.WindingUnsupported) != 0)
            return "Native visibility requires counter-clockwise winding; clockwise winding has no native raster companion.";
        if ((flags & EAdvancedNativeRasterStateFlags.CullUnsupported) != 0)
            return "Native visibility admits disabled or back-face culling only.";
        if ((flags & EAdvancedNativeRasterStateFlags.DepthUnsupported) != 0)
            return "Native visibility requires enabled Lequal depth testing and depth writes.";
        if ((flags & EAdvancedNativeRasterStateFlags.ColorMaskUnsupported) != 0)
            return "Native shading requires all RGBA color writes; partial material color masks have no native shading companion.";
        if ((flags & EAdvancedNativeRasterStateFlags.BlendUnsupported) != 0)
            return "Native opaque visibility requires disabled blending on every draw buffer.";
        if ((flags & EAdvancedNativeRasterStateFlags.StencilUnsupported) != 0)
            return "Native visibility does not admit authored stencil testing or writes.";
        if ((flags & EAdvancedNativeRasterStateFlags.AlphaToCoverageUnsupported) != 0)
            return "Native visibility does not admit authored alpha-to-coverage state.";
        return null;
    }
}
