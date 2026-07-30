namespace XREngine.Rendering;

/// <summary>
/// Maps one canonical triangle to the indexed and meshlet-local identities
/// emitted by different geometry producers.
/// </summary>
public readonly record struct AdvancedVisibilityPrimitiveReference(
    uint CanonicalPrimitiveIndex,
    uint MeshletOrClusterIndex,
    uint LocalPrimitiveIndex)
{
    public bool TryEncode(
        EAdvancedGeometryProducer producer,
        out uint encoded,
        out EAdvancedVisibilityPayloadOverflow overflow)
    {
        if (CanonicalPrimitiveIndex ==
            AdvancedVisibilityBufferContract.InvalidWord)
        {
            encoded = AdvancedVisibilityBufferContract.InvalidWord;
            overflow = EAdvancedVisibilityPayloadOverflow.PrimitiveIndex;
            return false;
        }

        return IsMeshletProducer(producer)
            ? AdvancedVisibilityPrimitiveIdentity.TryEncodeMeshlet(
                MeshletOrClusterIndex,
                LocalPrimitiveIndex,
                out encoded,
                out overflow)
            : AdvancedVisibilityPrimitiveIdentity.TryEncodeIndexed(
                CanonicalPrimitiveIndex,
                out encoded,
                out overflow);
    }

    public bool TryResolve(
        in AdvancedVisibilityDecodedPrimitive decoded,
        out uint canonicalPrimitiveIndex)
    {
        canonicalPrimitiveIndex =
            AdvancedVisibilityBufferContract.InvalidWord;
        if (!decoded.IsValid ||
            CanonicalPrimitiveIndex ==
                AdvancedVisibilityBufferContract.InvalidWord)
        {
            return false;
        }

        if (decoded.IsMeshletOrCluster)
        {
            if (decoded.MeshletOrClusterIndex != MeshletOrClusterIndex ||
                decoded.LocalPrimitiveIndex != LocalPrimitiveIndex)
            {
                return false;
            }
        }
        else if (decoded.PrimitiveIndex != CanonicalPrimitiveIndex)
        {
            return false;
        }

        canonicalPrimitiveIndex = CanonicalPrimitiveIndex;
        return true;
    }

    private static bool IsMeshletProducer(
        EAdvancedGeometryProducer producer)
        => producer is
            EAdvancedGeometryProducer.StaticMeshlet or
            EAdvancedGeometryProducer.SkinnedMeshlet;
}
