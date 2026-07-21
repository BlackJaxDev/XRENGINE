namespace XREngine;

/// <summary>Accelerated multiview lanes that require explicit validation before promotion.</summary>
public enum EGpuMultiViewPromotionLane : byte
{
    ExternalOpenXrPerFamilyCulling,
    MeshletStereoQuadOcclusion,
}
