using XREngine.Core.Files;

namespace XREngine.Animation;

internal sealed class AnimationClipCookedBinaryCodec : ICookedBinaryFeatureCodec
{
    public CookedBinarySerializationModuleInfo Info
        => new(1300, "AnimationClip", "AnimationClip custom payload via AnimationClipSerializedModel.");

    public bool CanHandle(Type type)
        => AnimationClipCookedBinarySerializer.CanHandle(type);

    public void Write(CookedBinaryWriter writer, object value)
        => AnimationClipCookedBinarySerializer.Write(writer, (AnimationClip)value);

    public object Read(Type targetType, CookedBinaryReader reader)
        => AnimationClipCookedBinarySerializer.Read(reader);

    public long CalculateSize(object value)
        => AnimationClipCookedBinarySerializer.CalculateSize((AnimationClip)value);

    public object CreateSchemaModel(object value)
        => AnimationClipSerialization.CreateModel((AnimationClip)value);

    public Type GetSchemaModelType(Type runtimeType)
        => typeof(AnimationClipSerializedModel);
}
