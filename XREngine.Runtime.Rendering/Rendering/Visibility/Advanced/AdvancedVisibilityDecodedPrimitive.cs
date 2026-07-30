namespace XREngine.Rendering;

/// <summary>
/// Decoded classic or meshlet/cluster primitive identity.
/// </summary>
public readonly record struct AdvancedVisibilityDecodedPrimitive(
    bool IsValid,
    bool IsMeshletOrCluster,
    uint PrimitiveIndex,
    uint MeshletOrClusterIndex,
    uint LocalPrimitiveIndex)
{
    public static AdvancedVisibilityDecodedPrimitive Invalid => default;
}
