namespace XREngine.Rendering;

/// <summary>
/// Compact producer/view/coverage metadata stored beside the two identity words.
/// </summary>
public readonly record struct AdvancedVisibilityMetadataWord(uint Value)
{
    private const uint ProducerMask = 0x7u;
    private const int OriginShift = 3;
    private const int MaskedShift = 4;
    private const int FrontFaceShift = 5;
    private const int VelocityValidShift = 6;
    private const int ViewShift = 8;
    private const uint ViewMask = 0xFFu;
    private const int VersionShift = 16;
    private const uint VersionMask = 0xFFu;
    private const int SelectionValidShift = 24;

    public const uint MaximumViewCount = ViewMask + 1u;
    public static AdvancedVisibilityMetadataWord Invalid
        => new(AdvancedVisibilityBufferContract.InvalidWord);

    public bool IsValid => Value != AdvancedVisibilityBufferContract.InvalidWord;

    public static bool TryCreate(
        EAdvancedGeometryProducer producer,
        EAdvancedVisibilityRasterOrigin origin,
        bool masked,
        bool frontFace,
        bool velocityValid,
        uint viewIndex,
        uint payloadVersion,
        bool selectionValid,
        out AdvancedVisibilityMetadataWord metadata,
        out EAdvancedVisibilityPayloadOverflow overflow)
    {
        if (producer is not (
            EAdvancedGeometryProducer.StaticMeshlet or
            EAdvancedGeometryProducer.SkinnedMeshlet or
            EAdvancedGeometryProducer.IndirectIndexed or
            EAdvancedGeometryProducer.CpuDirectStaticIndexed or
            EAdvancedGeometryProducer.CpuDirectPreSkinned))
        {
            metadata = Invalid;
            overflow = EAdvancedVisibilityPayloadOverflow.Producer;
            return false;
        }
        if (origin is not (
            EAdvancedVisibilityRasterOrigin.Early or
            EAdvancedVisibilityRasterOrigin.Late))
        {
            metadata = Invalid;
            overflow = EAdvancedVisibilityPayloadOverflow.RasterOrigin;
            return false;
        }
        if (viewIndex > ViewMask)
        {
            metadata = Invalid;
            overflow = EAdvancedVisibilityPayloadOverflow.ViewIndex;
            return false;
        }
        if (payloadVersion == 0u || payloadVersion > VersionMask)
        {
            metadata = Invalid;
            overflow = EAdvancedVisibilityPayloadOverflow.PayloadVersion;
            return false;
        }

        uint value = (uint)producer;
        value |= (uint)origin << OriginShift;
        value |= (masked ? 1u : 0u) << MaskedShift;
        value |= (frontFace ? 1u : 0u) << FrontFaceShift;
        value |= (velocityValid ? 1u : 0u) << VelocityValidShift;
        value |= viewIndex << ViewShift;
        value |= payloadVersion << VersionShift;
        value |= (selectionValid ? 1u : 0u) << SelectionValidShift;
        metadata = new AdvancedVisibilityMetadataWord(value);
        overflow = EAdvancedVisibilityPayloadOverflow.None;
        return true;
    }

    public AdvancedVisibilityDecodedMetadata Decode()
    {
        if (!IsValid)
            throw new InvalidOperationException("The invalid visibility metadata sentinel cannot be decoded.");

        return new AdvancedVisibilityDecodedMetadata(
            (EAdvancedGeometryProducer)(Value & ProducerMask),
            (EAdvancedVisibilityRasterOrigin)((Value >> OriginShift) & 0x1u),
            ((Value >> MaskedShift) & 0x1u) != 0u,
            ((Value >> FrontFaceShift) & 0x1u) != 0u,
            ((Value >> VelocityValidShift) & 0x1u) != 0u,
            (Value >> ViewShift) & ViewMask,
            (Value >> VersionShift) & VersionMask,
            ((Value >> SelectionValidShift) & 0x1u) != 0u);
    }
}
