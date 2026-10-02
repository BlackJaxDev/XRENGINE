namespace XREngine.Core.Files;

/// <summary>
/// Parsed fixed header of a cooked asset envelope. Offsets are relative to the start of the
/// envelope bytes so the payload can be sliced without copying.
/// </summary>
public readonly record struct CookedAssetEnvelopeHeader(
    ushort Version,
    CookedAssetFormat Format,
    int TypeReferenceOffset,
    int TypeReferenceLength,
    int PayloadOffset,
    int PayloadLength)
{
    /// <summary>Slices the UTF-8 type reference out of the envelope.</summary>
    public ReadOnlySpan<byte> TypeReferenceUtf8(ReadOnlySpan<byte> envelope)
        => envelope.Slice(TypeReferenceOffset, TypeReferenceLength);

    /// <summary>Slices the payload out of the envelope.</summary>
    public ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> envelope)
        => envelope.Slice(PayloadOffset, PayloadLength);

    /// <summary>Decodes the type reference. Allocates one string; use <see cref="TypeReferenceUtf8"/> to compare without allocating.</summary>
    public string DecodeTypeReference(ReadOnlySpan<byte> envelope)
        => System.Text.Encoding.UTF8.GetString(TypeReferenceUtf8(envelope));
}
