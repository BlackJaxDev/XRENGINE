using System;
using XREngine.Rendering.Models;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

[YamlTypeConverter]
public sealed class ModelYamlTypeConverter : IWriteOnlyYamlTypeConverter
{
    [ThreadStatic]
    private static bool _skip;

    public bool Accepts(Type type)
        => !_skip && type == typeof(Model);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        => throw new NotSupportedException($"{nameof(ModelYamlTypeConverter)} is write-only; reading is handled by {nameof(XRAssetDeserializer)}.");

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is null)
        {
            emitter.Emit(new Scalar("~"));
            return;
        }

        if (value is not Model model)
            throw new YamlException($"Expected {nameof(Model)} but got '{value.GetType()}'.");

        if (TryWriteAsReference.TryEmitReference(emitter, model))
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
