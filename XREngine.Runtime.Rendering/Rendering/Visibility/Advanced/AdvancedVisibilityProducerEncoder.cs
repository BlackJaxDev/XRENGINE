namespace XREngine.Rendering;

/// <summary>
/// Applies the one logical payload contract to every supported geometry producer.
/// </summary>
public static class AdvancedVisibilityProducerEncoder
{
    public static bool TryEncode(
        in AdvancedVisibilityPayload payload,
        in AdvancedVisibilityPrimitiveReference primitive,
        EAdvancedGeometryProducer producer,
        EAdvancedVisibilityRasterOrigin origin,
        uint viewIndex,
        uint selectionId,
        bool frontFace,
        bool velocityValid,
        out AdvancedVisibilityEncodedSurface encoded,
        out EAdvancedVisibilityPayloadOverflow overflow)
    {
        if (!IsCompatible(payload, producer))
        {
            encoded = AdvancedVisibilityEncodedSurface.Invalid;
            overflow = EAdvancedVisibilityPayloadOverflow.InvalidProducerPayload;
            return false;
        }
        if (!primitive.TryEncode(
                producer,
                out uint encodedPrimitive,
                out overflow))
        {
            encoded = AdvancedVisibilityEncodedSurface.Invalid;
            return false;
        }
        if (!AdvancedVisibilityBufferContract.TryEncodeIdentity(
                payload.Draw,
                encodedPrimitive,
                out AdvancedVisibilityPayloadWords identity,
                out overflow))
        {
            encoded = AdvancedVisibilityEncodedSurface.Invalid;
            return false;
        }
        if (!AdvancedVisibilityMetadataWord.TryCreate(
                producer,
                origin,
                payload.Coverage == EAdvancedMaterialCoverageMode.Masked,
                frontFace,
                velocityValid,
                viewIndex,
                AdvancedVisibilityBufferContract.PayloadVersion,
                selectionId != AdvancedVisibilityBufferContract.InvalidWord,
                out AdvancedVisibilityMetadataWord metadata,
                out overflow))
        {
            encoded = AdvancedVisibilityEncodedSurface.Invalid;
            return false;
        }

        encoded = new AdvancedVisibilityEncodedSurface(
            identity,
            metadata,
            selectionId);
        return true;
    }

    public static bool IsCompatible(
        in AdvancedVisibilityPayload payload,
        EAdvancedGeometryProducer producer)
        => producer switch
        {
            EAdvancedGeometryProducer.CpuDirectStaticIndexed
                => !payload.Skinned,
            EAdvancedGeometryProducer.CpuDirectPreSkinned
                => payload.Skinned,
            EAdvancedGeometryProducer.IndirectIndexed
                => true,
            EAdvancedGeometryProducer.StaticMeshlet
                => payload.MeshletsResident && !payload.Skinned,
            EAdvancedGeometryProducer.SkinnedMeshlet
                => payload.MeshletsResident && payload.Skinned,
            _ => false,
        };
}
