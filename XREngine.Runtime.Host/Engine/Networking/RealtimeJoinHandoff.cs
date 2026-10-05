using XREngine.Networking;

namespace XREngine;

public static class RealtimeJoinHandoff
{
    public const string PayloadEnvironmentVariable = RealtimeJoinHandoffContract.PayloadEnvironmentVariable;
    public const string PayloadFileEnvironmentVariable = RealtimeJoinHandoffContract.PayloadFileEnvironmentVariable;

    public static string CurrentProtocolVersion => RealtimeJoinHandoffContract.CurrentProtocolVersion;

    public static void ApplyToSettings(GameStartupSettings settings, RealtimeJoinHandoffPayload payload)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(payload);

        RealtimeEndpointDescriptor endpoint = payload.Endpoint
            ?? throw new InvalidOperationException("Realtime handoff payload is missing endpoint.");

        if (endpoint.Transport is not (RealtimeTransportKind.NativeUdp or RealtimeTransportKind.NativeTls or RealtimeTransportKind.WebSocket))
            throw new NotSupportedException($"Realtime transport '{endpoint.Transport}' is not supported by this runtime.");

        if (string.IsNullOrWhiteSpace(endpoint.Host))
            throw new InvalidOperationException("Realtime handoff endpoint host is required.");

        if (endpoint.Port is <= 0 or > 65535)
            throw new InvalidOperationException("Realtime handoff endpoint port must be between 1 and 65535.");

        if (endpoint.Transport == RealtimeTransportKind.WebSocket)
            ValidateWebSocketAdmission(endpoint.Host, endpoint.Port, payload.SessionId, payload.WorkerGeneration,
                payload.AccountId, payload.ClientId, payload.ReservationId, payload.AdmissionSecret, payload.CredentialEpoch);

