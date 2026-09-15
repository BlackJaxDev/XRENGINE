using System.Security.Cryptography;
using System.Text;
using XREngine.Networking;

namespace XREngine.ControlPlane;

/// <summary>Versioned, length-delimited ES256 credential encoding. Revocation and one-use epochs remain worker responsibilities.</summary>
public static class ManagedAdmissionSigning
{
    public static ManagedAdmissionSignature Sign(ManagedAdmissionGrant grant, ManagedWorkerLaunch launch, string issuer, string keyId, ECDsa key)
    {
        if (!IsP256(key))
            throw new CryptographicException("Admission signing requires an ECDSA P-256 key.");
        var signature = new ManagedAdmissionSignature { Issuer = issuer, KeyId = keyId, IssuedUtc = DateTimeOffset.UtcNow };
        signature.Value = Convert.ToBase64String(key.SignData(Encode(grant, launch, signature), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        return signature;
    }

    public static bool Verify(ManagedAdmissionGrant grant, ManagedWorkerLaunch launch, DateTimeOffset now)
    {
        ManagedAdmissionSignature? signature = grant.Signature;
        if (signature is null || signature.Issuer != launch.AdmissionIssuer || signature.IssuedUtc > now.AddSeconds(10)
            || grant.ExpiresUtc <= now || grant.ExpiresUtc > signature.IssuedUtc.AddMinutes(10)
            || signature.IssuedUtc < now.AddMinutes(-10) || !launch.AdmissionSigningKeys.TryGetValue(signature.KeyId, out string? publicKey))
            return false;
        try
        {
            using ECDsa key = ECDsa.Create();
            byte[] encodedKey = Convert.FromBase64String(publicKey);
            key.ImportSubjectPublicKeyInfo(encodedKey, out int read);
            byte[] proof = Convert.FromBase64String(signature.Value);
            return read == encodedKey.Length && IsP256(key) && proof.Length == 64
                && key.VerifyData(Encode(grant, launch, signature), proof, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static byte[] Encode(ManagedAdmissionGrant grant, ManagedWorkerLaunch launch, ManagedAdmissionSignature signature)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("xrengine-admission/1:ES256");
        Write(writer, signature.KeyId);
        Write(writer, signature.Issuer);
        writer.Write(signature.IssuedUtc.UtcTicks);
        writer.Write(grant.ExpiresUtc.UtcTicks);
        Write(writer, launch.InstanceId);
        writer.Write(grant.SessionId.ToByteArray());
        writer.Write(grant.Generation.ToByteArray());
        Write(writer, grant.ReservationId);
        Write(writer, grant.AccountId);
        Write(writer, grant.ClientId);
        writer.Write(grant.CredentialEpoch);
        writer.Write((int)grant.Purpose);
        Write(writer, grant.WorldId);
        Write(writer, grant.WorldRevision);
        Write(writer, WorldAssetIdentity.NormalizeHash(grant.ContentHash));
        Write(writer, grant.BuildVersion);
        writer.Write((int)launch.AdvertisedEndpoint.Transport);
        Write(writer, launch.AdvertisedEndpoint.Host);
        writer.Write(launch.AdvertisedEndpoint.Port);
        Write(writer, launch.AdvertisedEndpoint.ProtocolVersion);
        writer.Write(SHA256.HashData(Encoding.UTF8.GetBytes(grant.Secret)));
        writer.Flush();
        return stream.ToArray();
    }

    private static void Write(BinaryWriter writer, string value)
    {
        if (value.Length is 0 or > 1024)
            throw new ArgumentException("Admission claims must be nonempty and bounded.");
        writer.Write(value);
    }

    private static bool IsP256(ECDsa key)
        => key.KeySize == 256 && key.ExportParameters(includePrivateParameters: false).Curve.Oid.Value == "1.2.840.10045.3.1.7";
}
