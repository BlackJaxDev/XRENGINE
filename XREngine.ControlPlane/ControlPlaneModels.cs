using XREngine.Networking;

namespace XREngine.ControlPlane;

public enum ControlPlaneFailureReason
{
    None = 0,
    InvalidRequest,
    HostNotFound,
    NoHostCapacity,
    InstanceNotFound,
    InstanceNotRunning,
    InstanceFull,
    BuildVersionMismatch,
    WorldAssetMismatch,
    WorldPackageUnavailable,
}

public enum MultiplayerInstanceState
{
    Pending = 0,
    Running,
    Draining,
    Stopped,
}

public sealed class ControlPlaneOptions
{
    public string DefaultProtocolVersion { get; set; } = "dev";
    public int DefaultMaxPlayers { get; set; } = 8;
    public int TokenByteLength { get; set; } = 32;
    public string DefaultMulticastGroup { get; set; } = "239.0.0.222";
    public int DefaultMulticastPort { get; set; } = 5000;
}

public sealed class ControlPlaneHostRegistration
{
    public string HostId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public RealtimeEndpointDescriptor Endpoint { get; set; } = new()
    {
        Host = "127.0.0.1",
        Port = 5000,
        ProtocolVersion = "dev",
    };
    public int MaxInstances { get; set; } = 1;
    public int MaxPlayers { get; set; } = 16;
    public Dictionary<string, string> Metadata { get; set; } = [];
}

public sealed class ControlPlaneHostSnapshot
{
    public string HostId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public RealtimeEndpointDescriptor Endpoint { get; set; } = new();
    public int MaxInstances { get; set; }
    public int MaxPlayers { get; set; }
    public int ActiveInstances { get; set; }
    public int ActivePlayers { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];

    public bool HasCapacityFor(int requestedMaxPlayers)
        => ActiveInstances < MaxInstances
            && ActivePlayers + Math.Max(1, requestedMaxPlayers) <= MaxPlayers;
}

public sealed class CreateMultiplayerInstanceRequest
{
    public string? InstanceId { get; set; }
    public string? DisplayName { get; set; }
    public string? HostId { get; set; }
    public RealtimeEndpointDescriptor? Endpoint { get; set; }
    public WorldAssetIdentity? WorldAsset { get; set; }
    public WorldPackageManifest? WorldPackage { get; set; }
    public Guid? SessionId { get; set; }
    public string? SessionToken { get; set; }
    public int? MaxPlayers { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
}

public sealed class JoinMultiplayerInstanceRequest
{
    public string InstanceId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public WorldAssetIdentity? LocalWorldAsset { get; set; }
    public string? BuildVersion { get; set; }
    public int? ClientReceivePort { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
}

public sealed class LeaveMultiplayerInstanceRequest
{
    public string InstanceId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
}

public sealed class MultiplayerInstanceInfo
{
    public string InstanceId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string HostId { get; set; } = string.Empty;
    public RealtimeEndpointDescriptor Endpoint { get; set; } = new();
    public Guid SessionId { get; set; }
    public WorldAssetIdentity WorldAsset { get; set; } = new();
    public WorldPackageManifest? WorldPackage { get; set; }
    public MultiplayerInstanceState State { get; set; } = MultiplayerInstanceState.Running;
    public int MaxPlayers { get; set; }
    public int CurrentPlayers { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];

    /// <summary>
    /// Local-dev token that should be passed to the realtime worker as XRE_SESSION_TOKEN.
    /// Public services should keep the same shape but avoid returning this in list APIs.
    /// </summary>
    public string SessionToken { get; set; } = string.Empty;
}

public sealed class MultiplayerPlayerInfo
{
    public string ClientId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public DateTimeOffset JoinedUtc { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
}

public sealed class ControlPlaneResult<T>
{
    public ControlPlaneFailureReason FailureReason { get; init; }
    public string? Message { get; init; }
    public T? Value { get; init; }

    public bool Success => FailureReason == ControlPlaneFailureReason.None;

    public static ControlPlaneResult<T> Ok(T value)
        => new() { Value = value };

    public static ControlPlaneResult<T> Fail(ControlPlaneFailureReason reason, string message)
        => new() { FailureReason = reason, Message = message };
}

public sealed class JoinMultiplayerInstanceResult
{
    public MultiplayerInstanceInfo Instance { get; set; } = new();
    public MultiplayerPlayerInfo Player { get; set; } = new();
    public RealtimeJoinHandoffPayload HandoffPayload { get; set; } = new();
    public string HandoffJson { get; set; } = string.Empty;
    public Dictionary<string, string> ClientEnvironment { get; set; } = [];
}

public sealed class ServerLaunchPlan
{
    public MultiplayerInstanceInfo Instance { get; set; } = new();
    public Dictionary<string, string> Environment { get; set; } = [];
}
