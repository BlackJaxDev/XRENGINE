using XREngine.Components.Lights;

namespace XREngine.Rendering;

/// <summary>
/// One scheduled portal or mirror sub-view entry inside an <see cref="AdvancedPortalFramePlan"/>.
/// </summary>
public readonly record struct AdvancedScheduledPortalView(
    uint ViewIndex,
    uint PrimaryViewIndex,
    uint ParentViewIndex,
    uint StencilNodeId,
    uint ParentStencilNodeId,
    uint RecursionDepth,
    ulong ProviderId,
    uint ExcludedDrawIndex,
    float ProjectedArea,
    AdvancedPortalScissorRect ScissorRect,
    bool InvertedWinding,
    bool RenderTransparents,
    EMirrorMsaaShadingMode MsaaShadingMode,
    AdvancedPortalSurfaceDescriptor Surface);
