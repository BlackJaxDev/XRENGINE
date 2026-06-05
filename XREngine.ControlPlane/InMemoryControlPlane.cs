using System.Security.Cryptography;
using System.Text.Json;
using XREngine.Networking;

namespace XREngine.ControlPlane;

public sealed class InMemoryControlPlane(ControlPlaneOptions? options = null)
{
    private readonly object _sync = new();
    private readonly ControlPlaneOptions _options = options ?? new ControlPlaneOptions();
    private readonly Dictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InstanceState> _instances = new(StringComparer.OrdinalIgnoreCase);

    public ControlPlaneHostSnapshot RegisterHost(ControlPlaneHostRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        string hostId = string.IsNullOrWhiteSpace(registration.HostId)
            ? Guid.NewGuid().ToString("N")
            : registration.HostId.Trim();

        RealtimeEndpointDescriptor endpoint = CloneEndpoint(registration.Endpoint);
        if (string.IsNullOrWhiteSpace(endpoint.ProtocolVersion))
            endpoint.ProtocolVersion = _options.DefaultProtocolVersion;

        var state = new HostState
        {
            Registration = new ControlPlaneHostRegistration
            {
                HostId = hostId,
                DisplayName = registration.DisplayName,
                Endpoint = endpoint,
                MaxInstances = Math.Max(1, registration.MaxInstances),
                MaxPlayers = Math.Max(1, registration.MaxPlayers),
                Metadata = CloneDictionary(registration.Metadata),
            }
        };

        lock (_sync)
        {
            _hosts[hostId] = state;
            return CreateHostSnapshot(state);
        }
    }

    public IReadOnlyList<ControlPlaneHostSnapshot> ListHosts()
    {
        lock (_sync)
            return _hosts.Values.Select(CreateHostSnapshot).ToArray();
    }

    public ControlPlaneResult<MultiplayerInstanceInfo> CreateInstance(CreateMultiplayerInstanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        WorldAssetIdentity? worldAsset = request.WorldAsset ?? request.WorldPackage?.Asset;
        if (worldAsset is null)
        {
            return ControlPlaneResult<MultiplayerInstanceInfo>.Fail(
                ControlPlaneFailureReason.InvalidRequest,
                "A world asset identity or world package manifest is required.");
        }

        int maxPlayers = Math.Max(1, request.MaxPlayers ?? _options.DefaultMaxPlayers);
        string instanceId = string.IsNullOrWhiteSpace(request.InstanceId)
            ? Guid.NewGuid().ToString("N")
            : request.InstanceId.Trim();

        lock (_sync)
        {
            if (_instances.ContainsKey(instanceId))
            {
                return ControlPlaneResult<MultiplayerInstanceInfo>.Fail(
                    ControlPlaneFailureReason.InvalidRequest,
                    $"Instance '{instanceId}' already exists.");
            }

            HostState? host = ResolveHostForCreate(request, maxPlayers, out ControlPlaneFailureReason hostFailureReason, out string? hostFailure);
            if (hostFailure is not null)
                return ControlPlaneResult<MultiplayerInstanceInfo>.Fail(hostFailureReason, hostFailure);

            RealtimeEndpointDescriptor endpoint = request.Endpoint is not null
                ? CloneEndpoint(request.Endpoint)
                : CloneEndpoint(host?.Registration.Endpoint ?? new RealtimeEndpointDescriptor
                {
                    Host = "127.0.0.1",
                    Port = 5000,
                    ProtocolVersion = _options.DefaultProtocolVersion,
                });

            if (string.IsNullOrWhiteSpace(endpoint.ProtocolVersion))
                endpoint.ProtocolVersion = _options.DefaultProtocolVersion;

            string hostId = host?.Registration.HostId
                ?? (string.IsNullOrWhiteSpace(request.HostId) ? "direct-local" : request.HostId.Trim());

            var instance = new InstanceState
            {
                Info = new MultiplayerInstanceInfo
                {
                    InstanceId = instanceId,
                    DisplayName = request.DisplayName,
                    HostId = hostId,
                    Endpoint = endpoint,
                    SessionId = request.SessionId ?? Guid.NewGuid(),
                    SessionToken = string.IsNullOrWhiteSpace(request.SessionToken)
                        ? CreateOpaqueToken(_options.TokenByteLength)
                        : request.SessionToken!,
                    WorldAsset = CloneWorldAsset(worldAsset),
                    WorldPackage = request.WorldPackage is null ? null : CloneWorldPackage(request.WorldPackage),
                    MaxPlayers = maxPlayers,
                    CurrentPlayers = 0,
                    State = MultiplayerInstanceState.Running,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    Metadata = CloneDictionary(request.Metadata),
                }
            };

            _instances[instanceId] = instance;
            return ControlPlaneResult<MultiplayerInstanceInfo>.Ok(CloneInstanceInfo(instance.Info, includeToken: true));
        }
    }

