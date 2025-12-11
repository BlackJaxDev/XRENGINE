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
    }

    [MemoryPackable]
    public sealed partial class PlayerLeaveNotice
    {
        public int ServerPlayerIndex { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public string? Reason { get; set; }
    }

    [MemoryPackable]
    public sealed partial class PlayerHeartbeat
    {
        public int ServerPlayerIndex { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public double TimestampUtc { get; set; }
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
}
