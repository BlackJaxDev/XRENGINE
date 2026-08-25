using XREngine.Core.Files;

namespace XREngine.Animation;

internal sealed class BlendTreeCookedBinaryCodec : ICookedBinaryFeatureCodec
{
    public CookedBinarySerializationModuleInfo Info
        => new(1400, "BlendTrees", "BlendTree custom payload via serialized blend tree models.");

    public bool CanHandle(Type type)
        => BlendTreeCookedBinarySerializer.CanHandle(type);

    public void Write(CookedBinaryWriter writer, object value)
        => BlendTreeCookedBinarySerializer.Write(writer, (BlendTree)value);

    public object Read(Type targetType, CookedBinaryReader reader)
        => BlendTreeCookedBinarySerializer.Read(targetType, reader);

    public long CalculateSize(object value)
        => BlendTreeCookedBinarySerializer.CalculateSize((BlendTree)value);

    public object CreateSchemaModel(object value)
        => BlendTreeSerialization.CreateModel((BlendTree)value);

    public Type GetSchemaModelType(Type runtimeType)
    {
        if (typeof(BlendTree1D).IsAssignableFrom(runtimeType))
            return typeof(BlendTree1DSerializedModel);
        if (typeof(BlendTree2D).IsAssignableFrom(runtimeType))
            return typeof(BlendTree2DSerializedModel);
        if (typeof(BlendTreeDirect).IsAssignableFrom(runtimeType))
            return typeof(BlendTreeDirectSerializedModel);

        throw new NotSupportedException($"No serialized blend-tree model is registered for '{runtimeType.FullName}'.");
    }
}
