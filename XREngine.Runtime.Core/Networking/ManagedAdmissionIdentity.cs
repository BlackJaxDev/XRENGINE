namespace XREngine.Networking;

/// <summary>Canonical managed-admission identity used to derive transport keys without transmitting the admission secret.</summary>
public readonly record struct ManagedAdmissionIdentity(
    Guid SessionId,
    Guid Generation,
    string ReservationId,
    string ClientId,
    string AccountId,
    long CredentialEpoch,
    int CredentialPurpose);

