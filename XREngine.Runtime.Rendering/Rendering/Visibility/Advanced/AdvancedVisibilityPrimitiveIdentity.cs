namespace XREngine.Rendering;

/// <summary>
/// Exact primitive-word encoding shared by every visibility producer.
/// Indexed producers store a triangle ordinal directly. Mesh and future cluster
/// producers store a 24-bit meshlet/cluster index and an 8-bit local primitive.
/// The all-ones combination remains the global invalid sentinel.
/// </summary>
public static class AdvancedVisibilityPrimitiveIdentity
{
    public const uint MeshletIndexBits = 24u;
    public const uint LocalPrimitiveBits = 8u;
    public const uint MaximumMeshletIndex = (1u << (int)MeshletIndexBits) - 1u;
    public const uint MaximumLocalPrimitiveIndex =
        (1u << (int)LocalPrimitiveBits) - 1u;

    public static bool TryEncodeIndexed(
        uint triangleIndex,
        out uint encoded,
        out EAdvancedVisibilityPayloadOverflow overflow)
    {
        if (triangleIndex == AdvancedVisibilityBufferContract.InvalidWord)
        {
            encoded = AdvancedVisibilityBufferContract.InvalidWord;
            overflow = EAdvancedVisibilityPayloadOverflow.PrimitiveIndex;
            return false;
        }

        encoded = triangleIndex;
        overflow = EAdvancedVisibilityPayloadOverflow.None;
        return true;
    }

    public static bool TryEncodeMeshlet(
        uint meshletIndex,
        uint localPrimitiveIndex,
        out uint encoded,
        out EAdvancedVisibilityPayloadOverflow overflow)
    {
        if (meshletIndex > MaximumMeshletIndex ||
            localPrimitiveIndex > MaximumLocalPrimitiveIndex)
        {
            encoded = AdvancedVisibilityBufferContract.InvalidWord;
            overflow = EAdvancedVisibilityPayloadOverflow.PrimitiveIndex;
            return false;
        }

        encoded =
            (meshletIndex << (int)LocalPrimitiveBits) |
            localPrimitiveIndex;
        if (encoded == AdvancedVisibilityBufferContract.InvalidWord)
        {
            overflow = EAdvancedVisibilityPayloadOverflow.PrimitiveIndex;
            return false;
        }

        overflow = EAdvancedVisibilityPayloadOverflow.None;
        return true;
    }

    public static AdvancedVisibilityDecodedPrimitive Decode(
        uint encoded,
        EAdvancedGeometryProducer producer)
    {
        if (encoded == AdvancedVisibilityBufferContract.InvalidWord)
            return AdvancedVisibilityDecodedPrimitive.Invalid;

        return producer is
            EAdvancedGeometryProducer.StaticMeshlet or
            EAdvancedGeometryProducer.SkinnedMeshlet
                ? new AdvancedVisibilityDecodedPrimitive(
                    IsValid: true,
                    IsMeshletOrCluster: true,
                    PrimitiveIndex: 0u,
                    MeshletOrClusterIndex:
                        encoded >> (int)LocalPrimitiveBits,
                    LocalPrimitiveIndex:
                        encoded & MaximumLocalPrimitiveIndex)
                : new AdvancedVisibilityDecodedPrimitive(
                    IsValid: true,
                    IsMeshletOrCluster: false,
                    PrimitiveIndex: encoded,
                    MeshletOrClusterIndex: 0u,
                    LocalPrimitiveIndex: 0u);
    }
}
