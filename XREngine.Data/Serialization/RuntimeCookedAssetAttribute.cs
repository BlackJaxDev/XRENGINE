namespace XREngine;

/// <summary>Selects a generated published-asset codec for a declared runtime asset type.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RuntimeCookedAssetAttribute(Type assetType, ERuntimeCookedAssetCodec codec) : Attribute
{
    public Type AssetType { get; } = assetType;
    public ERuntimeCookedAssetCodec Codec { get; } = codec;
}

public enum ERuntimeCookedAssetCodec
{
    MemoryPack,
    TextureStreaming,
    AnimationClipModel,
    BlendTree1DModel,
    BlendTree2DModel,
    BlendTreeDirectModel,
    AnimStateMachineModel,
    CookedBinaryMesh,
}
