using System;
using MemoryPack;
using XREngine.Animation;
using XREngine.Core.Files;

namespace XREngine;

internal static class MotionSerialization
{
    public static SerializedMotionModel? CreateModel(MotionBase? motion)
    {
        if (motion is null)
            return null;

        return new SerializedMotionModel
        {
            TypeName = motion.GetType().AssemblyQualifiedName,
            Payload = SerializeMotionPayload(motion)
        };
    }

    public static SerializedMotionModel? CreatePublishedModel(MotionBase? motion)
    {
        if (motion is null)
            return null;

        return new SerializedMotionModel
        {
            TypeName = motion.GetType().FullName,
            Payload = motion switch
            {
                AnimationClip clip when clip.GetType() == typeof(AnimationClip)
                    => MemoryPackSerializer.Serialize(AnimationClipSerialization.CreatePublishedModel(clip)),
                BlendTree1D tree when tree.GetType() == typeof(BlendTree1D)
                    => MemoryPackSerializer.Serialize(BlendTreeSerialization.CreatePublishedModel(tree)),
                BlendTree2D tree when tree.GetType() == typeof(BlendTree2D)
                    => MemoryPackSerializer.Serialize(BlendTreeSerialization.CreatePublishedModel(tree)),
                BlendTreeDirect tree when tree.GetType() == typeof(BlendTreeDirect)
                    => MemoryPackSerializer.Serialize(BlendTreeSerialization.CreatePublishedModel(tree)),
                MotionBricksLocomotionMotion motionBricks when motionBricks.GetType() == typeof(MotionBricksLocomotionMotion)
                    => MemoryPackSerializer.Serialize(CreateMotionBricksModel(motionBricks)),
                _ => throw new NotSupportedException($"Published motion type '{motion.GetType().FullName}' is not registered.")
            }
        };
    }

    public static MotionBase? CreatePublishedRuntimeMotion(SerializedMotionModel? model)
    {
        if (model is null)
            return null;
        if (model.Payload is null || model.Payload.Length == 0)
            throw new InvalidDataException($"Published motion '{model.TypeName}' has no payload.");

        if (model.TypeName == typeof(AnimationClip).FullName)
        {
            AnimationClipSerializedModel state = MemoryPackSerializer.Deserialize<AnimationClipSerializedModel>(model.Payload)
                ?? throw new InvalidDataException("Published animation clip motion has an empty payload.");
            AnimationClip clip = new();
            AnimationClipSerialization.ApplyPublishedModel(clip, state);
            return clip;
        }

        if (model.TypeName == typeof(BlendTree1D).FullName)
            return BlendTreeSerialization.CreatePublishedRuntimeBlendTree(
                typeof(BlendTree1D), MemoryPackSerializer.Deserialize<BlendTree1DSerializedModel>(model.Payload));
        if (model.TypeName == typeof(BlendTree2D).FullName)
            return BlendTreeSerialization.CreatePublishedRuntimeBlendTree(
                typeof(BlendTree2D), MemoryPackSerializer.Deserialize<BlendTree2DSerializedModel>(model.Payload));
        if (model.TypeName == typeof(BlendTreeDirect).FullName)
            return BlendTreeSerialization.CreatePublishedRuntimeBlendTree(
                typeof(BlendTreeDirect), MemoryPackSerializer.Deserialize<BlendTreeDirectSerializedModel>(model.Payload));
        if (model.TypeName == typeof(MotionBricksLocomotionMotion).FullName)
            return CreateRuntimeMotionBricks(
                MemoryPackSerializer.Deserialize<MotionBricksLocomotionMotionSerializedModel>(model.Payload));

        throw new InvalidDataException($"Published motion type '{model.TypeName}' is not registered.");
    }

    public static MotionBase? CreateRuntimeMotion(SerializedMotionModel? model)
    {
        if (model?.Payload is null || model.Payload.Length == 0 || string.IsNullOrWhiteSpace(model.TypeName))
            return null;

        Type motionType = AotRuntimeMetadataStore.ResolveType(model.TypeName)
            ?? throw new InvalidOperationException($"Failed to resolve motion type '{model.TypeName}'.");

        return DeserializeMotionPayload(motionType, model.Payload);
    }

    private static byte[] SerializeMotionPayload(MotionBase motion)
        => motion switch
        {
            AnimationClip clip => MemoryPackSerializer.Serialize(AnimationClipSerialization.CreateModel(clip)),
            BlendTree1D blendTree1D => MemoryPackSerializer.Serialize((BlendTree1DSerializedModel)BlendTreeSerialization.CreateModel(blendTree1D)),
            BlendTree2D blendTree2D => MemoryPackSerializer.Serialize((BlendTree2DSerializedModel)BlendTreeSerialization.CreateModel(blendTree2D)),
            BlendTreeDirect blendTreeDirect => MemoryPackSerializer.Serialize((BlendTreeDirectSerializedModel)BlendTreeSerialization.CreateModel(blendTreeDirect)),
            MotionBricksLocomotionMotion motionBricks => MemoryPackSerializer.Serialize(CreateMotionBricksModel(motionBricks)),
            _ => throw new NotSupportedException($"Unsupported motion type '{motion.GetType().FullName}'.")
        };

