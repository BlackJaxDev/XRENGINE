using XREngine.Core.Files;

namespace XREngine.Animation;

internal sealed class AnimStateMachineCookedBinaryCodec : ICookedBinaryFeatureCodec
{
    public CookedBinarySerializationModuleInfo Info
        => new(1500, "AnimStateMachine", "AnimStateMachine custom payload via AnimStateMachineSerializedModel.");

    public bool CanHandle(Type type)
        => AnimStateMachineCookedBinarySerializer.CanHandle(type);

    public void Write(CookedBinaryWriter writer, object value)
        => AnimStateMachineCookedBinarySerializer.Write(writer, (AnimStateMachine)value);

    public object Read(Type targetType, CookedBinaryReader reader)
        => AnimStateMachineCookedBinarySerializer.Read(reader);

    public long CalculateSize(object value)
        => AnimStateMachineCookedBinarySerializer.CalculateSize((AnimStateMachine)value);

    public object CreateSchemaModel(object value)
        => AnimStateMachineSerialization.CreateModel((AnimStateMachine)value);

    public Type GetSchemaModelType(Type runtimeType)
        => typeof(AnimStateMachineSerializedModel);
}
