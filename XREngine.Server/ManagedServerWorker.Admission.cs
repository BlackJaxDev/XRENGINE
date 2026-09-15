using System.Security.Cryptography;
using XREngine.ControlPlane;

namespace XREngine.Networking;

internal sealed partial class ManagedServerWorker
{
    private readonly Dictionary<string, ManagedAdmissionVerifier> _grantVerifiers = new(StringComparer.Ordinal);

    /// <summary>Reads only installed proof material. Hello does not consume capacity or publish a connection.</summary>
    public bool TryGetManagedAdmissionVerifier(PlayerJoinRequest request, out ManagedAdmissionVerifier verifier)
    {
        lock (_grantLock)
        {
            verifier = null!;
            if (!TryResolveCurrentAdmission(request, out _, out ManagedAdmissionVerifier? current))
                return false;
            verifier = new ManagedAdmissionVerifier { Identity = current!.Identity, RootKey = current.RootKey.ToArray(), ExpiresUtc = current.ExpiresUtc };
            return true;
        }
    }

    /// <summary>
    /// Serializes grant revocation with synchronous connection publication. The callback cannot await,
    /// and callers must acquire this grant lock before any server player lock.
    /// </summary>
    public bool CommitManagedAdmission(PlayerJoinRequest request, ManagedAdmissionVerifier verifier, Func<ServerJoinAdmissionResult, bool> admit)
    {
        lock (_grantLock)
        {
            if (!TryResolveCurrentAdmission(request, out ManagedAdmissionGrant? grant, out ManagedAdmissionVerifier? current)
                || current!.Identity != verifier.Identity || !CryptographicOperations.FixedTimeEquals(current.RootKey, verifier.RootKey))
                return false;
            ServerSessionContext? session = Engine.ServerSessionResolver?.Invoke(request);
            if (session is null || session.SessionId != _launch.SessionId)
                return false;
            if (!CanAdvanceCredentialEpoch(grant!))
                return false;
            DateTimeOffset accepted = _acceptedAdmissionRetries.TryGetValue(grant!.ReservationId, out CachedAdmission? retry)
                && retry.Grant.CredentialEpoch == grant.CredentialEpoch ? retry.AcceptedUtc : DateTimeOffset.UtcNow;
            var admission = new ServerJoinAdmissionResult(session, AccountId: grant.AccountId, ReservationId: grant.ReservationId,
                CredentialPurpose: (int)grant.Purpose, CredentialEpoch: grant.CredentialEpoch, AcceptedUtc: accepted);
            if (!admit(admission))
                return false;
            RecordCredentialEpoch(grant);
            _acceptedAdmissionRetries[grant.ReservationId] = new CachedAdmission(grant, accepted);
            return true;
        }
    }

    private bool TryResolveCurrentAdmission(PlayerJoinRequest request, out ManagedAdmissionGrant? grant, out ManagedAdmissionVerifier? verifier)
    {
        grant = null;
        verifier = null;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_draining || _stopping || now > _freshUntilUtc || string.IsNullOrWhiteSpace(request.ReservationId)
            || _revoked.Contains(request.ReservationId) || !string.IsNullOrEmpty(request.AdmissionSecret) || !string.IsNullOrEmpty(request.SessionToken))
            return false;
        if (!_grants.TryGetValue(request.ReservationId, out grant) && _acceptedAdmissionRetries.TryGetValue(request.ReservationId, out CachedAdmission? retry))
            grant = retry.Grant;
        return grant is not null && grant.ExpiresUtc > now && _grantVerifiers.TryGetValue(request.ReservationId, out verifier)
            && grant.Generation == _launch.Generation && request.WorkerGeneration == _launch.Generation
            && request.SessionId == _launch.SessionId && grant.SessionId == _launch.SessionId
            && request.ResumeRequested == (grant.Purpose == ManagedAdmissionGrantPurpose.Resume)
            && request.CredentialEpoch == grant.CredentialEpoch
            && string.Equals(grant.ClientId, request.ClientId, StringComparison.Ordinal)
            && string.Equals(grant.AccountId, request.AccountId, StringComparison.Ordinal)
            && string.Equals(grant.BuildVersion, request.BuildVersion, StringComparison.Ordinal)
            && string.Equals(grant.BuildVersion, _launch.BuildVersion, StringComparison.Ordinal)
            && request.ClientWorldAsset?.IsSameAssetAs(_launch.WorldPackage.Asset) == true;
    }

    private static ManagedAdmissionIdentity CreateAdmissionIdentity(ManagedAdmissionGrant grant)
        => new(grant.SessionId, grant.Generation, grant.ReservationId, grant.ClientId, grant.AccountId, grant.CredentialEpoch, (int)grant.Purpose);
}
