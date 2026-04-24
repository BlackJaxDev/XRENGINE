using System.Globalization;
using System.Text.Json;
using XREngine.Networking;

namespace XREngine;

public static class RealtimeJoinHandoff
{
    public const string PayloadEnvironmentVariable = "XRE_REALTIME_JOIN_PAYLOAD";
    public const string PayloadFileEnvironmentVariable = "XRE_REALTIME_JOIN_PAYLOAD_FILE";

    public static string CurrentProtocolVersion { get; } = typeof(Engine).Assembly.GetName().Version?.ToString() ?? "dev";

    public static bool TryApplyFromEnvironment(
        GameStartupSettings settings,
        out RealtimeJoinHandoffPayload? payload,
        out string? source)
    {
        ArgumentNullException.ThrowIfNull(settings);

        payload = null;
        source = null;

        string? payloadPath = GetOptionalEnvironmentValue(PayloadFileEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(payloadPath))
        {
            string resolvedPath = Path.GetFullPath(payloadPath);
            if (!File.Exists(resolvedPath))
                throw new FileNotFoundException("Realtime join handoff payload file was not found.", resolvedPath);

            payload = DeserializePayload(File.ReadAllText(resolvedPath), resolvedPath);
            source = $"{PayloadFileEnvironmentVariable}={resolvedPath}";
        }
        else
        {
            string? payloadJson = GetOptionalEnvironmentValue(PayloadEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(payloadJson))
                return false;

            payload = DeserializePayload(payloadJson, PayloadEnvironmentVariable);
            source = PayloadEnvironmentVariable;
        }

        ApplyToSettings(settings, payload);
        return true;
    }

    public static void ApplyToSettings(GameStartupSettings settings, RealtimeJoinHandoffPayload payload)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(payload);

        RealtimeEndpointDescriptor endpoint = payload.Endpoint
            ?? throw new InvalidOperationException("Realtime handoff payload is missing endpoint.");

        if (endpoint.Transport != RealtimeTransportKind.NativeUdp)
            throw new NotSupportedException($"Realtime transport '{endpoint.Transport}' is not supported by this runtime.");

        if (string.IsNullOrWhiteSpace(endpoint.Host))
            throw new InvalidOperationException("Realtime handoff endpoint host is required.");

        if (endpoint.Port is <= 0 or > 65535)
            throw new InvalidOperationException("Realtime handoff endpoint port must be between 1 and 65535.");

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

        if (settings.MultiplayerTransport != RealtimeTransportKind.NativeUdp)
            throw new NotSupportedException($"Realtime transport '{settings.MultiplayerTransport}' is not supported by this runtime.");

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
            ENetworkingType.P2PClient => $"{settings.ServerIP}; multicast={settings.UdpMulticastGroupIP}:{settings.UdpMulticastPort}",
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
        if (string.IsNullOrWhiteSpace(expectedProtocolVersion))
            return true;

        if (string.Equals(expectedProtocolVersion, "dev", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(currentProtocolVersion, "dev", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(expectedProtocolVersion.Trim(), currentProtocolVersion, StringComparison.OrdinalIgnoreCase);
    }

    public static string DescribeWorldAsset(WorldAssetIdentity? asset)
    {
        if (asset is null)
            return "<none>";

        string hash = WorldAssetIdentity.NormalizeHash(asset.ContentHash);
        if (hash.Length > 12)
            hash = hash[..12];

        if (string.IsNullOrWhiteSpace(hash))
            hash = "<empty>";

        return string.Create(CultureInfo.InvariantCulture, $"{asset.WorldId}@{asset.RevisionId}; hash={hash}; schema={asset.AssetSchemaVersion}");
    }

    private static RealtimeJoinHandoffPayload DeserializePayload(string json, string source)
    {
        try
        {
            return JsonSerializer.Deserialize(json, XREngineRuntimeJsonContext.Default.RealtimeJoinHandoffPayload)
                ?? throw new InvalidOperationException("Realtime handoff payload was empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Realtime handoff payload from {source} is not valid JSON.", ex);
        }
    }

    private static string? GetOptionalEnvironmentValue(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
