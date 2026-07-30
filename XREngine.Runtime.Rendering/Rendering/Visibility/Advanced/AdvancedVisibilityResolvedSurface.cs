using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Scene-table identities reachable from a visibility pixel without a managed-object lookup.
/// </summary>
public readonly record struct AdvancedVisibilityResolvedSurface(
    AdvancedVisibilityLogicalSurface Surface,
    AdvancedGpuHandle Instance,
    AdvancedGpuHandle Geometry,
    AdvancedGpuHandle Material,
    AdvancedGpuHandle CurrentTransform,
    AdvancedGpuHandle PreviousTransform,
    AdvancedGpuHandle EditorIdentity,
    uint ShadingKernelId,
    uint PrimitiveSection);
