using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Versioned backend-neutral contract for the first advanced visibility target.
/// </summary>
public static class AdvancedVisibilityBufferContract
{
    public const uint PayloadVersion = 1u;
    public const uint InvalidWord = uint.MaxValue;
    public const uint MaximumEncodableIndex = uint.MaxValue - 1u;
    public const EAdvancedVisibilityTargetEncoding Encoding =
        EAdvancedVisibilityTargetEncoding.R32G32UInt;
    public const string FormatDecision =
        "One RG32_UINT identity attachment (draw-table index, primitive index) plus an R32_UINT metadata sidecar.";
    public const string BarycentricDecision =
        "Reconstruct perspective-correct barycentrics from the indexed triangle and pixel center; no production barycentric attachment.";

    public static bool TryEncodeIdentity(
        AdvancedGpuHandle draw,
        uint primitiveIndex,
        out AdvancedVisibilityPayloadWords words,
        out EAdvancedVisibilityPayloadOverflow overflow)
    {
        if (!draw.IsValid)
        {
            words = AdvancedVisibilityPayloadWords.Invalid;
            overflow = EAdvancedVisibilityPayloadOverflow.InvalidDraw;
            return false;
        }
        if (draw.Index > MaximumEncodableIndex)
        {
            words = AdvancedVisibilityPayloadWords.Invalid;
            overflow = EAdvancedVisibilityPayloadOverflow.DrawIndex;
            return false;
        }
        if (primitiveIndex > MaximumEncodableIndex)
        {
            words = AdvancedVisibilityPayloadWords.Invalid;
            overflow = EAdvancedVisibilityPayloadOverflow.PrimitiveIndex;
            return false;
        }

        words = new AdvancedVisibilityPayloadWords(draw.Index, primitiveIndex);
        overflow = EAdvancedVisibilityPayloadOverflow.None;
        return true;
    }

    public static AdvancedVisibilityPayloadWords EncodeIdentityOrThrow(
        AdvancedGpuHandle draw,
        uint primitiveIndex)
    {
        if (TryEncodeIdentity(
                draw,
                primitiveIndex,
                out AdvancedVisibilityPayloadWords words,
                out EAdvancedVisibilityPayloadOverflow overflow))
        {
            return words;
        }

        throw new AdvancedVisibilityPayloadOverflowException(
            overflow,
            $"Visibility payload overflow ({overflow}) for draw {draw.Index}:{draw.Generation}, primitive {primitiveIndex}.");
    }
}
