using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Complete visibility-buffer output for one rasterized surface sample.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedVisibilityEncodedSurface(
    AdvancedVisibilityPayloadWords Identity,
    AdvancedVisibilityMetadataWord Metadata,
    uint SelectionId)
{
    public static AdvancedVisibilityEncodedSurface Invalid => new(
        AdvancedVisibilityPayloadWords.Invalid,
        AdvancedVisibilityMetadataWord.Invalid,
        AdvancedVisibilityBufferContract.InvalidWord);

    public bool IsValid => Identity.IsValid && Metadata.IsValid;

    public AdvancedVisibilityLogicalSurface DecodeLogical()
    {
        if (!IsValid)
            throw new InvalidOperationException("The invalid visibility sentinel cannot resolve a surface.");

        AdvancedVisibilityDecodedMetadata metadata = Metadata.Decode();
        AdvancedVisibilityDecodedPrimitive primitive =
            AdvancedVisibilityPrimitiveIdentity.Decode(
                Identity.PrimitiveIndex,
                metadata.Producer);
        return new AdvancedVisibilityLogicalSurface(
            Identity.DrawTableIndex,
            metadata.Producer,
            primitive,
            metadata.SelectionValid
                ? SelectionId
                : AdvancedVisibilityBufferContract.InvalidWord,
            metadata.ViewIndex);
    }
}
