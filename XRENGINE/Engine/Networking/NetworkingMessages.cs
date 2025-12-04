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

    [MemoryPackable]
    public sealed partial class PlayerInputSnapshot
    {
        public int ServerPlayerIndex { get; set; }
        public CharacterPawnComponent.NetworkInputState Input { get; set; }
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
}
