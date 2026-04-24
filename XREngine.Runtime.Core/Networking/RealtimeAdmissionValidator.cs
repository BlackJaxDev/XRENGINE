using System;

namespace XREngine.Networking;

public static class RealtimeAdmissionValidator
{
    public static AdmissionFailureReason ValidateSession(
        PlayerJoinRequest request,
        Guid hostedSessionId,
        string? requiredSessionToken,
        out string message)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.SessionId is Guid requestedSessionId && requestedSessionId != hostedSessionId)
        {
            message = "The requested realtime session is not hosted by this server.";
            return AdmissionFailureReason.SessionNotFound;
        }

        if (!string.IsNullOrWhiteSpace(requiredSessionToken)
            && !string.Equals(request.SessionToken, requiredSessionToken, StringComparison.Ordinal))
        {
            message = "The supplied realtime session token was rejected.";
            return AdmissionFailureReason.Unauthorized;
        }

        message = string.Empty;
        return AdmissionFailureReason.None;
    }

    public static AdmissionFailureReason ValidateBuildAndWorld(
        PlayerJoinRequest request,
        WorldAssetIdentity? serverWorldAsset,
        string serverProtocolVersion,
        out string message)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.BuildVersion, serverProtocolVersion, StringComparison.OrdinalIgnoreCase))
        {
            message = $"Server version {serverProtocolVersion}, client version {request.BuildVersion}.";
            return AdmissionFailureReason.BuildVersionMismatch;
        }

        if (serverWorldAsset is null)
        {
            message = "No local world asset identity is ready for realtime joins.";
            return AdmissionFailureReason.SessionNotFound;
        }

        if (request.ClientWorldAsset is null)
        {
            message = "Client did not provide a local world asset identity.";
            return AdmissionFailureReason.InvalidRequest;
        }

        if (!serverWorldAsset.IsBuildCompatible(request.BuildVersion))
        {
            message = "Client build version is not compatible with the server world asset.";
            return AdmissionFailureReason.BuildVersionMismatch;
        }

        if (!serverWorldAsset.IsSameAssetAs(request.ClientWorldAsset))
        {
            message = "Client world asset does not exactly match the server world asset.";
            return AdmissionFailureReason.WorldAssetMismatch;
        }

        message = string.Empty;
        return AdmissionFailureReason.None;
    }
}
