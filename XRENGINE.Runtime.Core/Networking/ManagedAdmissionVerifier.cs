namespace XREngine.Networking;

/// <summary>Cached worker-side verifier material for a pending managed admission.</summary>
public sealed class ManagedAdmissionVerifier
{
    public required byte[] RootKey { get; init; }
    public required ManagedAdmissionIdentity Identity { get; init; }
    public required DateTimeOffset ExpiresUtc { get; init; }
}