        settings.NetworkingType = ENetworkingType.Client;
        settings.MultiplayerTransport = endpoint.Transport;
        settings.ServerIP = endpoint.Host.Trim();
        settings.UdpServerSendPort = endpoint.Port;
        settings.ExpectedMultiplayerProtocolVersion = string.IsNullOrWhiteSpace(endpoint.ProtocolVersion)
            ? null
            : endpoint.ProtocolVersion.Trim();
        settings.MultiplayerSessionId = payload.SessionId;
        settings.MultiplayerSessionToken = string.IsNullOrWhiteSpace(payload.SessionToken)
            ? null
            : payload.SessionToken;
        settings.MultiplayerAccountId = string.IsNullOrWhiteSpace(payload.AccountId) ? null : payload.AccountId.Trim();
        settings.MultiplayerClientId = string.IsNullOrWhiteSpace(payload.ClientId) ? null : payload.ClientId.Trim();
        settings.MultiplayerReservationId = payload.ReservationId;
        settings.MultiplayerAdmissionSecret = string.IsNullOrWhiteSpace(payload.AdmissionSecret)
            ? null
            : payload.AdmissionSecret;
        settings.MultiplayerWorkerGeneration = payload.WorkerGeneration;
        settings.MultiplayerResumeRequested = payload.ResumeRequested;
        settings.MultiplayerCredentialEpoch = payload.CredentialEpoch;
        settings.ExpectedMultiplayerWorldAsset = payload.WorldAsset;
    }

    public static void ValidateClientStartup(
        GameStartupSettings settings,
        WorldAssetIdentity? localWorldAsset,
        string currentProtocolVersion)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.NetworkingType != ENetworkingType.Client)
            return;

        if (settings.MultiplayerTransport is not (RealtimeTransportKind.NativeUdp or RealtimeTransportKind.NativeTls or RealtimeTransportKind.WebSocket))
            throw new NotSupportedException($"Realtime transport '{settings.MultiplayerTransport}' is not supported by this runtime.");

        if (settings.MultiplayerTransport == RealtimeTransportKind.WebSocket)
            ValidateWebSocketAdmission(settings.ServerIP, settings.UdpServerSendPort, settings.MultiplayerSessionId,
                settings.MultiplayerWorkerGeneration, settings.MultiplayerAccountId, settings.MultiplayerClientId,
                settings.MultiplayerReservationId, settings.MultiplayerAdmissionSecret, settings.MultiplayerCredentialEpoch);

        if (!IsProtocolCompatible(settings.ExpectedMultiplayerProtocolVersion, currentProtocolVersion))
        {
            throw new InvalidOperationException(
                $"Realtime handoff protocol '{settings.ExpectedMultiplayerProtocolVersion}' is not compatible with runtime protocol '{currentProtocolVersion}'.");
        }

        WorldAssetIdentity? expectedWorldAsset = settings.ExpectedMultiplayerWorldAsset;
        if (expectedWorldAsset is null)
            return;

        if (!IsProtocolCompatible(expectedWorldAsset.RequiredBuildVersion, currentProtocolVersion))
        {
            throw new InvalidOperationException(
                $"Realtime handoff world requires build '{expectedWorldAsset.RequiredBuildVersion}', but runtime protocol is '{currentProtocolVersion}'.");
        }

        if (localWorldAsset is null)
            throw new InvalidOperationException("Realtime handoff supplied an expected world identity, but no local world identity is loaded.");

        if (!localWorldAsset.IsSameAssetAs(expectedWorldAsset))
        {
            throw new InvalidOperationException(
                "Realtime handoff world identity does not match the loaded local world. " +
                $"Expected {DescribeWorldAsset(expectedWorldAsset)}; local {DescribeWorldAsset(localWorldAsset)}.");
        }
    }

    public static void LogStartupSummary(
        GameStartupSettings settings,
        WorldAssetIdentity? localWorldAsset,
        string currentProtocolVersion)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.NetworkingType == ENetworkingType.Local)
            return;

        string endpoint = settings.NetworkingType switch
        {
            ENetworkingType.Client => $"{settings.ServerIP}:{settings.UdpServerSendPort}",
            ENetworkingType.Server => $"bind=0.0.0.0:{settings.UdpServerBindPort}; advertised={settings.UdpServerSendPort}; multicast={settings.UdpMulticastGroupIP}:{settings.UdpMulticastPort}",
            _ => "<none>",
        };

        string session = settings.MultiplayerSessionId?.ToString("D") ?? "<none>";
        string tokenState = string.IsNullOrWhiteSpace(settings.MultiplayerSessionToken) ? "none" : "supplied";
        string expected = settings.ExpectedMultiplayerWorldAsset is null
            ? "<none>"
            : DescribeWorldAsset(settings.ExpectedMultiplayerWorldAsset);

        Debug.Networking(
            "[Realtime Startup] mode={0}; endpoint={1}; transport={2}; session={3}; token={4}; protocol={5}; expectedProtocol={6}; localWorld={7}; expectedWorld={8}",
            settings.NetworkingType,
            endpoint,
            settings.MultiplayerTransport,
            session,
            tokenState,
            currentProtocolVersion,
            string.IsNullOrWhiteSpace(settings.ExpectedMultiplayerProtocolVersion) ? "<none>" : settings.ExpectedMultiplayerProtocolVersion,
            DescribeWorldAsset(localWorldAsset),
            expected);
    }

    public static bool IsProtocolCompatible(string? expectedProtocolVersion, string currentProtocolVersion)
    {
        return RealtimeJoinHandoffContract.IsProtocolCompatible(expectedProtocolVersion, currentProtocolVersion);
    }

    /// <summary>Validates a transient browser admission against an independently verified loaded world.</summary>
    public static Uri ValidateWebSocketClientHandoff(RealtimeJoinHandoffPayload payload,
        WorldAssetIdentity localWorldAsset, string currentProtocolVersion)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(localWorldAsset);
        if (payload.Endpoint is not { Transport: RealtimeTransportKind.WebSocket } endpoint)
            throw new NotSupportedException("Browser realtime admission requires the WebSocket transport.");
        ValidateWebSocketAdmission(endpoint.Host, endpoint.Port, payload.SessionId, payload.WorkerGeneration,
            payload.AccountId, payload.ClientId, payload.ReservationId, payload.AdmissionSecret, payload.CredentialEpoch);
        if (!string.IsNullOrEmpty(payload.SessionToken))
            throw new InvalidOperationException("Browser realtime admission does not accept legacy session tokens.");
        if (!IsBoundedIdentity(endpoint.ProtocolVersion, 128)
            || !IsProtocolCompatible(endpoint.ProtocolVersion, currentProtocolVersion))
            throw new InvalidOperationException("Browser realtime admission has an incompatible build protocol.");
        if (payload.WorldAsset is not { } expected
            || !IsBoundedIdentity(expected.WorldId, 256) || !IsBoundedIdentity(expected.RevisionId, 256)
            || !IsBoundedIdentity(expected.ContentHash, 256) || expected.AssetSchemaVersion < 1
            || !IsBoundedIdentity(expected.RequiredBuildVersion, 128)
            || !IsProtocolCompatible(expected.RequiredBuildVersion, currentProtocolVersion)
            || !localWorldAsset.IsSameAssetAs(expected))
            throw new InvalidOperationException("Browser realtime admission does not match the verified loaded world and build.");
        return new UriBuilder("wss", endpoint.Host, endpoint.Port, RealtimeWebSocketProtocol.Path).Uri;
    }

    private static void ValidateWebSocketAdmission(string host, int port, Guid? session, Guid? generation,
        string? account, string? client, string? reservation, string? admission, long credentialEpoch)
    {
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535)
            throw new InvalidOperationException("Realtime WebSocket handoff requires a valid advertised hostname and port.");
        RealtimeWebSocketProtocol.ValidateEndpoint(new UriBuilder("wss", host, port, RealtimeWebSocketProtocol.Path).Uri);
        if (session is null || session == Guid.Empty || generation is null || generation == Guid.Empty || credentialEpoch < 0
            || !IsBoundedIdentity(account, 256) || !IsBoundedIdentity(client, 128) || !IsBoundedIdentity(reservation, 128)
            || string.IsNullOrWhiteSpace(admission) || admission.Length > 4096)
            throw new InvalidOperationException("Realtime WebSocket handoff requires a complete bounded managed player admission.");
    }

    private static bool IsBoundedIdentity(string? value, int maximumBytes)
        => !string.IsNullOrWhiteSpace(value) && System.Text.Encoding.UTF8.GetByteCount(value) <= maximumBytes;

    public static string DescribeWorldAsset(WorldAssetIdentity? asset)
    {
        return RealtimeJoinHandoffContract.DescribeWorldAsset(asset);
    }

}
