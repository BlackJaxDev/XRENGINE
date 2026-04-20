using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MemoryPack;
using XREngine.Animation;
using XREngine.Core.Files;

namespace XREngine;

[SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute is only intended to be used in application code or advanced source generator scenarios", Justification = "Published cooked asset serializers must register when the engine assembly loads.")]
internal static class EnginePublishedCookedAssetRegistryRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        PublishedCookedAssetRegistry.Register(
            typeof(AnimationClip),
            static asset => MemoryPackSerializer.Serialize(AnimationClipSerialization.CreateModel((AnimationClip)asset)),
            static (payload, assetType) => DeserializeAnimationAsset(payload, assetType));

        PublishedCookedAssetRegistry.Register(
            typeof(BlendTree1D),
            static asset => MemoryPackSerializer.Serialize((BlendTree1DSerializedModel)BlendTreeSerialization.CreateModel((BlendTree1D)asset)),
            static (payload, assetType) => DeserializeAnimationAsset(payload, assetType));

        PublishedCookedAssetRegistry.Register(
            typeof(BlendTree2D),
            static asset => MemoryPackSerializer.Serialize((BlendTree2DSerializedModel)BlendTreeSerialization.CreateModel((BlendTree2D)asset)),
            static (payload, assetType) => DeserializeAnimationAsset(payload, assetType));

        PublishedCookedAssetRegistry.Register(
            typeof(BlendTreeDirect),
            static asset => MemoryPackSerializer.Serialize((BlendTreeDirectSerializedModel)BlendTreeSerialization.CreateModel((BlendTreeDirect)asset)),
            static (payload, assetType) => DeserializeAnimationAsset(payload, assetType));

        PublishedCookedAssetRegistry.Register(
            typeof(AnimStateMachine),
            static asset => MemoryPackSerializer.Serialize(AnimStateMachineSerialization.CreateModel((AnimStateMachine)asset)),
            static (payload, assetType) => DeserializeAnimationAsset(payload, assetType));
    }

    private static MotionBase? DeserializeMotion(byte[] payload, Type assetType)
        => assetType == typeof(AnimationClip)
            ? DeserializeAnimationClip(payload)
            : assetType == typeof(BlendTree1D)
                ? DeserializeBlendTree1D(payload)
                : assetType == typeof(BlendTree2D)
                    ? DeserializeBlendTree2D(payload)
                    : assetType == typeof(BlendTreeDirect)
                        ? DeserializeBlendTreeDirect(payload)
                        : null;

    private static object? DeserializeAnimationAsset(byte[] payload, Type assetType)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(assetType);

        if (assetType == typeof(AnimStateMachine))
        {
            AnimStateMachineSerializedModel? model = MemoryPackSerializer.Deserialize<AnimStateMachineSerializedModel>(payload);
            AnimStateMachine stateMachine = new();
            AnimStateMachineSerialization.ApplyModel(stateMachine, model);
            return stateMachine;
        }

        return DeserializeMotion(payload, assetType);
    }

    private static AnimationClip DeserializeAnimationClip(byte[] payload)
    {
        AnimationClipSerializedModel? model = MemoryPackSerializer.Deserialize<AnimationClipSerializedModel>(payload);
        AnimationClip clip = new();
        AnimationClipSerialization.ApplyModel(clip, model);
        return clip;
    }

    private static BlendTree1D DeserializeBlendTree1D(byte[] payload)
        => BlendTreeSerialization.CreateRuntimeBlendTree(
            typeof(BlendTree1D),
            MemoryPackSerializer.Deserialize<BlendTree1DSerializedModel>(payload)) as BlendTree1D
            ?? throw new InvalidOperationException("Failed to deserialize published BlendTree1D asset.");

    private static BlendTree2D DeserializeBlendTree2D(byte[] payload)
        => BlendTreeSerialization.CreateRuntimeBlendTree(
            typeof(BlendTree2D),
            MemoryPackSerializer.Deserialize<BlendTree2DSerializedModel>(payload)) as BlendTree2D
            ?? throw new InvalidOperationException("Failed to deserialize published BlendTree2D asset.");

    private static BlendTreeDirect DeserializeBlendTreeDirect(byte[] payload)
        => BlendTreeSerialization.CreateRuntimeBlendTree(
            typeof(BlendTreeDirect),
            MemoryPackSerializer.Deserialize<BlendTreeDirectSerializedModel>(payload)) as BlendTreeDirect
            ?? throw new InvalidOperationException("Failed to deserialize published BlendTreeDirect asset.");
}