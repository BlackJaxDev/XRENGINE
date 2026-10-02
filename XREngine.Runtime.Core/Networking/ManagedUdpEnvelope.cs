using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XREngine.Networking;

public static class ManagedUdpEnvelope
{
    private static ReadOnlySpan<byte> Magic => "XRMU"u8;

    /// <summary>Total envelope size for an inner payload of <paramref name="payloadLength"/> bytes.</summary>
    public static int GetEnvelopeLength(int payloadLength)
        => ManagedUdpEnvelopeHeader.TotalHeaderLength + payloadLength;

    /// <summary>Allocating convenience for handshake and close messages. The data path uses <see cref="TryWrite"/>.</summary>
    public static byte[] Create(ManagedUdpEnvelopeHeader header, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> key)
    {
        if (payload.Length > BaseNetworkingManager.MaxInboundDatagramBytes - ManagedUdpEnvelopeHeader.TotalHeaderLength)
            throw new ArgumentOutOfRangeException(nameof(payload));

        byte[] result = new byte[GetEnvelopeLength(payload.Length)];
        if (!TryWrite(result, header, payload, key, out _))
            throw new InvalidOperationException("Managed UDP envelope could not be authenticated.");
        return result;
    }

    /// <summary>
    /// Writes an authenticated envelope into caller-owned storage without allocating. The inner
    /// payload may alias neither the header nor the tag region of <paramref name="destination"/>.
    /// </summary>
    public static bool TryWrite(Span<byte> destination, in ManagedUdpEnvelopeHeader header, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> key, out int bytesWritten)
    {
        bytesWritten = 0;
        if (payload.Length > BaseNetworkingManager.MaxInboundDatagramBytes - ManagedUdpEnvelopeHeader.TotalHeaderLength)
            return false;

        int total = GetEnvelopeLength(payload.Length);
        if (destination.Length < total)
            return false;

        WriteUnsignedHeader(destination, header, payload.Length);
        payload.CopyTo(destination[ManagedUdpEnvelopeHeader.UnsignedHeaderLength..]);
        int signedLength = ManagedUdpEnvelopeHeader.UnsignedHeaderLength + payload.Length;
        if (!HMACSHA256.TryHashData(key, destination[..signedLength], destination.Slice(signedLength, ManagedUdpEnvelopeHeader.TagLength), out int tagLength)
            || tagLength != ManagedUdpEnvelopeHeader.TagLength)
        {
            return false;
        }

        bytesWritten = total;
        return true;
    }

    public static bool TryRead(ReadOnlySpan<byte> datagram, out ManagedUdpEnvelopeHeader header, out ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> tag)
    {
        header = default;
        payload = default;
        tag = default;
        if (datagram.Length < ManagedUdpEnvelopeHeader.TotalHeaderLength
            || !datagram[..4].SequenceEqual(Magic)
            || datagram[4] != ManagedUdpEnvelopeHeader.Version
            || datagram[8] != 0 || datagram[9] != ManagedUdpEnvelopeHeader.UnsignedHeaderLength
            || datagram[10] != 0 || datagram[11] != 0)
        {
            return false;
        }

        ManagedUdpMessageKind kind = (ManagedUdpMessageKind)datagram[5];
        ManagedUdpDirection direction = (ManagedUdpDirection)datagram[6];
        if (kind is < ManagedUdpMessageKind.Hello or > ManagedUdpMessageKind.Close
            || direction is < ManagedUdpDirection.ClientToServer or > ManagedUdpDirection.ServerToClient
            || datagram[7] != 0)
        {
            return false;
        }

        uint wireLength = BinaryPrimitives.ReadUInt32BigEndian(datagram[12..16]);
        if (wireLength > BaseNetworkingManager.MaxInboundDatagramBytes - ManagedUdpEnvelopeHeader.TotalHeaderLength)
            return false;
        int length = (int)wireLength;
        if (datagram.Length != ManagedUdpEnvelopeHeader.TotalHeaderLength + length)
            return false;

        header = new(
            kind,
            direction,
            new Guid(datagram[16..32]),
            new Guid(datagram[32..48]),
            new Guid(datagram[48..64]),
            BinaryPrimitives.ReadInt64BigEndian(datagram[64..72]),
            BinaryPrimitives.ReadUInt64BigEndian(datagram[72..80]));
        payload = datagram.Slice(ManagedUdpEnvelopeHeader.UnsignedHeaderLength, length);
        tag = datagram.Slice(ManagedUdpEnvelopeHeader.UnsignedHeaderLength + length, ManagedUdpEnvelopeHeader.TagLength);
        return true;
    }

    public static bool Verify(ReadOnlySpan<byte> datagram, ReadOnlySpan<byte> key)
    {
        if (!TryRead(datagram, out _, out ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> tag))
            return false;
        int signedLength = ManagedUdpEnvelopeHeader.UnsignedHeaderLength + payload.Length;
        return ManagedUdpAuthentication.VerifyTag(key, datagram[..signedLength], tag);
    }

    private static void WriteUnsignedHeader(Span<byte> target, in ManagedUdpEnvelopeHeader header, int payloadLength)
    {
        Magic.CopyTo(target);
        target[4] = ManagedUdpEnvelopeHeader.Version;
        target[5] = (byte)header.Kind;
        target[6] = (byte)header.Direction;
        target[7] = 0;
        target[8] = 0;
        target[9] = ManagedUdpEnvelopeHeader.UnsignedHeaderLength;
        target[10] = 0;
        target[11] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(target[12..16], (uint)payloadLength);
        header.SessionId.TryWriteBytes(target[16..32]);
        header.Generation.TryWriteBytes(target[32..48]);
        header.AssociationId.TryWriteBytes(target[48..64]);
        BinaryPrimitives.WriteInt64BigEndian(target[64..72], header.CredentialEpoch);
        BinaryPrimitives.WriteUInt64BigEndian(target[72..80], header.Counter);
    }
}
