using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace XREngine.Networking;

/// <summary>Cryptographic derivation primitives for the managed UDP transport.</summary>
public static class ManagedUdpAuthentication
{
    private static readonly byte[] RootLabel = "XRE/MUDP/1/root\0"u8.ToArray();

    public static byte[] DeriveRootKey(string admissionSecret, ManagedAdmissionIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(admissionSecret);
        byte[] secret = Encoding.UTF8.GetBytes(admissionSecret);
        try
        {
            using var hmac = new HMACSHA256(secret);
            return hmac.ComputeHash(BuildRootContext(identity));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static byte[] DeriveKey(ReadOnlySpan<byte> parentKey, ReadOnlySpan<byte> label, ReadOnlySpan<byte> context)
    {
        using var hmac = new HMACSHA256(parentKey.ToArray());
        byte[] input = new byte[label.Length + context.Length];
        label.CopyTo(input);
        context.CopyTo(input.AsSpan(label.Length));
        try { return hmac.ComputeHash(input); }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    public static byte[] DeriveJoinKey(ReadOnlySpan<byte> rootKey, ManagedUdpDirection direction)
        => DeriveKey(rootKey, direction == ManagedUdpDirection.ClientToServer ? "join/c2s\0"u8 : "join/s2c\0"u8, []);

    public static byte[] DeriveTrafficKey(ReadOnlySpan<byte> rootKey, ReadOnlySpan<byte> transcriptHash, ManagedUdpDirection direction)
    {
        byte[] master = DeriveKey(rootKey, "traffic\0"u8, transcriptHash);
        try { return DeriveKey(master, direction == ManagedUdpDirection.ClientToServer ? "c2s\0"u8 : "s2c\0"u8, []); }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    public static bool VerifyTag(ReadOnlySpan<byte> key, ReadOnlySpan<byte> signedBytes, ReadOnlySpan<byte> tag)
    {
        if (tag.Length != 32)
            return false;
        using var hmac = new HMACSHA256(key.ToArray());
        byte[] expected = hmac.ComputeHash(signedBytes.ToArray());
        try { return CryptographicOperations.FixedTimeEquals(expected, tag); }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    /// <summary>Builds the transcript binding for one challenge/commit exchange.</summary>
    public static byte[] DeriveTranscriptHash(
        ManagedAdmissionIdentity identity,
        ReadOnlySpan<byte> clientNonce,
        ReadOnlySpan<byte> serverNonce,
        Guid associationId,
        ReadOnlySpan<byte> requestHash,
        long expiryUnixSeconds,
        ReadOnlySpan<byte> endpointCookie)
    {
        if (clientNonce.Length != 32 || serverNonce.Length != 32 || requestHash.Length != 32 || associationId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(clientNonce));

        using var stream = new MemoryStream();
        stream.Write(BuildIdentityContext(identity));
        stream.Write(clientNonce);
        stream.Write(serverNonce);
        stream.Write(associationId.ToByteArray());
        stream.Write(requestHash);
        WriteInt64(stream, expiryUnixSeconds);
        stream.Write(endpointCookie);
        return SHA256.HashData(stream.ToArray());
    }

    private static byte[] BuildRootContext(ManagedAdmissionIdentity identity)
    {
        using var stream = new MemoryStream();
        stream.Write(RootLabel);
        stream.Write(BuildIdentityContext(identity));
        return stream.ToArray();
    }

    internal static byte[] BuildIdentityContext(ManagedAdmissionIdentity identity)
    {
        using var stream = new MemoryStream();
        stream.Write(identity.SessionId.ToByteArray());
        stream.Write(identity.Generation.ToByteArray());
        WriteInt64(stream, identity.CredentialEpoch);
        WriteInt32(stream, identity.CredentialPurpose);
        WriteString(stream, identity.ReservationId);
        WriteString(stream, identity.ClientId);
        WriteString(stream, identity.AccountId);
        return stream.ToArray();
    }

    private static void WriteString(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