    public IReadOnlyList<MultiplayerInstanceInfo> ListInstances(bool includeStopped = false)
    {
        lock (_sync)
        {
            return _instances.Values
                .Where(instance => includeStopped || instance.Info.State != MultiplayerInstanceState.Stopped)
                .Select(instance => CloneInstanceInfo(instance.Info, includeToken: false))
                .ToArray();
        }
    }

    public ControlPlaneResult<MultiplayerInstanceInfo> GetInstance(string instanceId, bool includeToken = false)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return ControlPlaneResult<MultiplayerInstanceInfo>.Fail(
                ControlPlaneFailureReason.InvalidRequest,
                "Instance id is required.");
        }

        lock (_sync)
        {
            return _instances.TryGetValue(instanceId.Trim(), out InstanceState? instance)
                ? ControlPlaneResult<MultiplayerInstanceInfo>.Ok(CloneInstanceInfo(instance.Info, includeToken))
                : ControlPlaneResult<MultiplayerInstanceInfo>.Fail(ControlPlaneFailureReason.InstanceNotFound, $"Instance '{instanceId}' was not found.");
        }
    }

    public ControlPlaneResult<JoinMultiplayerInstanceResult> JoinInstance(JoinMultiplayerInstanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.InstanceId))
        {
            return ControlPlaneResult<JoinMultiplayerInstanceResult>.Fail(
                ControlPlaneFailureReason.InvalidRequest,
                "Instance id is required.");
        }

        string clientId = string.IsNullOrWhiteSpace(request.ClientId)
            ? Guid.NewGuid().ToString("N")
            : request.ClientId.Trim();

        lock (_sync)
        {
            if (!_instances.TryGetValue(request.InstanceId.Trim(), out InstanceState? instance))
            {
                return ControlPlaneResult<JoinMultiplayerInstanceResult>.Fail(
                    ControlPlaneFailureReason.InstanceNotFound,
                    $"Instance '{request.InstanceId}' was not found.");
            }

            if (instance.Info.State != MultiplayerInstanceState.Running)
            {
                return ControlPlaneResult<JoinMultiplayerInstanceResult>.Fail(
                    ControlPlaneFailureReason.InstanceNotRunning,
                    $"Instance '{request.InstanceId}' is {instance.Info.State}.");
            }

            if (request.LocalWorldAsset is not null && !instance.Info.WorldAsset.IsSameAssetAs(request.LocalWorldAsset))
            {
                return ControlPlaneResult<JoinMultiplayerInstanceResult>.Fail(
                    ControlPlaneFailureReason.WorldAssetMismatch,
                    "Client world asset does not match the instance world asset.");
            }

            if (!instance.Info.WorldAsset.IsBuildCompatible(request.BuildVersion ?? instance.Info.Endpoint.ProtocolVersion))
            {
                return ControlPlaneResult<JoinMultiplayerInstanceResult>.Fail(
                    ControlPlaneFailureReason.BuildVersionMismatch,
                    "Client build version is not compatible with the instance world asset.");
            }

            bool existingPlayer = instance.Players.ContainsKey(clientId);
            if (!existingPlayer && instance.Players.Count >= instance.Info.MaxPlayers)
            {
                return ControlPlaneResult<JoinMultiplayerInstanceResult>.Fail(
                    ControlPlaneFailureReason.InstanceFull,
                    $"Instance '{request.InstanceId}' is full.");
            }

            var player = new MultiplayerPlayerInfo
            {
                ClientId = clientId,
                DisplayName = request.DisplayName,
                JoinedUtc = existingPlayer ? instance.Players[clientId].JoinedUtc : DateTimeOffset.UtcNow,
                Metadata = CloneDictionary(request.Metadata),
            };
            instance.Players[clientId] = player;
            instance.Info.CurrentPlayers = instance.Players.Count;

            RealtimeJoinHandoffPayload payload = CreateJoinPayload(instance.Info);
            string handoffJson = JsonSerializer.Serialize(payload, XreControlPlaneJsonContext.Default.RealtimeJoinHandoffPayload);
            Dictionary<string, string> clientEnv = CreateClientEnvironment(payload, request.ClientReceivePort);

            return ControlPlaneResult<JoinMultiplayerInstanceResult>.Ok(new JoinMultiplayerInstanceResult
            {
                Instance = CloneInstanceInfo(instance.Info, includeToken: false),
                Player = ClonePlayerInfo(player),
                HandoffPayload = payload,
                HandoffJson = handoffJson,
                ClientEnvironment = clientEnv,
            });
        }
    }

    public bool LeaveInstance(LeaveMultiplayerInstanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.InstanceId) || string.IsNullOrWhiteSpace(request.ClientId))
            return false;

        lock (_sync)
        {
            if (!_instances.TryGetValue(request.InstanceId.Trim(), out InstanceState? instance))
                return false;

            bool removed = instance.Players.Remove(request.ClientId.Trim());
            if (removed)
                instance.Info.CurrentPlayers = instance.Players.Count;

            return removed;
        }
    }

    public bool StopInstance(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return false;

        lock (_sync)
        {
            if (!_instances.TryGetValue(instanceId.Trim(), out InstanceState? instance))
                return false;

            instance.Info.State = MultiplayerInstanceState.Stopped;
            instance.Players.Clear();
            instance.Info.CurrentPlayers = 0;
            return true;
        }
    }

    public ServerLaunchPlan CreateServerLaunchPlan(string instanceId)
    {
        ControlPlaneResult<MultiplayerInstanceInfo> result = GetInstance(instanceId, includeToken: true);
        if (!result.Success || result.Value is null)
            throw new InvalidOperationException(result.Message ?? $"Instance '{instanceId}' was not found.");

        return new ServerLaunchPlan
        {
            Instance = result.Value,
            Environment = CreateServerEnvironment(result.Value),
        };
    }

    public static RealtimeJoinHandoffPayload CreateJoinPayload(MultiplayerInstanceInfo instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return new RealtimeJoinHandoffPayload
        {
            SessionId = instance.SessionId,
            SessionToken = instance.SessionToken,
            Endpoint = CloneEndpoint(instance.Endpoint),
            WorldAsset = CloneWorldAsset(instance.WorldAsset),
        };
    }

    public static Dictionary<string, string> CreateServerEnvironment(MultiplayerInstanceInfo instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["XRE_SESSION_ID"] = instance.SessionId.ToString("D"),
            ["XRE_SESSION_TOKEN"] = instance.SessionToken,
            ["XRE_WORLD_ID"] = instance.WorldAsset.WorldId,
            ["XRE_WORLD_REVISION"] = instance.WorldAsset.RevisionId,
            ["XRE_WORLD_CONTENT_HASH"] = instance.WorldAsset.ContentHash,
            ["XRE_WORLD_ASSET_SCHEMA_VERSION"] = instance.WorldAsset.AssetSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["XRE_WORLD_REQUIRED_BUILD_VERSION"] = instance.WorldAsset.RequiredBuildVersion,
            ["XRE_UDP_BIND_PORT"] = instance.Endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["XRE_UDP_ADVERTISED_PORT"] = instance.Endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    public static Dictionary<string, string> CreateClientEnvironment(RealtimeJoinHandoffPayload payload, int? clientReceivePort = null)
    {
        ArgumentNullException.ThrowIfNull(payload);

        string handoffJson = JsonSerializer.Serialize(payload, XreControlPlaneJsonContext.Default.RealtimeJoinHandoffPayload);
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["XRE_NET_MODE"] = "Client",
            ["XRE_REALTIME_JOIN_PAYLOAD"] = handoffJson,
        };

        if (payload.WorldAsset is not null)
        {
            env["XRE_WORLD_ID"] = payload.WorldAsset.WorldId;
            env["XRE_WORLD_REVISION"] = payload.WorldAsset.RevisionId;
            env["XRE_WORLD_CONTENT_HASH"] = payload.WorldAsset.ContentHash;
            env["XRE_WORLD_ASSET_SCHEMA_VERSION"] = payload.WorldAsset.AssetSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
            env["XRE_WORLD_REQUIRED_BUILD_VERSION"] = payload.WorldAsset.RequiredBuildVersion;
        }

        if (clientReceivePort is int port)
            env["XRE_UDP_CLIENT_RECEIVE_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return env;
    }

    private HostState? ResolveHostForCreate(
        CreateMultiplayerInstanceRequest request,
        int requestedMaxPlayers,
        out ControlPlaneFailureReason failureReason,
        out string? failure)
    {
        failureReason = ControlPlaneFailureReason.None;
        failure = null;

        if (!string.IsNullOrWhiteSpace(request.HostId))
        {
            if (!_hosts.TryGetValue(request.HostId.Trim(), out HostState? requestedHost))
            {
                failureReason = ControlPlaneFailureReason.HostNotFound;
                failure = $"Host '{request.HostId}' was not found.";
                return null;
            }

            if (!HostHasCapacity(requestedHost, requestedMaxPlayers))
            {
                failureReason = ControlPlaneFailureReason.NoHostCapacity;
                failure = $"Host '{request.HostId}' does not have capacity for the requested instance.";
                return requestedHost;
            }

            return requestedHost;
        }

        if (request.Endpoint is not null)
            return null;

        HostState? host = _hosts.Values.FirstOrDefault(candidate => HostHasCapacity(candidate, requestedMaxPlayers));
        if (host is null && _hosts.Count > 0)
        {
            failureReason = ControlPlaneFailureReason.NoHostCapacity;
            failure = "No registered host has capacity for the requested instance.";
        }

        return host;
    }

    private bool HostHasCapacity(HostState host, int requestedMaxPlayers)
    {
        int activeInstances = 0;
        int reservedPlayerSlots = 0;

        foreach (InstanceState instance in _instances.Values)
        {
            if (!string.Equals(instance.Info.HostId, host.Registration.HostId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (instance.Info.State is MultiplayerInstanceState.Stopped)
                continue;

            activeInstances++;
            reservedPlayerSlots += instance.Info.MaxPlayers;
        }

        return activeInstances < host.Registration.MaxInstances
            && reservedPlayerSlots + Math.Max(1, requestedMaxPlayers) <= host.Registration.MaxPlayers;
    }

    private ControlPlaneHostSnapshot CreateHostSnapshot(HostState host)
    {
        int activeInstances = 0;
        int activePlayers = 0;

        foreach (InstanceState instance in _instances.Values)
        {
            if (!string.Equals(instance.Info.HostId, host.Registration.HostId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (instance.Info.State is MultiplayerInstanceState.Stopped)
                continue;

            activeInstances++;
            activePlayers += instance.Players.Count;
        }

        return new ControlPlaneHostSnapshot
        {
            HostId = host.Registration.HostId,
            DisplayName = host.Registration.DisplayName,
            Endpoint = CloneEndpoint(host.Registration.Endpoint),
            MaxInstances = host.Registration.MaxInstances,
            MaxPlayers = host.Registration.MaxPlayers,
            ActiveInstances = activeInstances,
            ActivePlayers = activePlayers,
            Metadata = CloneDictionary(host.Registration.Metadata),
        };
    }

    private static string CreateOpaqueToken(int byteLength)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(Math.Clamp(byteLength, 16, 128));
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static RealtimeEndpointDescriptor CloneEndpoint(RealtimeEndpointDescriptor endpoint)
        => new()
        {
            Transport = endpoint.Transport,
            Host = endpoint.Host,
            Port = endpoint.Port,
            ProtocolVersion = endpoint.ProtocolVersion,
            Metadata = CloneDictionary(endpoint.Metadata),
        };

    internal static WorldAssetIdentity CloneWorldAsset(WorldAssetIdentity asset)
        => new()
        {
            WorldId = asset.WorldId,
            RevisionId = asset.RevisionId,
            ContentHash = asset.ContentHash,
            AssetSchemaVersion = asset.AssetSchemaVersion,
            RequiredBuildVersion = asset.RequiredBuildVersion,
            Metadata = CloneDictionary(asset.Metadata),
        };

    internal static WorldPackageManifest CloneWorldPackage(WorldPackageManifest manifest)
        => new()
        {
            SchemaVersion = manifest.SchemaVersion,
            PackageId = manifest.PackageId,
            Asset = CloneWorldAsset(manifest.Asset),
            RootPath = manifest.RootPath,
            TotalBytes = manifest.TotalBytes,
            ManifestHash = manifest.ManifestHash,
            Files = manifest.Files.Select(static file => new WorldPackageFile
            {
                RelativePath = file.RelativePath,
                Length = file.Length,
                Sha256 = file.Sha256,
            }).ToList(),
            Metadata = CloneDictionary(manifest.Metadata),
        };

    private static MultiplayerInstanceInfo CloneInstanceInfo(MultiplayerInstanceInfo info, bool includeToken)
        => new()
        {
            InstanceId = info.InstanceId,
            DisplayName = info.DisplayName,
            HostId = info.HostId,
            Endpoint = CloneEndpoint(info.Endpoint),
            SessionId = info.SessionId,
            SessionToken = includeToken ? info.SessionToken : string.Empty,
            WorldAsset = CloneWorldAsset(info.WorldAsset),
            WorldPackage = info.WorldPackage is null ? null : CloneWorldPackage(info.WorldPackage),
            State = info.State,
            MaxPlayers = info.MaxPlayers,
            CurrentPlayers = info.CurrentPlayers,
            CreatedUtc = info.CreatedUtc,
            Metadata = CloneDictionary(info.Metadata),
        };

    private static MultiplayerPlayerInfo ClonePlayerInfo(MultiplayerPlayerInfo player)
        => new()
        {
            ClientId = player.ClientId,
            DisplayName = player.DisplayName,
            JoinedUtc = player.JoinedUtc,
            Metadata = CloneDictionary(player.Metadata),
        };

    private static Dictionary<string, string> CloneDictionary(IReadOnlyDictionary<string, string>? source)
        => source is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);

    private sealed class HostState
    {
        public ControlPlaneHostRegistration Registration { get; init; } = new();
    }

    private sealed class InstanceState
    {
        public MultiplayerInstanceInfo Info { get; init; } = new();
        public Dictionary<string, MultiplayerPlayerInfo> Players { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
