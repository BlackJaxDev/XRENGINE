using System;
using XREngine.Rendering;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

[YamlTypeConverter]
public sealed class XRMaterialYamlTypeConverter : IWriteOnlyYamlTypeConverter
{
    [ThreadStatic]
    private static bool _skip;

    public bool Accepts(Type type)
    {
        if (_skip)
            return false;

        return type == typeof(XRMaterial);
    }

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        => throw new NotSupportedException($"{nameof(XRMaterialYamlTypeConverter)} is write-only; reading is handled by {nameof(XRAssetDeserializer)}.");

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is null)
        {
            emitter.Emit(new Scalar("~"));
            return;
        }

        if (value is not XRMaterial material)
            throw new YamlException($"Expected {nameof(XRMaterial)} but got '{value.GetType()}'.");

        if (TryWriteAsReference.TryEmitReference(emitter, material))
            return;

        bool previous = _skip;
        _skip = true;
        try
        {
            serializer(value, value.GetType());
        }
        finally
        {
            _skip = previous;
        }
    }
}
