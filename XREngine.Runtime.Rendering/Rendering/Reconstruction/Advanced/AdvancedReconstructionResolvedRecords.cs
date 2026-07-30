using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// CPU reference result for the generation-checked records behind one pixel.
/// </summary>
public readonly record struct AdvancedReconstructionResolvedRecords(
    AdvancedGpuHandle DrawHandle,
    AdvancedVisibilityLogicalSurface Surface,
    AdvancedDrawRecord Draw,
    AdvancedInstanceRecord Instance,
    AdvancedGeometryRecord Geometry,
    AdvancedMaterialRecord Material,
    AdvancedShadingKernelRecord ShadingKernel,
    AdvancedTransformRecord CurrentTransform,
    AdvancedTransformRecord PreviousTransform,
    AdvancedDeformationRecord Deformation,
    AdvancedViewRecord View,
    bool HasDeformation);
