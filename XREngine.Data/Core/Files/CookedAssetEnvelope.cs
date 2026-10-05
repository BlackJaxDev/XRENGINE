using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace XREngine.Core.Files;

/// <summary>
/// Fixed binary envelope around a cooked asset payload. The header is read from a span and the
/// payload is sliced in place, so loading never materializes the payload as a separate array.
/// </summary>
/// <remarks>
/// Layout (little-endian):
/// <list type="bullet">
/// <item>bytes 0..3: magic <c>XRCA</c></item>
/// <item>bytes 4..5: envelope version</item>
/// <item>byte 6: <see cref="CookedAssetFormat"/></item>
/// <item>byte 7: reserved, zero</item>
/// <item>bytes 8..9: type reference length in UTF-8 bytes</item>
/// <item>bytes 10..11: reserved, zero</item>
/// <item>bytes 12..15: payload offset from the start of the envelope</item>
/// <item>bytes 16..19: payload length</item>
/// <item>bytes 20..: type reference, then padding to an 8-byte boundary, then the payload</item>
/// </list>
/// The type reference is the string produced by <see cref="CookedAssetTypeReference.Encode"/>:
/// either an assembly-qualified name or an <c>aot:</c> index into published metadata.
/// </remarks>
public static class CookedAssetEnvelope
{
    public const uint Magic = 0x41435258; // "XRCA"
    public const ushort CurrentVersion = 2;
    public const int HeaderSize = 20;
    public const int PayloadAlignment = 8;
    public const int MaxTypeReferenceBytes = ushort.MaxValue;

    /// <summary>Guidance appended to every rejection so the user knows how to recover.</summary>
    public const string RecookGuidance = "Re-cook the content with the current editor build; cooked archives from earlier envelope versions are not readable.";

    /// <summary>Total envelope size for a type reference of <paramref name="typeReferenceUtf8Length"/> bytes and a payload of <paramref name="payloadLength"/> bytes.</summary>
    public static int GetEnvelopeSize(int typeReferenceUtf8Length, int payloadLength)
        => GetPayloadOffset(typeReferenceUtf8Length) + payloadLength;

    public static int GetPayloadOffset(int typeReferenceUtf8Length)
        => Align(HeaderSize + typeReferenceUtf8Length, PayloadAlignment);

    /// <summary>Writes a complete envelope to <paramref name="writer"/>.</summary>
    public static void Write(IBufferWriter<byte> writer, string typeReference, CookedAssetFormat format, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeReference);

        int typeReferenceLength = Encoding.UTF8.GetByteCount(typeReference);
        if (typeReferenceLength > MaxTypeReferenceBytes)
            throw new ArgumentOutOfRangeException(nameof(typeReference), $"Type reference exceeds {MaxTypeReferenceBytes} UTF-8 bytes.");

        int payloadOffset = GetPayloadOffset(typeReferenceLength);
        int total = payloadOffset + payload.Length;
        Span<byte> destination = writer.GetSpan(total);
        destination[..total].Clear();

        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], CurrentVersion);
        destination[6] = (byte)format;
        destination[7] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], (ushort)typeReferenceLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], payloadOffset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], payload.Length);
        Encoding.UTF8.GetBytes(typeReference, destination.Slice(HeaderSize, typeReferenceLength));
        payload.CopyTo(destination.Slice(payloadOffset, payload.Length));
        writer.Advance(total);
    }

    /// <summary>Serializes an authoring blob into a new array. Intended for cooking and tests.</summary>
    public static byte[] Serialize(in CookedAssetBlob blob)
    {
        ArgumentNullException.ThrowIfNull(blob.Payload);
        ArrayBufferWriter<byte> writer = new(GetEnvelopeSize(Encoding.UTF8.GetByteCount(blob.TypeReference), blob.Payload.Length));
        Write(writer, blob.TypeReference, blob.Format, blob.Payload);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Parses an envelope into an authoring blob, copying the payload. Intended for tooling.</summary>
    public static CookedAssetBlob Deserialize(ReadOnlySpan<byte> envelope)
    {
        CookedAssetEnvelopeHeader header = ParseHeader(envelope);
        return new CookedAssetBlob(header.DecodeTypeReference(envelope), header.Format, header.Payload(envelope).ToArray());
    }

    /// <summary>Parses the header or throws <see cref="InvalidDataException"/> with an actionable message.</summary>
    public static CookedAssetEnvelopeHeader ParseHeader(ReadOnlySpan<byte> envelope)
    {
        if (!TryParseHeader(envelope, out CookedAssetEnvelopeHeader header, out string? error))
            throw new InvalidDataException(error);
        return header;
    }

    /// <summary>Parses the header without throwing. <paramref name="error"/> explains a rejection and how to recover.</summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> envelope, out CookedAssetEnvelopeHeader header, out string? error)
    {
        header = default;
        if (envelope.Length < HeaderSize)
        {
            error = envelope.IsEmpty
                ? "Cooked asset envelope is empty."
                : $"Cooked asset envelope is {envelope.Length} bytes, shorter than the {HeaderSize}-byte header. {RecookGuidance}";
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(envelope);
        if (magic != Magic)
        {
            error = LooksLikeLegacyMemoryPackEnvelope(envelope)
                ? $"Cooked asset was written with the previous MemoryPack envelope (format version 1), which this runtime no longer reads. {RecookGuidance}"
                : $"Cooked asset envelope magic 0x{magic:X8} does not match 0x{Magic:X8}. {RecookGuidance}";
            return false;
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(envelope[4..]);
        if (version != CurrentVersion)
        {
            error = $"Cooked asset envelope version {version} is not supported; this runtime reads version {CurrentVersion}. {RecookGuidance}";
            return false;
        }

        CookedAssetFormat format = (CookedAssetFormat)envelope[6];
        int typeReferenceLength = BinaryPrimitives.ReadUInt16LittleEndian(envelope[8..]);
        int payloadOffset = BinaryPrimitives.ReadInt32LittleEndian(envelope[12..]);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(envelope[16..]);

        if (typeReferenceLength == 0
            || HeaderSize + typeReferenceLength > envelope.Length
            || payloadOffset < HeaderSize + typeReferenceLength
            || payloadLength < 0
            || (long)payloadOffset + payloadLength > envelope.Length)
        {
            error = $"Cooked asset envelope header is inconsistent with its {envelope.Length}-byte buffer (type reference {typeReferenceLength} bytes, payload at {payloadOffset} for {payloadLength} bytes). {RecookGuidance}";
            return false;
        }

        header = new CookedAssetEnvelopeHeader(version, format, HeaderSize, typeReferenceLength, payloadOffset, payloadLength);
        error = null;
        return true;
    }

    /// <summary>True when the bytes carry a current envelope header.</summary>
    public static bool IsEnvelope(ReadOnlySpan<byte> bytes)
        => bytes.Length >= HeaderSize && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic;

    private static bool LooksLikeLegacyMemoryPackEnvelope(ReadOnlySpan<byte> bytes)
    {
        // The previous envelope was a MemoryPack struct with three members: a string, a byte enum,
        // and a byte array. Its first byte is the member count.
        return bytes.Length > 4 && bytes[0] == 3;
    }

    private static int Align(int value, int alignment)
        => (value + alignment - 1) & ~(alignment - 1);
}
