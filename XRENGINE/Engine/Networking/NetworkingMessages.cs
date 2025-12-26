using System;
using System.Numerics;
using MemoryPack;
using XREngine.Components;

namespace XREngine.Networking
{
    [MemoryPackable]
    public sealed partial class PlayerJoinRequest
    {
        public string ClientId { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string BuildVersion { get; set; } = "dev";
        public string? WorldName { get; set; }
        public string? PreferredScene { get; set; }
        /// <summary>
        /// Optional instance the client wants to join. If omitted, the server will create one using <see cref="WorldLocator"/> when permitted.
        /// </summary>
        public Guid? InstanceId { get; set; }
        /// <summary>
        /// Optional world locator describing where to fetch or create the world for this instance.
        /// </summary>
        public WorldLocator? WorldLocator { get; set; }
        /// <summary>
        /// When true, request server-side rendering (dev/local only). Production hosts should ignore this.
        /// </summary>
        public bool EnableDevRendering { get; set; }
    }

    [MemoryPackable]
    public sealed partial class PlayerAssignment
    {
        public int ServerPlayerIndex { get; set; }
        public Guid PawnId { get; set; }
        public Guid TransformId { get; set; }
        public WorldSyncDescriptor? World { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public Guid InstanceId { get; set; }
        public bool IsAuthoritative { get; set; }
    }

    [MemoryPackable(GenerateType.NoGenerate)]
    public partial interface IPawnInputSnapshot
    {
        
    }

    [MemoryPackable]
    public sealed partial class PlayerInputSnapshot
    {
        public int ServerPlayerIndex { get; set; }
        public IPawnInputSnapshot? Input { get; set; }
        public double TimestampUtc { get; set; }
        public Guid InstanceId { get; set; }
    }

    [MemoryPackable]
    public sealed partial class WorldSyncDescriptor
    {
        public string? WorldName { get; set; }
        public string? GameModeType { get; set; }
        public string[] SceneNames { get; set; } = Array.Empty<string>();
    }

    [MemoryPackable]
    public sealed partial class PlayerTransformUpdate
    {
        public int ServerPlayerIndex { get; set; }
        public Guid TransformId { get; set; }
        public Vector3 Translation { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Velocity { get; set; }
        public Guid InstanceId { get; set; }
    }

    [MemoryPackable]
    public sealed partial class PlayerLeaveNotice
    {
        public int ServerPlayerIndex { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public Guid InstanceId { get; set; }
    }

    [MemoryPackable]
    public sealed partial class PlayerHeartbeat
    {
        public int ServerPlayerIndex { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public double TimestampUtc { get; set; }
        public Guid? InstanceId { get; set; }
    }

    [MemoryPackable]
    public sealed partial class ServerErrorMessage
    {
        /// <summary>
        /// HTTP-like status code (e.g., 400, 401, 426, 500).
        /// </summary>
        public int StatusCode { get; set; }
        /// <summary>
        /// Short title/phrase for the error (e.g., "Bad Request").
        /// </summary>
        public string Title { get; set; } = string.Empty;
        /// <summary>
        /// Human-readable detail.
        /// </summary>
        public string Detail { get; set; } = string.Empty;
        /// <summary>
        /// Optional client id this message targets. Empty/null means broadcast to all.
        /// </summary>
        public string? ClientId { get; set; }
        /// <summary>
        /// Optional player slot this message refers to.
        /// </summary>
        public int? ServerPlayerIndex { get; set; }
        /// <summary>
        /// Correlation/request id for tracing.
        /// </summary>
        public string? RequestId { get; set; }
        /// <summary>
        /// If true, the client should treat this as fatal and tear down the session.
        /// </summary>
        public bool Fatal { get; set; }
    }

    [MemoryPackable]
    public sealed partial class WorldLocator
    {
        /// <summary>
        /// Stable identifier for this world content. Maps to the load balancer instance ID.
        /// </summary>
        public Guid WorldId { get; set; }
        /// <summary>
        /// Friendly name (echoed back to clients).
        /// </summary>
        public string? Name { get; set; }
        /// <summary>
        /// Storage provider hint: azure, s3, or direct.
        /// </summary>
        public string Provider { get; set; } = "direct";
        /// <summary>
        /// Container/bucket or base URL path.
        /// </summary>
        public string? ContainerOrBucket { get; set; }
        /// <summary>
        /// Object/blob path under the container.
        /// </summary>
        public string? ObjectPath { get; set; }
        /// <summary>
        /// Full presigned/authorized URI for direct downloads.
        /// </summary>
        public string? DownloadUri { get; set; }
        /// <summary>
        /// Optional SAS/presigned token appended when constructing provider-specific URLs.
        /// </summary>
        public string? AccessToken { get; set; }
    }
}
