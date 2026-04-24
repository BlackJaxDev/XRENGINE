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
        public WorldAssetIdentity? ClientWorldAsset { get; set; }
        /// <summary>
        /// Optional session/room id supplied by an external control plane or direct launch command.
        /// </summary>
        public Guid? SessionId { get; set; }
        /// <summary>
        /// Optional opaque token supplied by an external control plane. XRENGINE does not issue it.
        /// </summary>
        public string? SessionToken { get; set; }
    }

    [MemoryPackable]
    public sealed partial class PlayerAssignment
    {
        public int ServerPlayerIndex { get; set; }
        public NetworkEntityId PlayerEntityId { get; set; }
        public Guid PawnId { get; set; }
        public Guid TransformId { get; set; }
        public WorldSyncDescriptor? World { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public Guid SessionId { get; set; }
        public bool IsAuthoritative { get; set; }
        public NetworkAuthorityLease? AuthorityLease { get; set; }
        public long ServerTickId { get; set; }
        public double ServerTimeUtc { get; set; }
    }

    [MemoryPackable(GenerateType.NoGenerate)]
    public partial interface IPawnInputSnapshot
    {
    }

    [MemoryPackable]
    public sealed partial class PlayerInputSnapshot
    {
        public int ServerPlayerIndex { get; set; }
        public NetworkEntityId EntityId { get; set; }
        public IPawnInputSnapshot? Input { get; set; }
        public double TimestampUtc { get; set; }
        public double ClientSendTimestampUtc { get; set; }
        public uint InputSequence { get; set; }
        public long ClientTickId { get; set; }
        public Guid SessionId { get; set; }
    }

    [MemoryPackable]
    public sealed partial class WorldSyncDescriptor
    {
        public string? WorldName { get; set; }
        public string? GameModeType { get; set; }
        public string[] SceneNames { get; set; } = Array.Empty<string>();
        public WorldAssetIdentity? Asset { get; set; }
    }

    [MemoryPackable]
    public sealed partial class PlayerTransformUpdate
    {
        public int ServerPlayerIndex { get; set; }
        public NetworkEntityId EntityId { get; set; }
        public Guid TransformId { get; set; }
        public Vector3 Translation { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Velocity { get; set; }
        public Guid SessionId { get; set; }
        public long ServerTickId { get; set; }
        public long BaselineTickId { get; set; }
        public uint ClientInputSequence { get; set; }
        public uint LastProcessedInputSequence { get; set; }
        public NetworkAuthorityMode AuthorityMode { get; set; } = NetworkAuthorityMode.None;
        public bool IsServerCorrection { get; set; }
        public double ServerTimestampUtc { get; set; }
    }

    [MemoryPackable]
    public sealed partial class PlayerLeaveNotice
    {
        public int ServerPlayerIndex { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public Guid SessionId { get; set; }
    }

    [MemoryPackable]
    public sealed partial class PlayerHeartbeat
    {
        public int ServerPlayerIndex { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public double TimestampUtc { get; set; }
        public double ClientSendTimestampUtc { get; set; }
        public long LastReceivedServerTickId { get; set; }
        public uint LastProcessedInputSequence { get; set; }
        public Guid? SessionId { get; set; }
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
    public sealed partial class HumanoidPoseFrame
    {
        public Guid SessionId { get; set; }
        public string SourceClientId { get; set; } = string.Empty;
        public NetworkReplicationChannel Channel { get; set; } = NetworkReplicationChannel.HumanoidPose;
        public HumanoidPosePacketKind Kind { get; set; }
        public ushort BaselineSequence { get; set; }
        public long ServerTickId { get; set; }
        public long BaselineTickId { get; set; }
        public uint FrameSequence { get; set; }
        public double ServerTimestampUtc { get; set; }
        public NetworkAuthorityMode AuthorityMode { get; set; } = NetworkAuthorityMode.None;
        public NetworkEntityId[] EntityIds { get; set; } = Array.Empty<NetworkEntityId>();
        public int AvatarCount { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }
}
