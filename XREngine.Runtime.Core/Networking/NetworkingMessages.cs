using System;
using System.Numerics;
using MemoryPack;

namespace XREngine.Networking
{
    public enum HumanoidPosePacketKind : byte
    {
        Baseline,
        Delta
    }

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
        public int StatusCode { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string? ClientId { get; set; }
        public int? ServerPlayerIndex { get; set; }
        public string? RequestId { get; set; }
        public bool Fatal { get; set; }
    }

    [MemoryPackable]
    public sealed partial class WorldLocator
    {
        public Guid WorldId { get; set; }
        public string? Name { get; set; }
        public string Provider { get; set; } = "direct";
        public string? ContainerOrBucket { get; set; }
        public string? ObjectPath { get; set; }
        public string? DownloadUri { get; set; }
        public string? AccessToken { get; set; }
    }

    [MemoryPackable]
    public sealed partial class HumanoidPoseFrame
    {
        public HumanoidPosePacketKind Kind { get; set; }
        public ushort BaselineSequence { get; set; }
        public int AvatarCount { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }
}