    private static MotionBase DeserializeMotionPayload(Type motionType, byte[] payload)
    {
        if (motionType == typeof(AnimationClip))
        {
            AnimationClipSerializedModel? model = MemoryPackSerializer.Deserialize<AnimationClipSerializedModel>(payload);
            AnimationClip clip = new();
            AnimationClipSerialization.ApplyModel(clip, model);
            return clip;
        }

        if (motionType == typeof(BlendTree1D))
        {
            BlendTree1DSerializedModel? model = MemoryPackSerializer.Deserialize<BlendTree1DSerializedModel>(payload);
            return BlendTreeSerialization.CreateRuntimeBlendTree(motionType, model)
                ?? throw new InvalidOperationException($"Failed to deserialize motion '{motionType.FullName}'.");
        }

        if (motionType == typeof(BlendTree2D))
        {
            BlendTree2DSerializedModel? model = MemoryPackSerializer.Deserialize<BlendTree2DSerializedModel>(payload);
            return BlendTreeSerialization.CreateRuntimeBlendTree(motionType, model)
                ?? throw new InvalidOperationException($"Failed to deserialize motion '{motionType.FullName}'.");
        }

        if (motionType == typeof(BlendTreeDirect))
        {
            BlendTreeDirectSerializedModel? model = MemoryPackSerializer.Deserialize<BlendTreeDirectSerializedModel>(payload);
            return BlendTreeSerialization.CreateRuntimeBlendTree(motionType, model)
                ?? throw new InvalidOperationException($"Failed to deserialize motion '{motionType.FullName}'.");
        }

        if (motionType == typeof(MotionBricksLocomotionMotion))
        {
            MotionBricksLocomotionMotionSerializedModel? model =
                MemoryPackSerializer.Deserialize<MotionBricksLocomotionMotionSerializedModel>(payload);
            return CreateRuntimeMotionBricks(model);
        }

        throw new NotSupportedException($"Unsupported motion type '{motionType.FullName}'.");
    }

    private static MotionBricksLocomotionMotionSerializedModel CreateMotionBricksModel(MotionBricksLocomotionMotion motion)
        => new()
        {
            Name = motion.Name,
            ModelProfileId = motion.ModelProfileId,
            LocomotionPresetId = motion.LocomotionPresetId,
            MoveXParameter = motion.MoveXParameter,
            MoveYParameter = motion.MoveYParameter,
            DesiredFacingYawParameter = motion.DesiredFacingYawParameter,
            StrafeModeParameter = motion.StrafeModeParameter,
            StyleParameter = motion.StyleParameter,
            IsGroundedParameter = motion.IsGroundedParameter,
            WalkSpeedMetersPerSecond = motion.WalkSpeedMetersPerSecond,
            RunSpeedMetersPerSecond = motion.RunSpeedMetersPerSecond,
            ReplanIntervalSeconds = motion.ReplanIntervalSeconds,
            InputReplanThreshold = motion.InputReplanThreshold,
            BakeOrientationIntoPose = motion.BakeOrientationIntoPose,
            BakePositionYIntoPose = motion.BakePositionYIntoPose,
            BakePositionXZIntoPose = motion.BakePositionXZIntoPose,
        };

    private static MotionBricksLocomotionMotion CreateRuntimeMotionBricks(MotionBricksLocomotionMotionSerializedModel? model)
    {
        if (model is null)
            throw new InvalidDataException("MotionBricks locomotion motion has an empty payload.");

        return new MotionBricksLocomotionMotion
        {
            Name = model.Name ?? string.Empty,
            ModelProfileId = model.ModelProfileId ?? "soma-rp-v1",
            LocomotionPresetId = model.LocomotionPresetId ?? "locomotion_bs2_directional",
            MoveXParameter = model.MoveXParameter ?? "MoveX",
            MoveYParameter = model.MoveYParameter ?? "MoveY",
            DesiredFacingYawParameter = model.DesiredFacingYawParameter ?? "DesiredFacingYaw",
            StrafeModeParameter = model.StrafeModeParameter ?? "StrafeMode",
            StyleParameter = model.StyleParameter ?? "LocomotionStyle",
            IsGroundedParameter = model.IsGroundedParameter ?? "IsGrounded",
            WalkSpeedMetersPerSecond = model.WalkSpeedMetersPerSecond,
            RunSpeedMetersPerSecond = model.RunSpeedMetersPerSecond,
            ReplanIntervalSeconds = model.ReplanIntervalSeconds,
            InputReplanThreshold = model.InputReplanThreshold,
            BakeOrientationIntoPose = model.BakeOrientationIntoPose,
            BakePositionYIntoPose = model.BakePositionYIntoPose,
            BakePositionXZIntoPose = model.BakePositionXZIntoPose,
        };
    }
}

[MemoryPackable]
internal sealed partial class SerializedMotionModel
{
    public string? TypeName { get; set; }

    public byte[]? Payload { get; set; }
}

[MemoryPackable]
internal sealed partial class MotionBricksLocomotionMotionSerializedModel
{
    public string? Name { get; set; }
    public string? ModelProfileId { get; set; }
    public string? LocomotionPresetId { get; set; }
    public string? MoveXParameter { get; set; }
    public string? MoveYParameter { get; set; }
    public string? DesiredFacingYawParameter { get; set; }
    public string? StrafeModeParameter { get; set; }
    public string? StyleParameter { get; set; }
    public string? IsGroundedParameter { get; set; }
    public float WalkSpeedMetersPerSecond { get; set; } = 1.5f;
    public float RunSpeedMetersPerSecond { get; set; } = 4.2f;
    public float ReplanIntervalSeconds { get; set; } = 4.0f / 30.0f;
    public float InputReplanThreshold { get; set; } = 0.15f;
    public bool BakeOrientationIntoPose { get; set; }
    public bool BakePositionYIntoPose { get; set; } = true;
    public bool BakePositionXZIntoPose { get; set; }
}
