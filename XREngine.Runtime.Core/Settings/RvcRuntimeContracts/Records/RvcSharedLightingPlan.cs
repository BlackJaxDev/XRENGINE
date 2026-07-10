using System.Numerics;

namespace XREngine;

public readonly record struct RvcSharedLightingPlan(
    bool UseExistingForwardPlusLightMetadata,
    bool HeapBackedResourceReferences,
    bool KeepPerViewForwardPlusTileGridFallback,
    bool ReservoirEvaluationEnabled,
    RvcLightClusterGridDescriptor ClusterGrid)
{
    public static RvcSharedLightingPlan CreateDefault(Vector3 cameraRelativeOrigin)
        => new(
            UseExistingForwardPlusLightMetadata: true,
            HeapBackedResourceReferences: true,
            KeepPerViewForwardPlusTileGridFallback: true,
            ReservoirEvaluationEnabled: true,
            RvcLightClusterGridDescriptor.CreateDefault(cameraRelativeOrigin));
}
