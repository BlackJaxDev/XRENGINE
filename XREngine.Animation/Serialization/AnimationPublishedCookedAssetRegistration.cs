using MemoryPack;
using XREngine.Core.Files;
using XREngine.Data;

namespace XREngine.Animation;

/// <summary>Installs Animation-owned published cooked asset serializers.</summary>
public static class AnimationPublishedCookedAssetRegistration
{
    public static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(Register(
                typeof(AnimationClip),
                static asset => MemoryPackSerializer.Serialize(
                    AnimationClipSerialization.CreateModel((AnimationClip)asset))));
            leases.Add(Register(
                typeof(BlendTree1D),
                static asset => MemoryPackSerializer.Serialize(
                    (BlendTree1DSerializedModel)BlendTreeSerialization.CreateModel((BlendTree1D)asset))));
            leases.Add(Register(
                typeof(BlendTree2D),
                static asset => MemoryPackSerializer.Serialize(
                    (BlendTree2DSerializedModel)BlendTreeSerialization.CreateModel((BlendTree2D)asset))));
            leases.Add(Register(
                typeof(BlendTreeDirect),
                static asset => MemoryPackSerializer.Serialize(
                    (BlendTreeDirectSerializedModel)BlendTreeSerialization.CreateModel((BlendTreeDirect)asset))));
            leases.Add(Register(
                typeof(AnimStateMachine),
                static asset => MemoryPackSerializer.Serialize(
                    AnimStateMachineSerialization.CreateModel((AnimStateMachine)asset))));
        });

    private static IDisposable Register(
        Type assetType,
        PublishedCookedAssetSerializeDelegate serialize)
        => PublishedCookedAssetRegistry.Register(
            assetType,
            serialize,
            static (payload, type) => Deserialize(payload, type),
            "XREngine.Animation");

    private static object? Deserialize(byte[] payload, Type assetType)
    {
        if (assetType == typeof(AnimStateMachine))
        {
            AnimStateMachineSerializedModel? stateMachineModel =
                MemoryPackSerializer.Deserialize<AnimStateMachineSerializedModel>(payload);
            AnimStateMachine stateMachine = new();
            AnimStateMachineSerialization.ApplyModel(stateMachine, stateMachineModel);
            return stateMachine;
        }

        if (assetType == typeof(AnimationClip))
        {
            AnimationClipSerializedModel? clipModel =
                MemoryPackSerializer.Deserialize<AnimationClipSerializedModel>(payload);
            AnimationClip clip = new();
            AnimationClipSerialization.ApplyModel(clip, clipModel);
            return clip;
        }

        Type modelType = assetType == typeof(BlendTree1D)
            ? typeof(BlendTree1DSerializedModel)
            : assetType == typeof(BlendTree2D)
                ? typeof(BlendTree2DSerializedModel)
                : assetType == typeof(BlendTreeDirect)
                    ? typeof(BlendTreeDirectSerializedModel)
                    : throw new InvalidOperationException(
                        $"Animation published cooked asset type '{assetType.FullName}' has no owner registration.");
        object? model = modelType == typeof(BlendTree1DSerializedModel)
            ? MemoryPackSerializer.Deserialize<BlendTree1DSerializedModel>(payload)
            : modelType == typeof(BlendTree2DSerializedModel)
                ? MemoryPackSerializer.Deserialize<BlendTree2DSerializedModel>(payload)
                : MemoryPackSerializer.Deserialize<BlendTreeDirectSerializedModel>(payload);
        return BlendTreeSerialization.CreateRuntimeBlendTree(assetType, model);
    }

}
