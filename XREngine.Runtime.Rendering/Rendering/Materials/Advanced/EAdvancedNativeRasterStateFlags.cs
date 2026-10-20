namespace XREngine.Rendering;

/// <summary>Frozen native raster admission in the render-state word's otherwise unused high byte.</summary>
[Flags]
public enum EAdvancedNativeRasterStateFlags : uint
{
    None = 0,
    WindingUnsupported = 1u << 24,
    CullUnsupported = 1u << 25,
    DepthUnsupported = 1u << 26,
    ColorMaskUnsupported = 1u << 27,
    BlendUnsupported = 1u << 28,
    StencilUnsupported = 1u << 29,
    AlphaToCoverageUnsupported = 1u << 30,
    Captured = 1u << 31,
}
